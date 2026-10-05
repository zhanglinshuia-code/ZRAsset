using System;
using System.Threading;
using UnityEngine.SceneManagement;

namespace ZRAsset
{
    public enum SceneState { Waiting, LoadingBundles, LoadingScene, Ready, Unloading, Released, Failed, Canceled, AwaitingActivation }

    /// <summary>场景拥有独立的依赖使用凭证，只有真正卸载或加载失败时才交回。</summary>
    public sealed class SceneHandle
    {
        private readonly ResourceManager m_owner;
        private readonly ISceneBackend m_backend;
        private readonly BundleProvider[] m_bundles;
        private readonly ResourceOperationBase m_bundlesReady;
        private readonly CancellationTokenSource m_cancellation;
        private Scene m_scene;
        private ResourceOperationBase m_unloadTask;
        private readonly OperationCompletionSource<bool> m_readyToActivate = new();
        private ISceneLoadRequest m_request;
        private bool m_activationRequested;
        public string AssetPath { get; }
        public ResourceOperationBase<Scene> Operation { get; private set; }
        public SceneState State { get; private set; } = SceneState.Waiting;
        public bool IsReleased { get; private set; }
        public Exception Error { get; private set; }
        public ResourceOperationBase ReadyToActivate
        {
            get
            {
                return m_readyToActivate.Operation;
            }
        }

        /// <summary>Unity 场景请求进度，不包含之前的 Bundle 下载；等待激活时停在 0.9。</summary>
        public float Progress { get { m_owner.CheckThread(); return State == SceneState.Ready ? 1 : m_request?.Progress ?? 0; } }
        public Scene Scene
        {
            get
            {
                m_owner.CheckThread();
                return IsReleased
                    ? throw new ObjectDisposedException(nameof(SceneHandle))
                    : State != SceneState.Ready ? throw new InvalidOperationException("Scene is not ready.") : m_scene;
            }
        }

        internal SceneHandle(ResourceManager owner, ISceneBackend backend, string path,
            BundleProvider[] bundles, ResourceOperationBase bundlesReady, CancellationToken token)
        {
            m_owner = owner;
            m_backend = backend;
            m_bundles = bundles;
            m_bundlesReady = bundlesReady;
            AssetPath = path;
            m_cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

        }

        internal void Start(ResourceOperationBase previous, ResourceSceneLoadOptions options)
        {
            m_activationRequested = options.ActivateOnLoad;
            Operation = LoadAsync(previous, options);

        }

        private async ResourceOperationBase<Scene> LoadAsync(ResourceOperationBase previous, ResourceSceneLoadOptions options)
        {
            var started = ResourceTelemetry.StartTimer();
            Exception failure = null;
            try {
                // 前一个场景请求失败不能阻断整个队列；当前请求仍有机会继续。
                try { await ResourceOperations.WaitAsync(previous, m_cancellation.Token); }
                catch when (!m_cancellation.IsCancellationRequested) { }
                State = SceneState.LoadingBundles;
                await ResourceOperations.WaitAsync(m_bundlesReady, m_cancellation.Token);
                m_cancellation.Token.ThrowIfCancellationRequested();
                State = SceneState.LoadingScene;
                // 提交后不可撤回：Unity 不支持取消已开始的场景切换，Single 尤其需要保留结果。
                if (m_backend is IControlledSceneBackend controlled) {
                    m_request = controlled.BeginLoadScene(AssetPath, options, m_cancellation.Token);
                    if (m_activationRequested) {
                        m_request.Activate();
                    }

                    try { await m_request.ReadyToActivate; }
                    catch {
                        // Ready 阶段失败不能跳过后端原生请求的 finally 排空。
                        try { await m_request.Operation; } catch { }
                        throw;
                    }
                    if (!m_activationRequested && !m_cancellation.IsCancellationRequested && !m_request.Operation.IsDone) {
                        State = SceneState.AwaitingActivation;
                    }

                    m_readyToActivate.TrySetResult(true);
                    m_scene = await m_request.Operation;
                }
                else {
                    m_scene = await m_backend.LoadSceneAsync(AssetPath, options.Mode);
                    m_readyToActivate.TrySetResult(true);
                }
                State = SceneState.Ready;
                m_owner.PollLifetimes();
                return m_scene;
            }
            catch (OperationCanceledException error) { failure = error; m_readyToActivate.TrySetException(error); Finish(SceneState.Canceled); throw; }
            catch (Exception exception) { failure = exception; m_readyToActivate.TrySetException(exception); Error = exception; Finish(SceneState.Failed); throw; }
            finally {
                ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.SceneLoad, m_owner.PackageName, m_owner.PackageVersion,
                    AssetPath, ResourceFailure.FromException(failure, ResourceStage.Load), ResourceTelemetry.Elapsed(started)));
            }
        }

        public ResourceOperationBase<Scene> ActivateAsync()
        {
            m_owner.CheckThread();
            if (IsReleased) {
                throw new ObjectDisposedException(nameof(SceneHandle));
            }

            m_activationRequested = true;
            m_request?.Activate();
            if (State == SceneState.AwaitingActivation) {
                State = SceneState.LoadingScene;
            }

            return Operation;
        }

        public bool SetActiveScene() { m_owner.CheckThread(); return SceneManager.SetActiveScene(Scene); }

        /// <summary>排队中则取消；已提交则等加载完成后卸载。卸载失败时保留引用并允许重试。</summary>
        public ResourceOperationBase UnloadAsync()
        {
            m_owner.CheckThread();
            if (m_unloadTask != null && !m_unloadTask.IsFaulted) {
                return m_unloadTask;
            }

            if (IsReleased) {
                return ResourceOperationBase.CompletedOperation;
            }

            m_activationRequested = true;
            m_request?.Activate(); // 延迟激活阶段也必须先放行，才能排空并卸载原生场景。
            m_cancellation.Cancel();
            m_unloadTask = UnloadCoreAsync();

            return m_unloadTask;
        }

        private async ResourceOperationBase UnloadCoreAsync()
        {
            try { await Operation; }
            catch { return; } // 加载失败或取消时已交回场景的 Bundle 使用凭证。
            if (IsReleased) {
                return;
            }

            State = SceneState.Unloading;
            try {
                await m_backend.UnloadSceneAsync(m_scene);
                Finish(SceneState.Released);
            }
            catch (Exception exception) {
                // 卸载失败仍保留依赖，例如先加载替代场景后可以再次尝试卸载最后一个场景。
                Error = exception;
                State = SceneState.Ready;
                throw;
            }
        }

        internal void Poll()
        {
            if (!IsReleased && State == SceneState.Ready && (!m_scene.IsValid() || !m_scene.isLoaded)) {
                Finish(SceneState.Released);
            }
        }

        private void Finish(SceneState state)
        {
            if (IsReleased) {
                return;
            }

            IsReleased = true;
            State = state;
            m_cancellation.Dispose();
            m_owner.ReleaseScene(this, m_bundles);
        }
    }
}

using System;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZRAsset
{
    public sealed class ResourceSceneLoadOptions
    {
        public LoadSceneMode Mode { get; }
        public bool ActivateOnLoad { get; }
        public int Priority { get; }
        public LocalPhysicsMode LocalPhysicsMode { get; }
        public ResourceSceneLoadOptions(LoadSceneMode mode = LoadSceneMode.Additive, bool activateOnLoad = true,
            int priority = 0, LocalPhysicsMode localPhysicsMode = LocalPhysicsMode.None)
        {
            if (mode != LoadSceneMode.Additive && mode != LoadSceneMode.Single) {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            if ((localPhysicsMode & ~(LocalPhysicsMode.Physics2D | LocalPhysicsMode.Physics3D)) != 0) {
                throw new ArgumentOutOfRangeException(nameof(localPhysicsMode));
            }

            Mode = mode; ActivateOnLoad = activateOnLoad; Priority = priority; LocalPhysicsMode = localPhysicsMode;
        }
    }

    public interface ISceneLoadRequest
    {
        ResourceOperationBase<Scene> Operation { get; }
        ResourceOperationBase ReadyToActivate { get; }
        float Progress { get; }
        void Activate();
    }

    /// <summary>提交前取消可以撤回；原生提交后的取消必须放行激活并保留场景所有权，不能遗留暂停的 Unity 队列。</summary>
    public interface IControlledSceneBackend
    {
        ISceneLoadRequest BeginLoadScene(string path, ResourceSceneLoadOptions options, CancellationToken cancellationToken);
    }

    /// <summary>真实和 Editor 模拟场景共用的原生请求包装。全局门限串行提交，避免多管理器相互阻塞。</summary>
    public sealed class UnitySceneLoadRequest: ISceneLoadRequest
    {
        private static readonly OperationSemaphore s_nativeGate = new(1, 1);
        private static UnitySceneLoadRequest s_paused;
        private readonly OperationCompletionSource<bool> m_ready = new();
        private readonly CancellationToken m_token;
        private AsyncOperation m_native;
        private bool m_activate;
        public ResourceOperationBase<Scene> Operation { get; }
        public ResourceOperationBase ReadyToActivate
        {
            get
            {
                return m_ready.Operation;
            }
        }

        public float Progress
        {
            get
            {
                return m_native?.progress ?? 0;
            }
        }

        public UnitySceneLoadRequest(string path, ResourceSceneLoadOptions options, Func<AsyncOperation> startNative,
            CancellationToken cancellationToken = default)
        {
            if (options == null) {
                throw new ArgumentNullException(nameof(options));
            }

            if (startNative == null) {
                throw new ArgumentNullException(nameof(startNative));
            }

            m_activate = options.ActivateOnLoad; m_token = cancellationToken;
            Operation = RunAsync(path, options, startNative);
        }

        public void Activate()
        {
            if (!OperationSystem.IsMainThread) {
                throw new InvalidOperationException("场景激活必须在 Unity 主线程调用。");
            }

            m_activate = true;
            if (m_native != null && !m_native.isDone) {
                m_native.allowSceneActivation = true;
            }

            if (ReferenceEquals(s_paused, this)) {
                s_paused = null;
            }
        }

        public static void RequireUnblockedQueue()
        {
            if (s_paused != null && !s_paused.m_activate && !s_paused.m_token.IsCancellationRequested) {
                throw new InvalidOperationException("场景正在等待激活，请先 ActivateAsync 或 UnloadAsync 该场景，再重试当前操作。");
            }
        }

        private async ResourceOperationBase<Scene> RunAsync(string path, ResourceSceneLoadOptions options, Func<AsyncOperation> startNative)
        {
            var held = false;
            Scene loaded = default;
            void OnLoaded(Scene scene, LoadSceneMode _) { if (string.Equals(scene.path, path, StringComparison.OrdinalIgnoreCase)) { loaded = scene; } }
            try {
                if (!Application.isPlaying) {
                    throw new InvalidOperationException("Scene loading requires Play Mode.");
                }

                await s_nativeGate.WaitAsync(m_token); held = true;
                m_token.ThrowIfCancellationRequested();
                if (SceneManager.GetSceneByPath(path).isLoaded) {
                    throw new InvalidOperationException("Scene already loaded outside this request: " + path);
                }

                SceneManager.sceneLoaded += OnLoaded;
                m_native = startNative() ?? throw new InvalidOperationException("Scene load did not start: " + path);
                m_native.priority = options.Priority;
                m_native.allowSceneActivation = m_activate;
                if (!m_activate) {
                    s_paused = this;
                }

                while (!m_native.isDone) {
                    // 取消可能来自工作线程；这里只在主线程修改 Unity 原生请求。
                    if (m_token.IsCancellationRequested && !m_activate) {
                        Activate();
                    }

                    if (m_native.progress >= 0.9f) {
                        m_ready.TrySetResult(true);
                    }

                    await ResourceOperationBase.Yield();
                }
                if (!loaded.IsValid()) {
                    loaded = SceneManager.GetSceneByPath(path);
                }

                if (!loaded.IsValid() || !loaded.isLoaded) {
                    throw new InvalidOperationException("Scene did not load: " + path);
                }

                m_ready.TrySetResult(true);
                return loaded;
            }
            catch (Exception error) { m_ready.TrySetException(error); throw; }
            finally {
                // 原生提交后的异常也必须放行队列并等待收尾。
                if (m_native != null && !m_native.isDone) {
                    m_native.allowSceneActivation = true;
                    await UnityOperations.WaitAsync(m_native);
                }
                SceneManager.sceneLoaded -= OnLoaded;
                if (ReferenceEquals(s_paused, this)) {
                    s_paused = null;
                }

                if (held) {
                    s_nativeGate.Release();
                }
            }
        }
    }
}

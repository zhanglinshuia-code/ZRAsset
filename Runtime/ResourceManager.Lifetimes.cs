using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZRAsset
{
    public sealed partial class ResourceManager
    {
        private readonly HashSet<InstanceHandle> m_instances = new();
        private readonly Dictionary<string, SceneHandle> m_scenes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ISceneBackend m_sceneBackend;
        private ResourceOperationBase m_sceneQueue = ResourceOperationBase.CompletedOperation;
        private readonly List<InstanceHandle> m_instanceSnapshot = new();
        private readonly List<SceneHandle> m_sceneSnapshot = new();
        private bool m_pollingLifetimes;

        public InstanceHandle InstantiateAsync(string address, Transform parent, bool worldPositionStays,
            CancellationToken cancellationToken)
        {
            return InstantiateAsync(address, parent, worldPositionStays, cancellationToken, 0);
        }

        public SceneHandle LoadSceneAsync(string address, LoadSceneMode mode,
                    CancellationToken cancellationToken)
        {
            return LoadSceneAsync(address, mode, cancellationToken, 0);
        }

        /// <summary>加载 Prefab 并创建独立实例；实例句柄内部持有资源凭证，销毁实例后自动交回。</summary>
        public InstanceHandle InstantiateAsync(string address, Transform parent = null, bool worldPositionStays = false,
            CancellationToken cancellationToken = default, int priority = 0)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            CheckThread();
            if (m_closing) {
                throw new ObjectDisposedException(nameof(ResourceManager));
            }

            if (!Application.isPlaying) {
                throw new InvalidOperationException("Instance creation requires Play Mode.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var handle = new InstanceHandle(this, cancellationToken);
            m_instances.Add(handle);
            handle.Start(address, parent, worldPositionStays, priority);
            return handle;
        }

        /// <summary>默认叠加加载；按请求顺序提交场景切换，同一路径只允许一个活跃场景句柄。</summary>
        public SceneHandle LoadSceneAsync(string address, LoadSceneMode mode = LoadSceneMode.Additive,
            CancellationToken cancellationToken = default, int priority = 0)
        {
            return LoadSceneAsync(address, new ResourceSceneLoadOptions(mode, priority: priority), cancellationToken);
        }

        public SceneHandle LoadSceneAsync(string address, ResourceSceneLoadOptions options, CancellationToken cancellationToken = default)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            CheckThread();
            if (m_closing) {
                throw new ObjectDisposedException(nameof(ResourceManager));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (options == null) {
                throw new ArgumentNullException(nameof(options));
            }

            if (!(m_sceneBackend is IControlledSceneBackend) && (!options.ActivateOnLoad || options.Priority != 0 || options.LocalPhysicsMode != LocalPhysicsMode.None)) {
                throw new NotSupportedException("当前场景后端不支持延迟激活、优先级或独立物理场景。");
            }

            if (address == null || !m_locations.TryGetValue(address, out AssetInfo info)) {
                throw new KeyNotFoundException($"Unknown scene address: {address}");
            }

            if (info.Kind != ResourceKind.Scene) {
                throw new InvalidOperationException("Address is not a scene.");
            }

            PollLifetimes();
            if (m_scenes.ContainsKey(info.AssetPath)) {
                throw new InvalidOperationException($"A handle already owns this scene. Unload it before loading again: {info.AssetPath}");
            }

            var closure = m_dependencyGraph.GetAssetClosure(info);
            BundleProvider[] bundles = closure.Select(name => AcquireBundle(name, false, options.Priority)).ToArray();
            var ready = ResourceOperationBase.WhenAll(bundles.Select(b => b.Operation));
            var handle = new SceneHandle(this, m_sceneBackend, info.AssetPath, bundles, ready, cancellationToken);
            m_scenes.Add(info.AssetPath, handle);
            ResourceOperationBase previous = m_sceneQueue.IsDone ? ResourceOperationBase.CompletedOperation : m_sceneQueue;
            handle.Start(previous, options);
            // 队列中间请求取消时，后续请求仍必须等待前驱完成，不能插队触发场景切换。
            m_sceneQueue = ResourceOperationBase.WhenAll(previous, handle.Operation);

            return handle;
        }

        internal void Forget(InstanceHandle handle)
        {
            m_instances.Remove(handle);
        }

        internal void ReleaseScene(SceneHandle handle, BundleProvider[] bundles)
        {
            m_scenes.Remove(handle.AssetPath);
            foreach (BundleProvider bundle in bundles) {
                ReleaseBundle(bundle);
            }
        }

        internal void PollLifetimes()
        {
            if (m_pollingLifetimes) {
                return;
            }

            m_pollingLifetimes = true;
            try {
                // 复用快照容量，避免每帧 ToArray 分配；Poll 可以安全移除原集合中的对象。
                foreach (InstanceHandle instance in m_instances) {
                    m_instanceSnapshot.Add(instance);
                }

                foreach (SceneHandle scene in m_scenes.Values) {
                    m_sceneSnapshot.Add(scene);
                }

                foreach (InstanceHandle instance in m_instanceSnapshot) {
                    instance.Poll();
                }

                foreach (SceneHandle scene in m_sceneSnapshot) {
                    scene.Poll();
                }
            }
            finally { m_instanceSnapshot.Clear(); m_sceneSnapshot.Clear(); m_pollingLifetimes = false; }
        }
    }
}

using System;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>位置与旋转使用世界坐标；未指定时沿用 Instantiate 的 parent/worldPositionStays 语义。</summary>
    public readonly struct ResourceInstantiateOptions
    {
        public Transform Parent { get; }
        public bool WorldPositionStays { get; }
        public Vector3? Position { get; }
        public Quaternion? Rotation { get; }
        /// <summary>null 保留预制体状态；false 保证创建过程中不会触发 OnEnable。</summary>
        public bool? Active { get; }

        public ResourceInstantiateOptions(Transform parent = null, bool worldPositionStays = false,
            Vector3? position = null, Quaternion? rotation = null, bool? active = null)
        {
            Parent = parent;
            WorldPositionStays = worldPositionStays;
            Position = position;
            Rotation = rotation;
            Active = active;
        }
    }

    public sealed partial class ResourceManager
    {
        public InstanceHandle InstantiateAsync(string address, ResourceInstantiateOptions options,
            CancellationToken cancellationToken = default, int priority = 0)
        {
            InstanceHandle handle = CreateInstanceHandle(cancellationToken);
            handle.Start(address, options, priority);
            return handle;
        }

        public InstanceHandle InstantiateSync(string address, ResourceInstantiateOptions options = default)
        {
            InstanceHandle handle = CreateInstanceHandle(default);
            handle.StartSync(address, options);
            return handle;
        }

        private InstanceHandle CreateInstanceHandle(CancellationToken token)
        {
            CheckThread();
            if (m_closing) { throw new ObjectDisposedException(nameof(ResourceManager)); }
            if (!Application.isPlaying) { throw new InvalidOperationException("Instance creation requires Play Mode."); }
            token.ThrowIfCancellationRequested();
            var handle = new InstanceHandle(this, token);
            m_instances.Add(handle);
            return handle;
        }
    }

    public sealed partial class ResourcePackage
    {
        public InstanceHandle InstantiateAsync(string address, ResourceInstantiateOptions options,
            CancellationToken cancellationToken = default, int priority = 0)
        {
            return Resources.InstantiateAsync(address, options, cancellationToken, priority);
        }

        public InstanceHandle InstantiateSync(string address, ResourceInstantiateOptions options = default)
        {
            return Resources.InstantiateSync(address, options);
        }
    }
}

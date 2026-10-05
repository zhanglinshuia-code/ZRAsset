using System;
using System.Collections.Generic;

namespace ZRAsset
{
    public sealed partial class ResourceManager
    {
        private readonly HashSet<AssetProvider> m_unusedAssets = new();
        private readonly HashSet<BundleProvider> m_unusedBundles = new();
        private readonly HashSet<RawFileProvider> m_unusedRawFiles = new();

        private void ReleaseBundle(BundleProvider bundle)
        {
            if (--bundle.References == 0) { m_unusedBundles.Add(bundle); }
        }

        private bool m_autoUnload;
        private AutoUnloadOperation m_autoUnloadOperation;
        public bool AutoUnloadUnused
        {
            get
            {
                return m_autoUnload;
            }

            set
            {
                CheckThread(); if (m_closing) {
                    throw new ObjectDisposedException(nameof(ResourceManager));
                }

                m_autoUnload = value;
                if (value && (m_autoUnloadOperation == null || m_autoUnloadOperation.IsDone)) {
                    m_autoUnloadOperation = OperationSystem.Start(new AutoUnloadOperation(this));
                }
            }
        }
        /// <summary>回收零引用资源；可等待 Unity 底层卸载完成。持有中的凭证继续有效。</summary>
        public async ResourceOperationBase UnloadUnusedAsync(bool waitForEngine = false)
        {
            CheckThread(); if (m_closing) {
                throw new ObjectDisposedException(nameof(ResourceManager));
            }

            UnloadUnused(true);
            if (waitForEngine) {
                await UnityOperations.WaitAsync(UnityEngine.Resources.UnloadUnusedAssets());
            }
        }
        private sealed class AutoUnloadOperation: ResourceOperationBase
        {
            private readonly WeakReference<ResourceManager> m_manager;
            internal AutoUnloadOperation(ResourceManager manager) { m_manager = new WeakReference<ResourceManager>(manager); }
            protected override void OnUpdate()
            {
                if (!m_manager.TryGetTarget(out ResourceManager target) || target.m_closing || !target.m_autoUnload) { Succeed(); return; }
                target.UnloadUnused();
            }
        }
    }
}

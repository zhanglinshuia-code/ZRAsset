using System.Linq;

namespace ZRAsset
{
    public sealed partial class ResourceManager
    {
        private ResourceOperationBase m_forceDisposal;
        private UnloadAllOperation m_unloadAll;
        private bool m_shutdownRequested;
#if UNITY_EDITOR
        internal async ResourceOperationBase ShutdownEditorSessionAsync()
        {
            m_closing = true;
            m_shutdownRequested = true;
            if (m_unloadAll != null && !m_unloadAll.IsDone) {
                try { await m_unloadAll; } catch { }
            }

            await ForceDisposeAsync();
        }
#endif

        /// <summary>
        /// 撤销全部 Handle，关闭作用域、实例、场景及 RawFile 流；保留管理器、清单和文件系统供再次加载。
        /// 业务先停止使用旧对象，并等待完成。失败保留依赖及加载屏障，可重试此方法或显式强制关闭。
        /// </summary>
        public ResourceOperationBase UnloadAllAssetsAsync()
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            return UnloadAllAssetsAsync(false);
        }

        /// <summary>可选等待 Unity.UnloadUnusedAssets；等待期间保持加载屏障，进度包含引擎回收阶段。</summary>
        public ResourceOperationBase UnloadAllAssetsAsync(bool waitForEngine)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            CheckThread();
            if (m_disposeTask != null || m_forceDisposal != null || m_disposalReservations != 0 || m_shutdownRequested) {
                throw new System.ObjectDisposedException(nameof(ResourceManager));
            }

            if (m_unloadAll != null && !m_unloadAll.IsDone) {
                m_unloadAll.WaitForEngine |= waitForEngine;
                return m_unloadAll;
            }
            m_closing = true;
            m_unloadAll = new UnloadAllOperation(this, waitForEngine);
            return OperationSystem.Start(m_unloadAll);
        }

        private sealed class UnloadAllOperation: ResourceOperationBase
        {
            private readonly ResourceManager m_owner;
            private ResourceOperationBase m_resources, m_engine;
            internal bool WaitForEngine;

            internal UnloadAllOperation(ResourceManager owner, bool waitForEngine)
            {
                m_owner = owner;
                WaitForEngine = waitForEngine;
            }

            protected override void OnUpdate()
            {
                m_resources ??= m_owner.UnloadAllCoreAsync();
                if (!m_resources.IsDone) { Progress = 0.5f * m_resources.Progress; return; }
                m_resources.GetAwaiter().GetResult();
                if (WaitForEngine) {
                    m_engine ??= UnityOperations.WaitAsync(UnityEngine.Resources.UnloadUnusedAssets());
                    Progress = 0.5f + (0.5f * m_engine.Progress);
                    if (!m_engine.IsDone) { return; }
                    m_engine.GetAwaiter().GetResult();
                }
                if (!m_owner.m_shutdownRequested && m_owner.m_disposalReservations == 0 &&
                    m_owner.m_disposeTask == null && m_owner.m_forceDisposal == null) {
                    m_owner.m_closing = false;
                    if (m_owner.m_autoUnload) { m_owner.AutoUnloadUnused = true; }
                }
                Succeed();
            }
        }

        private async ResourceOperationBase UnloadAllCoreAsync()
        {
            await ReleaseOwnedResourcesAsync();
            // 不关闭文件系统；等待既有准备和不可取消的 Unity 请求，防止新旧 Provider 交叠。
            try {
                await ResourceOperationBase.WhenAll(m_assetCache.Values.Select(a => (ResourceOperationBase)a.Operation)
                    .Concat(m_bundleCache.Values.Select(b => (ResourceOperationBase)b.Operation))
                    .Concat(m_rawFiles.Values.Select(f => (ResourceOperationBase)f.Operation)).Concat(m_pendingDownloads.ToArray()));
            }
            catch { /* 失败的加载仍须回收，其原操作保留错误。 */ }
            UnloadUnused(true);
        }

        /// <summary>
        /// 显式终止整个管理器：关闭作用域、销毁实例、卸载场景，撤销所有资源凭证并关闭 RawFile 流。
        /// 必须先停止业务使用资源并 await 完成；已经保存的 Unity 对象引用随 Bundle 卸载失效。
        /// 原生卸载失败会保留依赖并允许再次调用，不能在失败后恢复加载。
        /// </summary>
        public ResourceOperationBase ForceDisposeAsync()
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            CheckThread();
            return m_unloadAll != null && !m_unloadAll.IsDone
                ? throw new System.InvalidOperationException("请先等待全部资源卸载完成，再关闭管理器。")
                : m_forceDisposal != null && !m_forceDisposal.IsFaulted
                ? m_forceDisposal
                : m_disposeTask != null && !m_disposeTask.IsFaulted
                ? m_disposeTask
                : m_disposalReservations != 0 ? throw new System.InvalidOperationException("管理器正在参与整组关闭检查。") : BeginDisposal(true);
        }

        private async ResourceOperationBase ForceDisposeCoreAsync()
        {
            await ReleaseOwnedResourcesAsync();
            // Drain 等待在途 Provider 与文件读取完成，再统一释放底层对象和流。
            await DrainAsync();
        }

        private async ResourceOperationBase ReleaseOwnedResourcesAsync()
        {
            await ResourceOperationBase.Yield();
            PollLifetimes();
            ResourceScope[] scopes = m_scopes?.ToArray() ?? System.Array.Empty<ResourceScope>();
            SceneHandle[] sceneHandles = m_scenes.Values.ToArray();
            InstanceHandle[] instanceHandles = m_instances.ToArray();
            await ResourceOperationBase.WhenAll(scopes.Select(scope => scope.DisposeAsync())
                .Concat(sceneHandles.Select(scene => scene.UnloadAsync()))
                .Concat(instanceHandles.Select(instance => instance.DestroyAsync())));
            foreach (AssetProvider provider in m_assetCache.Values) {
                provider.Revoked = true;
                provider.References = 0;
                m_unusedAssets.Add(provider);
            }
            foreach (RawFileProvider provider in m_rawFiles.Values) {
                provider.Revoked = true;
                provider.References = 0;
                m_unusedRawFiles.Add(provider);
            }
        }
    }
}

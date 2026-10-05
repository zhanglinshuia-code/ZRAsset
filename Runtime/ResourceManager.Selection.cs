using System;
using System.Collections.Generic;
using System.Threading;

namespace ZRAsset
{
    /// <summary>可选只读文件规划能力；检查不下载、不复制、不修复文件。</summary>
    public interface IResourceFileInspector
    {
        ResourceOperationBase<ResourcePreparationBundle> InspectAsync(BundleInfo info, CancellationToken cancellationToken = default);
    }

    public sealed partial class ResourceManager
    {
        private ResourceCatalog m_contentCatalog;
        public ResourceCatalog Catalog
        {
            get
            {
                CheckThread();
                return m_closing
                    ? throw new ObjectDisposedException(nameof(ResourceManager))
                    : (m_contentCatalog ??= ResourceCatalog.CreateOwned(OwnedManifest, m_locationMatching));
            }
        }
        private ResourceOperationBase<ResourceCatalog> m_catalogPreparation;

        /// <summary>Shares one catalog build. Cancellation stops only this caller's wait; closing waits for the build to drain.</summary>
        public ResourceOperationBase<ResourceCatalog> WarmupCatalogAsync(CancellationToken cancellationToken = default)
        {
            CheckThread();
            if (m_closing) {
                throw new ObjectDisposedException(nameof(ResourceManager));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (m_contentCatalog != null) {
                return ResourceOperationBase.FromResult(m_contentCatalog);
            }

            if (m_catalogPreparation == null || m_catalogPreparation.IsFaulted || m_catalogPreparation.IsCanceled) {
                m_catalogPreparation = TrackContentOperation<ResourceCatalog>(async () =>
                {
                    ResourceCatalog prepared = await ResourceCatalog.CreateOwnedAsync(OwnedManifest, m_locationMatching);
                    return m_closing ? throw new OperationCanceledException("资源管理器正在关闭。") : (m_contentCatalog ??= prepared);
                });
            }

            return cancellationToken.CanBeCanceled ? WaitForCatalogAsync(m_catalogPreparation, cancellationToken) : m_catalogPreparation;
        }

        private static async ResourceOperationBase<ResourceCatalog> WaitForCatalogAsync(ResourceOperationBase<ResourceCatalog> source,
            CancellationToken token)
        {
            await ResourceOperations.WaitAsync(source, token);
            return source.Result;
        }

        public ResourceOperationBase<ResourceSelectionResult> SelectAsync(ResourceSelection selection, CancellationToken cancellationToken = default)
        {
            CheckThread();
            if (m_closing) {
                throw new ObjectDisposedException(nameof(ResourceManager));
            }

            if (selection == null) {
                throw new ArgumentNullException(nameof(selection));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return TrackContentOperation<ResourceSelectionResult>(async () =>
            {
                ResourceCatalog index = await WarmupCatalogAsync(cancellationToken);
                return await index.SelectAsync(selection, cancellationToken);
            });
        }

        public ResourceAssetInfo GetAssetInfo(string address)
        {
            return Catalog.GetAssetInfo(address);
        }

        public ResourceAssetInfo GetAssetInfoByGuid(string guid)
        {
            return Catalog.GetAssetInfoByGuid(guid);
        }

        public ResourceSelectionResult Select(ResourceSelection selection)
        {
            return Catalog.Select(selection);
        }

        /// <summary>校验当前版本所选依赖的实际来源和缺失字节量，不打开 Unity 对象。</summary>
        public ResourceOperationBase<ResourcePreparationPlan> PlanSelectionAsync(ResourceSelection selection, CancellationToken cancellationToken = default)
        {
            CheckThread();
            if (m_closing) {
                throw new ObjectDisposedException(nameof(ResourceManager));
            }

            cancellationToken.ThrowIfCancellationRequested();
            ValidateSelectionInput(selection);
            return FileSystem is not IResourceFileInspector inspector
                ? throw new NotSupportedException("当前文件系统不支持只读下载规划。")
                : TrackContentOperation<ResourcePreparationPlan>(async () =>
            {
                ResourceSelectionResult selected = await SelectAsync(selection, cancellationToken);
                ResourcePreparationBundle[] result = await ResourceWorkBatch.MapAsync(selected.BundleNames, 3,
                    (name, token) =>
                    {
                        return m_closing
                            ? throw new OperationCanceledException("资源管理器正在关闭。", token)
                            : inspector.InspectAsync(ResourceManifest.CopyBundle(m_catalog[name]), token);
                    }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return await ResourcePreparationPlan.CreateAsync(PackageVersion, OwnedManifest, new List<ResourcePreparationBundle>(result), selected, cancellationToken);
            });
        }

        public ResourceOperationBase PrepareSelectionAsync(ResourceSelection selection, DownloadPriority priority = DownloadPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            CheckThread();
            if (m_closing) {
                throw new ObjectDisposedException(nameof(ResourceManager));
            }

            cancellationToken.ThrowIfCancellationRequested();
            ValidateSelectionInput(selection);
            return FileSystem == null
                ? throw new NotSupportedException("当前资源后端未提供文件系统。")
                : (ResourceOperationBase)TrackContentOperation<bool>(async () =>
            {
                ResourceSelectionResult selected = await SelectAsync(selection, cancellationToken);
                await ResourceWorkBatch.RunAsync(selected.BundleNames, 3,
                    (name, token) => PrepareFileAsync(name, priority, token), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            });
        }

        private void ValidateSelectionInput(ResourceSelection selection)
        {
            if (selection == null) {
                throw new ArgumentNullException(nameof(selection));
            }

            if (selection.Kind == ResourceSelection.SelectionKind.Addresses) {
                foreach (var address in selection.Values) {
                    if (!m_locations.ContainsKey(address)) {
                        throw new KeyNotFoundException("Unknown resource address: " + address);
                    }
                }
            }
        }

        private ResourceOperationBase<ResourceFileLocation> PrepareFileAsync(string name, DownloadPriority priority, CancellationToken token)
        {
            // 用户文件系统可以在进入时重入关闭管理器；后续工作者不得再派发文件操作。
            return m_closing
                ? throw new OperationCanceledException("资源管理器正在关闭。", token)
                : FileSystem.ResolveAsync(ResourceManifest.CopyBundle(m_catalog[name]), priority, token);
        }

        private ResourceOperationBase<T> TrackContentOperation<T>(Func<ResourceOperationBase<T>> action)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            // 先登记，再进入可扩展文件系统，关闭重入时也会等待真实操作收尾。
            var completion = new OperationCompletionSource<T>();
            m_pendingDownloads.Add(completion.Operation);
            _ = CompleteAsync();
            return completion.Operation;
            async ResourceOperationBase CompleteAsync()
            {
                try {
                    T result = await action();
                    m_pendingDownloads.Remove(completion.Operation);
                    completion.SetResult(result);
                }
                catch (Exception error) {
                    m_pendingDownloads.Remove(completion.Operation);
                    completion.SetException(error);
                }
            }
        }
    }
}

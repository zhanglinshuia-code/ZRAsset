using System;
using System.Threading;

namespace ZRAsset
{
    /// <summary>复用现有的校验、断点恢复、空间预留及跨版本复用引擎；不承担 Unity 对象加载。</summary>
    public sealed class CachedResourceFileSystem: ResourceFileSystem, IResourceDownloadSource, ISynchronousResourceFileSystem, IResourceFileInspector, IResourceFileImporter
    {
        private readonly BundleDownloadCache m_cache;
        public BundleDownloadQueue DownloadQueue
        {
            get
            {
                return m_cache.Queue;
            }
        }

        public override bool SupportsDownloads
        {
            get
            {
                return true;
            }
        }

        public override bool SupportsPersistentCache
        {
            get
            {
                return true;
            }
        }

        public override bool OwnsRetries
        {
            get
            {
                return true;
            }
        }

        public override ResourceFileCapabilities FileCapabilities
        {
            get
            {
                return m_cache.FileCapabilities;
            }
        }

        public CachedResourceFileSystem(string builtInRoot, BundleDownloadOptions options, string buildTarget = "legacy",
            ResourceManifest builtInManifest = null, IDownloadTransport transport = null, IDownloadTransport unpackTransport = null)
            : this(new BundleDownloadCache(builtInRoot, options, buildTarget, builtInManifest, transport, unpackTransport)) { }

        /// <summary>接管缓存实例的关闭职责，不能把同一实例交给多个管理器。</summary>
        public CachedResourceFileSystem(BundleDownloadCache cache) { m_cache = cache ?? throw new ArgumentNullException(nameof(cache)); }

        protected override async ResourceOperationBase<ResourceFileLocation> ResolveCoreAsync(BundleInfo info, DownloadPriority priority, CancellationToken token)
        {
            return new ResourceFileLocation(await m_cache.ResolvePathAsync(info, priority, token));
        }

        protected override IDisposable AcquireReadLeaseCore()
        {
            return m_cache.AcquireReadLease();
        }

        protected override ResourceOperationBase ShutdownAsync()
        {
            return m_cache.DisposeAsync();
        }

        /// <summary>仅定位目标缓存或本地首包；不扫描复用、不下载、不修复。缺失时须先异步准备。</summary>
        public ResourceFileLocation Resolve(BundleInfo info)
        {
            CheckAvailable();
            return m_cache.ResolveLocal(Snapshot(info));
        }

        /// <summary>只读规划，与真正准备使用同一缓存/首包检查链。不会下载或修复缓存。</summary>
        public ResourceOperationBase<ResourcePreparationBundle> InspectAsync(BundleInfo info, CancellationToken cancellationToken = default)
        {
            CheckAvailable();
            cancellationToken.ThrowIfCancellationRequested();
            BundleInfo snapshot = Snapshot(info);
            return RunTrackedAsync(token => m_cache.InspectAsync(snapshot, token), cancellationToken);
        }

        public ResourceOperationBase<ResourceFileLocation> ImportAsync(BundleInfo info, string sourcePath,
            CancellationToken cancellationToken = default)
        {
            CheckAvailable();
            cancellationToken.ThrowIfCancellationRequested();
            BundleInfo snapshot = Snapshot(info);
            if (string.IsNullOrWhiteSpace(sourcePath) || !System.IO.Path.IsPathRooted(sourcePath)) {
                throw new ArgumentException("导入源必须为本地绝对路径。", nameof(sourcePath));
            }

            var source = System.IO.Path.GetFullPath(sourcePath);
            return RunTrackedAsync(token => m_cache.ImportAsync(snapshot, source, token), cancellationToken);
        }
    }
}

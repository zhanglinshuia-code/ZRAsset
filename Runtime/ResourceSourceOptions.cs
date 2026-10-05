using System;
using System.IO;
using System.Threading;

namespace ZRAsset
{
    public readonly struct ResourceSourceContext
    {
        public string BuildTarget { get; }
        public string Version { get; }
        public string CacheRoot { get; }
        public bool Offline { get; }
        internal ResourceSourceContext(string target, BundleDownloadOptions options, bool offline)
        { BuildTarget = target; Version = options.CacheVersion; CacheRoot = options.CacheRoot; Offline = offline; }
    }

    /// <summary>
    /// 将 PAD 或自定义本地来源接入版本准备、按需加载和启动流程。每次工厂必须返回独立实例，缓存接管关闭职责。
    /// 来源须支持只读检查并返回可校验的本地文件；缺失或损坏可回退 CDN，取消及平台交互错误保持原样。
    /// </summary>
    public sealed class ResourceSourceOptions
    {
        public Func<ResourceSourceContext, IResourceFileSystem> Factory { get; }
        public Func<BundleInfo, bool> Contains { get; }
        public bool PrepareMissing { get; }
        public ResourceSourceOptions(Func<ResourceSourceContext, IResourceFileSystem> factory,
            Func<BundleInfo, bool> contains = null, bool prepareMissing = true)
        { Factory = factory ?? throw new ArgumentNullException(nameof(factory)); Contains = contains; PrepareMissing = prepareMissing; }
    }

    public sealed partial class BundleDownloadCache
    {
        private readonly IResourceFileSystem m_additionalSource;
        private readonly bool m_offline;

        private bool HasAdditionalSource(BundleInfo info)
        {
            return m_additionalSource != null && (m_options.SourceOptions.Contains?.Invoke(Snapshot(info)) ?? true);
        }

        private async ResourceOperationBase<ResourceFileLocation> InspectAdditionalAsync(BundleInfo info, CancellationToken token)
        {
            if (!HasAdditionalSource(info)) { return null; }
            try {
                if (m_additionalSource is PlatformResourceFileSystem platform) { return await platform.InspectVerifiedAsync(info, token); }
                ResourcePreparationBundle entry = await ((IResourceFileInspector)m_additionalSource).InspectAsync(info, token);
                // Inspection never invokes Prepare/Resolve: even if the file disappears, offline mode cannot start a platform download.
                return entry.Source == ResourcePreparationSource.Download || string.IsNullOrEmpty(entry.SourcePath) ? null :
                    await VerifyAdditionalAsync(new ResourceFileLocation(entry.SourcePath), info, token);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (InvalidDataException) { return null; }
        }

        private static async ResourceOperationBase<ResourceFileLocation> VerifyAdditionalAsync(ResourceFileLocation file, BundleInfo info, CancellationToken token)
        {
            return file?.LocalPath == null
                ? throw new InvalidOperationException("组合来源必须返回可校验的本地文件。")
                : await IsUsableFileAsync(file.LocalPath, info, token) ? file : null;
        }

        private async ResourceOperationBase<ResourceFileLocation> PrepareAdditionalAsync(BundleInfo info, DownloadPriority priority, CancellationToken token)
        {
            if (m_offline || !HasAdditionalSource(info) || !m_options.SourceOptions.PrepareMissing) { return null; }
            try {
                ResourceFileLocation file = await m_additionalSource.ResolveAsync(info, priority, token);
                return m_additionalSource is PlatformResourceFileSystem ? file : await VerifyAdditionalAsync(file, info, token);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (InvalidDataException) { return null; }
        }

        internal IDisposable AcquireReadLease()
        {
            IDisposable cache = Queue.AcquireCacheUsageLease();
            if (m_additionalSource == null) { return cache; }
            try { return new SourceReadLease(cache, m_additionalSource.AcquireReadLease()); }
            catch { cache.Dispose(); throw; }
        }

        private sealed class SourceReadLease: IDisposable
        {
            private IDisposable m_cache, m_source;
            internal SourceReadLease(IDisposable cache, IDisposable source) { m_cache = cache; m_source = source; }
            public void Dispose()
            {
                IDisposable cache = m_cache, source = m_source;
                m_cache = m_source = null;
                try { source?.Dispose(); } finally { cache?.Dispose(); }
            }
        }
    }
}

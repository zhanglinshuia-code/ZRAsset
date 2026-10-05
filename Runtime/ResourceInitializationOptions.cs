using System;
using System.Threading;

namespace ZRAsset
{
    public enum ResourceManifestFormat { Json, Binary, Encoded }

    /// <summary>单次初始化的值快照；默认值等同于默认首包、JSON 清单和 5 秒延迟卸载。</summary>
    public struct ResourceInitializationOptions
    {
        private double? m_unloadDelay;
        public string Root { get; set; }
        public ResourceManifestFormat ManifestFormat { get; set; }
        public double UnloadDelaySeconds { get { return m_unloadDelay ?? 5; } set { m_unloadDelay = value; } }
        public ResourceRetryPolicy RetryPolicy { get; set; }
        public ResourceLoadOptions LoadOptions { get; set; }
        public IResourceDecryptionServices DecryptionServices { get; set; }
        public IResourceKeyProvider ManifestKeys { get; set; }
        public IResourceManifestCodec ManifestCodec { get; set; }
        /// <summary>显式策略用于初始化及更新；配置更新时非空值覆盖下载选项的策略，null 保留下载选项。</summary>
        public ResourceDownloadPolicy NetworkPolicy { get; set; }

        internal void Validate()
        {
            if (!Enum.IsDefined(typeof(ResourceManifestFormat), ManifestFormat)) { throw new ArgumentOutOfRangeException(nameof(ManifestFormat)); }
            if (double.IsNaN(UnloadDelaySeconds) || double.IsInfinity(UnloadDelaySeconds) || UnloadDelaySeconds < 0) {
                throw new ArgumentOutOfRangeException(nameof(UnloadDelaySeconds));
            }
        }

        internal static string FileName(ResourceManifestFormat format)
        {
            return format switch
            {
                ResourceManifestFormat.Json => "manifest.json",
                ResourceManifestFormat.Binary => "manifest.zrmb",
                ResourceManifestFormat.Encoded => "manifest.zrme",
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
        }
    }

    public sealed partial class ResourcePackage
    {
        public ResourceOperationBase InitializeAsync(ResourceInitializationOptions options, CancellationToken cancellationToken = default)
        {
            options.Validate();
            return InitializeAsync(options.Root, cancellationToken, options.UnloadDelaySeconds, options.RetryPolicy,
                options.LoadOptions, options.DecryptionServices, options.ManifestFormat == ResourceManifestFormat.Binary,
                options.ManifestFormat == ResourceManifestFormat.Encoded, options.ManifestKeys, options.ManifestCodec, options.NetworkPolicy);
        }

        /// <summary>异步恢复更新指针；首包格式、密钥及加载选项使用同一份值快照。</summary>
        public ResourceOperationBase ConfigureUpdatesAsync(string buildTarget, BundleDownloadOptions downloads,
            ResourceInitializationOptions options, CancellationToken cancellationToken = default)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(this);
            return ConfigureUpdatesCoreAsync(buildTarget, downloads, options, cancellationToken);
        }

        public ResourceOperationBase ConfigureUpdatesAsync(string buildTarget, BundleDownloadOptions downloads,
            string builtInRoot = null, CancellationToken cancellationToken = default)
        {
            return ConfigureUpdatesAsync(buildTarget, downloads, new ResourceInitializationOptions { Root = builtInRoot }, cancellationToken);
        }

        public void ConfigureUpdates(string buildTarget, BundleDownloadOptions downloads, ResourceInitializationOptions options)
        {
            CheckAvailable();
            BundleDownloadOptions scoped = PrepareUpdateOptions(downloads, ref options);
            var manager = ResourceVersionManager.CreateForPackage(buildTarget, scoped, Name, options);
            SetVersions(manager, options);
        }

        private async ResourceOperationBase ConfigureUpdatesCoreAsync(string buildTarget, BundleDownloadOptions downloads,
            ResourceInitializationOptions options, CancellationToken token)
        {
            Begin();
#if UNITY_EDITOR
            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, m_editorSessionCancellation.Token);
            token = sessionCancellation.Token;
#endif
            try {
                token.ThrowIfCancellationRequested();
                BundleDownloadOptions scoped = PrepareUpdateOptions(downloads, ref options);
                ResourceVersionManager manager = await ResourceVersionManager.CreateForPackageAsync(buildTarget, scoped, Name, options, token);
                token.ThrowIfCancellationRequested();
                SetVersions(manager, options);
            }
            finally { m_busy = false; }
        }

        private BundleDownloadOptions PrepareUpdateOptions(BundleDownloadOptions downloads, ref ResourceInitializationOptions options)
        {
            if (m_resources != null || m_versions != null) { throw new InvalidOperationException("Package 已初始化或配置更新。"); }
            if (downloads == null) { throw new ArgumentNullException(nameof(downloads)); }
            options.Validate();
            options.Root ??= ResourcePath.Combine(UnityEngine.Application.streamingAssetsPath, "ZRAsset/Packages/" + Name);
            return new BundleDownloadOptions(downloads.RemoteBaseUrl, downloads.CacheVersion,
                ResourcePackageIdentity.GetCacheRoot(downloads.CacheRoot, Name), downloads.MaxConcurrentDownloads,
                downloads.RequestTimeoutSeconds, downloads.RetryPolicy, downloads.DiskPolicy, options.NetworkPolicy ?? downloads.NetworkPolicy, downloads.SourceOptions);
        }

        private void SetVersions(ResourceVersionManager manager, ResourceInitializationOptions options)
        {
            manager.SchedulingPackage = this;
            m_versions = manager;
            m_unloadDelay = options.UnloadDelaySeconds;
            m_retryPolicy = options.RetryPolicy;
            m_loadOptions = options.LoadOptions;
            m_decryptionServices = options.DecryptionServices;
        }
    }
}

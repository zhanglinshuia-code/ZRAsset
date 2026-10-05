using System;
using System.IO;
using UnityEngine;

namespace ZRAsset.HotUpdate
{
    /// <summary>保存在主包中的启动策略和公钥。业务入口来自已认证的热更清单，不在这里猜测类名。</summary>
    [CreateAssetMenu(menuName = "ZRAsset/热更启动配置", fileName = "ZRAssetStartupConfig")]
    public sealed class HotUpdateStartupConfig: ScriptableObject
    {
        [Header("宿主与启动方式")]
        public string PlayerBuildId;
        [Tooltip("留空兼容旧清单；命名包必须与构建配置一致，缓存自动按包隔离。")]
        public string PackageName;
        public HotUpdateBootstrapMode Mode = HotUpdateBootstrapMode.SignedUpdate;
        public string ManifestAddress = "hotupdate/manifest";
        [Tooltip("只允许发布检查、规划或准备失败后使用旧活动版本；激活/代码启动失败不会自动恢复。")]
        public bool AllowOfflineFallback;
        [Header("可信发布")]
        public string SignedReleaseUrl;
        public string RemoteBundleBaseUrl;
        public ResourceReleasePublicKey[] TrustedKeys = Array.Empty<ResourceReleasePublicKey>();
        public long MinimumSequence;
        [Tooltip("仅供本机验证；生产发布应关闭。")]
        public bool AllowHttpLoopback;
        [Header("本地文件")]
        [Tooltip("persistentDataPath 下的单层缓存目录名。")]
        public string CacheDirectory = "ZRAssetCache";
        public bool UseBuiltInFiles = true;
        [Tooltip("首次离线启动使用的首包版本；留空则要求已有活动版本。")]
        public string BuiltInVersion;
        public string BuiltInManifestSha256;
        [Tooltip("StreamingAssets 下的单层首包目录名。")]
        public string BuiltInDirectory = "ZRAsset";
        [Tooltip("读取 manifest.zrme；未开启时读取 manifest.json。密钥与 Codec 由运行时注入。")]
        public bool EncodedBuiltInManifest;
        [Header("并发与回收")]
        [Range(1, 32)] public int MaxConcurrentDownloads = 3;
        [Range(1, 600)] public int RequestTimeoutSeconds = 60;
        [Range(1, 64)] public int MaxConcurrentBundleLoads = 4;
        [Range(1, 64)] public int MaxConcurrentAssetLoads = 8;
        [Min(0)] public float UnloadDelaySeconds = 5;

        /// <summary>复制配置产生本次启动快照；测试/构建验证可显式覆盖目录，运行中修改资产不影响既有快照。</summary>
        public HotUpdateBootstrapOptions CreateOptions(string cacheRootOverride = null, string builtInRootOverride = null,
            Func<ResourcePreparationPlan, System.Threading.CancellationToken, ResourceOperationBase> beforePrepareAsync = null,
            IResourceDecryptionServices decryptionServices = null, IResourceKeyProvider manifestKeys = null, IResourceManifestCodec manifestCodec = null,
            ResourceDownloadPolicy networkPolicy = null, ResourceSourceOptions sourceOptions = null)
        {
            if (!DownloadStorage.IsSafeSegment(CacheDirectory) || (UseBuiltInFiles && !DownloadStorage.IsSafeSegment(BuiltInDirectory))) {
                throw new ArgumentException("缓存和首包目录必须是安全的单层目录名。");
            }

            ResourceReleaseTrustOptions trust = Mode == HotUpdateBootstrapMode.SignedUpdate
                ? new ResourceReleaseTrustOptions(TrustedKeys, MinimumSequence, AllowHttpLoopback) : null;
            // 离线模式仍需提供结构有效的下载配置；内部传输器始终拒绝网络，不会请求这个占位地址。
            var remote = Mode == HotUpdateBootstrapMode.OfflineActive && string.IsNullOrWhiteSpace(RemoteBundleBaseUrl)
                ? "https://offline.invalid/" : RemoteBundleBaseUrl;
            var parent = cacheRootOverride ?? Path.Combine(Application.persistentDataPath, CacheDirectory);
            var downloads = new BundleDownloadOptions(remote, "bootstrap", HostCacheLifecycle.PathFor(parent, PlayerBuildId),
                MaxConcurrentDownloads, RequestTimeoutSeconds, networkPolicy: networkPolicy, sourceOptions: sourceOptions);
            var package = string.IsNullOrWhiteSpace(PackageName) ? null : PackageName;
            var builtIn = "";
            if (UseBuiltInFiles) {
                builtIn = BundleLoader.Combine(Application.streamingAssetsPath, BuiltInDirectory);
                if (package != null) { builtIn = BundleLoader.Combine(BundleLoader.Combine(builtIn, "Packages"), package); }
            }
            builtIn = builtInRootOverride ?? builtIn;
            return new HotUpdateBootstrapOptions(PlayerBuildId, downloads, trust, SignedReleaseUrl, builtIn, Mode,
                AllowOfflineFallback, ManifestAddress, UnloadDelaySeconds, new ResourceLoadOptions(MaxConcurrentBundleLoads, MaxConcurrentAssetLoads), parent,
                string.IsNullOrWhiteSpace(BuiltInVersion) ? null : BuiltInVersion, BuiltInManifestSha256, beforePrepareAsync,
                package, decryptionServices, manifestKeys, manifestCodec, EncodedBuiltInManifest);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>平台分发只提供路径；框架仍校验清单长度与 SHA-256。路径不能跨启动缓存。</summary>
    public interface IResourceDeliveryProvider
    {
        ResourceFileLocation TryLocate(BundleInfo info);
        ResourceOperationBase<ResourceFileLocation> PrepareAsync(BundleInfo info, CancellationToken token);
    }

    public sealed class ResourceDeliveryConfirmationException: IOException
    {
        public string AssetPack { get; }
        public int Status { get; }
        public ResourceDeliveryConfirmationException(string assetPack, int status)
            : base("PAD 需要用户确认：" + assetPack + "，状态 " + status) { AssetPack = assetPack; Status = status; }
    }
    public sealed class PlatformResourceFileSystem: ResourceFileSystem, ISynchronousResourceFileSystem, IResourceFileInspector
    {
        private readonly IResourceDeliveryProvider m_provider;
        public PlatformResourceFileSystem(IResourceDeliveryProvider provider) { m_provider = provider ?? throw new ArgumentNullException(nameof(provider)); }
        public override ResourceFileCapabilities FileCapabilities
        {
            get
            {
                return ResourceFileCapabilities.SynchronousRead | ResourceFileCapabilities.RandomAccess;
            }
        }

        public override bool SupportsDownloads
        {
            get
            {
                return true;
            }
        }

        protected override async ResourceOperationBase<ResourceFileLocation> ResolveCoreAsync(BundleInfo info, DownloadPriority priority, CancellationToken token)
        {
            ResourceFileLocation file = await m_provider.PrepareAsync(info, token);
            if (file?.LocalPath == null) {
                throw new IOException("平台分发必须返回已就绪的本地文件。");
            }

            token.ThrowIfCancellationRequested();
            return !File.Exists(file.LocalPath) || new FileInfo(file.LocalPath).Length != info.Size
                ? throw new InvalidDataException("平台文件长度不匹配。")
                : string.IsNullOrEmpty(info.Sha256) || !string.Equals(await ResourceFileIO.Shared.ComputeSha256Async(file.LocalPath, token), info.Sha256, StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidDataException("平台文件 SHA-256 不匹配。")
                : file;
        }
        public ResourceFileLocation Resolve(BundleInfo info)
        {
            CheckAvailable(); info = Snapshot(info);
            ResourceFileLocation file = m_provider.TryLocate(info) ?? throw new InvalidOperationException("平台文件尚未准备，请先异步准备。");
            ValidateMetadataAndSize(file, info);
            BuiltInResourceFileSystem.ValidateLocalFile(file, info); return file;
        }
        private static void ValidateMetadataAndSize(ResourceFileLocation file, BundleInfo info)
        {
            if (file?.LocalPath == null || !DownloadStorage.IsSha256(info.Sha256) || !File.Exists(file.LocalPath) || new FileInfo(file.LocalPath).Length != info.Size) {
                throw new InvalidDataException("平台文件必须具有准确长度和 SHA-256。");
            }
        }
        public ResourceOperationBase<ResourcePreparationBundle> InspectAsync(BundleInfo info, CancellationToken cancellationToken = default)
        {
            CheckAvailable(); cancellationToken.ThrowIfCancellationRequested(); info = Snapshot(info);
            return RunTrackedAsync(token => InspectCoreAsync(info, token), cancellationToken);
        }
        private async ResourceOperationBase<ResourcePreparationBundle> InspectCoreAsync(BundleInfo info, CancellationToken cancellationToken)
        {
            ResourceFileLocation file = await InspectVerifiedCoreAsync(info, cancellationToken);
            return new ResourcePreparationBundle(info.Name, info.Size,
                file == null ? ResourcePreparationSource.Download : ResourcePreparationSource.BuiltIn, file?.Location);
        }

        // 只向框架组合层交付本次校验的结果；不将公开计划或旧路径当作后续请求的校验凭证。
        internal ResourceOperationBase<ResourceFileLocation> InspectVerifiedAsync(BundleInfo info, CancellationToken token)
        {
            CheckAvailable(); token.ThrowIfCancellationRequested(); info = Snapshot(info);
            return RunTrackedAsync(cancellation => InspectVerifiedCoreAsync(info, cancellation), token);
        }

        private async ResourceOperationBase<ResourceFileLocation> InspectVerifiedCoreAsync(BundleInfo info, CancellationToken cancellationToken)
        {
            ResourceFileLocation file = m_provider.TryLocate(info);
            try {
                if (file != null) {
                    ValidateMetadataAndSize(file, info);
                    if (!string.Equals(await ResourceFileIO.Shared.ComputeSha256Async(file.LocalPath, cancellationToken), info.Sha256, StringComparison.OrdinalIgnoreCase)) {
                        file = null;
                    }
                }
            }
            catch (IOException) { file = null; }
            return file;
        }
    }

    /// <summary>Google PAD fast-follow/on-demand 资源包；要求工程已集成 asset-delivery Android 库与对应 asset pack。</summary>
    public sealed class GooglePlayDeliveryProvider: IResourceDeliveryProvider
    {
        private readonly Dictionary<string, string> m_packs;
        private readonly double m_timeout;
        private readonly Func<string, int, CancellationToken, ResourceOperationBase> m_confirm;
        public GooglePlayDeliveryProvider(IDictionary<string, string> bundleToAssetPack, double timeoutSeconds = 300,
            Func<string, int, CancellationToken, ResourceOperationBase> confirmAsync = null)
        {
            m_packs = new Dictionary<string, string>(bundleToAssetPack ?? throw new ArgumentNullException(nameof(bundleToAssetPack)), StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in m_packs) {
                if (!ResourceManifest.IsSafeBundleName(pair.Key) || string.IsNullOrEmpty(pair.Value) || !char.IsLetter(pair.Value[0]) ||
                    System.Linq.Enumerable.Any(pair.Value, c => !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'))) {
                    throw new ArgumentException("PAD 名称或 Bundle 映射无效。");
                }
            }

            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds <= 0) {
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            }

            m_timeout = timeoutSeconds;
            m_confirm = confirmAsync;
        }
        public bool Contains(BundleInfo info) { return info != null && m_packs.ContainsKey(info.Name); }

        /// <summary>创建独立文件系统工厂，映射在注册时复制；可直接放入 BundleDownloadOptions.SourceOptions。</summary>
        public static ResourceSourceOptions CreateSourceOptions(IDictionary<string, string> bundleToAssetPack,
            double timeoutSeconds = 300, Func<string, int, CancellationToken, ResourceOperationBase> confirmAsync = null)
        {
            var snapshot = new GooglePlayDeliveryProvider(bundleToAssetPack, timeoutSeconds, confirmAsync);
            return new ResourceSourceOptions(_ => new PlatformResourceFileSystem(
                new GooglePlayDeliveryProvider(snapshot.m_packs, timeoutSeconds, confirmAsync)), snapshot.Contains);
        }
        private string Pack(BundleInfo info)
        {
            return m_packs.TryGetValue(info.Name, out var pack) ? pack : throw new KeyNotFoundException("未配置 PAD 包：" + info.Name);
        }

        public ResourceFileLocation TryLocate(BundleInfo info)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using AndroidJavaObject manager = CreateManager(); return Locate(manager, Pack(info), info.Name);
#else
            throw new PlatformNotSupportedException("Google PAD 只能在 Android Player 中使用。");
#endif
        }
        public async ResourceOperationBase<ResourceFileLocation> PrepareAsync(BundleInfo info, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
#if UNITY_ANDROID && !UNITY_EDITOR
            using AndroidJavaObject manager = CreateManager(); var pack = Pack(info);
            ResourceFileLocation file = Locate(manager, pack, info.Name); if (file != null) {
                return file;
            }

            using var collections = new AndroidJavaClass("java.util.Collections");
            using AndroidJavaObject names = collections.CallStatic<AndroidJavaObject>("singletonList", pack);
            using AndroidJavaObject fetch = manager.Call<AndroidJavaObject>("fetch", names);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var confirmedStatus = 0;
            while (true) {
                token.ThrowIfCancellationRequested();
                if (clock.Elapsed.TotalSeconds > m_timeout) {
                    throw new TimeoutException("PAD 下载超时：" + pack);
                }

                if (fetch.Call<bool>("isComplete") && !fetch.Call<bool>("isSuccessful")) {
                    throw new IOException("PAD fetch 失败：" + pack);
                }

                file = Locate(manager, pack, info.Name); if (file != null) {
                    return file;
                }

                using AndroidJavaObject query = manager.Call<AndroidJavaObject>("getPackStates", names);
                while (!query.Call<bool>("isComplete")) { token.ThrowIfCancellationRequested(); if (clock.Elapsed.TotalSeconds > m_timeout) { throw new TimeoutException("PAD 状态查询超时。"); } await ResourceOperationBase.Yield(); }
                if (!query.Call<bool>("isSuccessful")) {
                    throw new IOException("PAD 状态查询失败。");
                }

                using AndroidJavaObject states = query.Call<AndroidJavaObject>("getResult");
                using AndroidJavaObject map = states.Call<AndroidJavaObject>("packStates");
                using AndroidJavaObject state = map.Call<AndroidJavaObject>("get", pack);
                var status = state.Call<int>("status");
                if (status == 5 || status == 6) {
                    throw new IOException("PAD 下载失败/取消，错误码：" + state.Call<int>("errorCode"));
                }

                if (status == 7 || status == 9) {
                    if (m_confirm == null) { throw new ResourceDeliveryConfirmationException(pack, status); }
                    if (confirmedStatus != status) {
                        await m_confirm(pack, status, token);
                        token.ThrowIfCancellationRequested();
                        confirmedStatus = status;
                    }
                }
                else { confirmedStatus = 0; }

                await ResourceOperationBase.Delay(500, token);
            }
#else
            await ResourceOperationBase.Yield(); throw new PlatformNotSupportedException("Google PAD 只能在 Android Player 中使用。");
#endif
        }
#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject CreateManager()
        {
            using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using AndroidJavaObject activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
            using var factory = new AndroidJavaClass("com.google.android.play.core.assetpacks.AssetPackManagerFactory");
            return factory.CallStatic<AndroidJavaObject>("getInstance", activity);
        }
        private static ResourceFileLocation Locate(AndroidJavaObject manager, string pack, string bundle)
        {
            using AndroidJavaObject location = manager.Call<AndroidJavaObject>("getPackLocation", pack);
            if (location == null) {
                return null;
            }

            var root = location.Call<string>("assetsPath");
            if (string.IsNullOrEmpty(root)) {
                return null;
            }

            var path = DownloadStorage.ValidatePath(root, Path.Combine(root, bundle));
            return File.Exists(path) ? new ResourceFileLocation(path) : null;
        }
#endif
    }
}

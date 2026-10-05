using System;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace ZRAsset
{
    /// <summary>SDK 的完整 Bundle 生命周期；请求创建、对象提取和卸载必须来自同一平台实现。</summary>
    public interface IMiniGamePlatformStrategy
    {
        UnityWebRequest CreateBundleRequest(string url, BundleInfo info);
        AssetBundle ExtractBundle(UnityWebRequest request);
        void UnloadBundle(AssetBundle bundle, bool unloadAllObjects);
    }

    /// <summary>适配宿主 SDK 的异步加载器。文件系统负责并发/重试；底层请求完成后才交付或卸载 Bundle。</summary>
    public sealed class MiniGameUnityResourceLoader: IUnityResourceLoader, IResourceCollectionBackend
    {
        private readonly IMiniGamePlatformStrategy m_platform;
        private readonly ResourceDownloadPolicy m_network;
        private readonly string m_originalUrl;
        private readonly int m_timeout;
        private readonly UnityResourceLoader m_assets = new();
        public MiniGameUnityResourceLoader(IMiniGamePlatformStrategy platform, ResourceDownloadPolicy networkPolicy = null,
            string originalUrl = null, int timeoutSeconds = 60)
        {
            m_platform = platform ?? throw new ArgumentNullException(nameof(platform));
            if (timeoutSeconds < 1 || timeoutSeconds > 600) { throw new ArgumentOutOfRangeException(nameof(timeoutSeconds)); }
            m_network = networkPolicy; m_originalUrl = originalUrl; m_timeout = timeoutSeconds;
        }
        public bool SupportsAssetCollections { get { return true; } }

        public async ResourceOperationBase<object> LoadBundleAsync(ResourceFileLocation file, BundleInfo info)
        {
            if (file == null) { throw new ArgumentNullException(nameof(file)); }
            info = ResourceFileSystem.Snapshot(info);
            if (file.Kind != ResourceLocationKind.HttpUri || info.FileType != ResourceFileType.AssetBundle || info.IsEncrypted) {
                throw new NotSupportedException("SDK URL 加载器只接受未加密的 HTTP(S) AssetBundle；本地和加密文件使用 UnityResourceLoader。");
            }
            using UnityWebRequest request = m_platform.CreateBundleRequest(file.Location, info) ?? throw new InvalidOperationException("平台返回了空请求。");
            request.timeout = m_timeout;
            (m_network ?? file.NetworkPolicy)?.Configure(request, new ResourceRequestContext(file.Location, m_originalUrl ?? file.Location, ResourceRequestKind.AssetBundle));
            await UnityOperations.WaitAsync(request.SendWebRequest());
            if (request.result != UnityWebRequest.Result.Success) {
                throw new ResourceDownloadException("平台 Bundle 加载失败：" + request.error, request.responseCode, request.GetResponseHeader("Retry-After"));
            }
            AssetBundle bundle = m_platform.ExtractBundle(request);
            return bundle == null ? throw new IOException("平台没有返回 AssetBundle：" + info.Name) : (object)bundle;
        }
        public ResourceOperationBase<Object> LoadAssetAsync(object bundle, string assetPath, Type type)
        { return m_assets.LoadAssetAsync(bundle, assetPath, type); }
        public ResourceOperationBase<Object[]> LoadSubAssetsAsync(object bundle, string assetPath, Type type)
        { return m_assets.LoadSubAssetsAsync(bundle, assetPath, type); }
        public ResourceOperationBase<Object[]> LoadAllAssetsAsync(object bundle, Type type)
        { return m_assets.LoadAllAssetsAsync(bundle, type); }
        public void UnloadBundle(object bundle) { m_platform.UnloadBundle((AssetBundle)bundle, true); }
    }
}

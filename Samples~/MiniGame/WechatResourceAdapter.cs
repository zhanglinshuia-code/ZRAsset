#if UNITY_WEBGL && (WEIXINMINIGAME || UNITY_WECHATMINIGAME)
using UnityEngine;
using UnityEngine.Networking;
using WeChatWASM;

namespace ZRAsset.Integrations
{
    /// <summary>微信 Unity SDK 原生 Bundle 路径。必须在 WX SDK 初始化完成后使用。</summary>
    public sealed class WechatResourceAdapter: IMiniGamePlatformStrategy
    {
        public UnityWebRequest CreateBundleRequest(string url, BundleInfo info)
        {
            UnityWebRequest request = WXAssetBundle.GetAssetBundle(url);
            request.disposeDownloadHandlerOnDispose = true;
            return request;
        }
        public AssetBundle ExtractBundle(UnityWebRequest request)
        { return ((DownloadHandlerWXAssetBundle)request.downloadHandler).assetBundle; }
        public void UnloadBundle(AssetBundle bundle, bool unloadAllObjects) { bundle.WXUnload(unloadAllObjects); }

        public static void InitializePackage(ResourcePackage package, ResourceManifest manifest, string versionedRoot,
            ResourceDownloadPolicy network = null, ResourceRetryPolicy retry = null)
        {
            network ??= new ResourceDownloadPolicy();
            var preload = new MiniGamePreloadStrategy(url =>
            {
                var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET);
                request.SetRequestHeader("wechatminigame-preload", "1");
                return request;
            }, networkPolicy: network, originalUrl: versionedRoot);
            var files = new MiniGameWebFileSystem(versionedRoot, preload, retryPolicy: retry,
                urlPolicy: network.UrlPolicy, maxRequestsPerFrame: network.MaxRequestsPerFrame, downloadRetryPolicy: network.RetryPolicy);
            try {
                package.InitializeFileSystem(manifest, files,
                    objectLoader: new MiniGameUnityResourceLoader(new WechatResourceAdapter(), network, versionedRoot));
            }
            catch { _ = files.DisposeAsync(); throw; }
        }
    }
}
#endif

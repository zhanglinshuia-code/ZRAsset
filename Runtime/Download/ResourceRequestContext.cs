using System;
using System.Collections.Generic;
using UnityEngine.Networking;

namespace ZRAsset
{
    public enum ResourceRequestKind { Manifest, SignedRelease, BundleDownload, AssetBundle, MiniGamePreload, RawFile }

    /// <summary>请求配置回调的只读上下文。OriginalUrl 是镜像切换之前的地址。</summary>
    public readonly struct ResourceRequestContext
    {
        public string Url { get; }
        public string OriginalUrl { get; }
        public ResourceRequestKind Kind { get; }
        public ResourceRequestContext(string url, string originalUrl, ResourceRequestKind kind)
        { Url = url; OriginalUrl = originalUrl ?? url; Kind = kind; }
    }

    public sealed partial class ResourceDownloadPolicy
    {
        /// <summary>统一应用于清单、签名描述、下载和原生 Bundle 请求；回调在主线程运行。</summary>
        public void Configure(UnityWebRequest request, ResourceRequestContext context)
        {
            if (request == null) { throw new ArgumentNullException(nameof(request)); }
            DownloadHandler handler = request.downloadHandler;
            UploadHandler upload = request.uploadHandler;
            var url = request.url;
            var method = request.method;
            if (AllowsCredentials(context.Url, context.OriginalUrl)) {
                foreach (KeyValuePair<string, string> header in Headers) { request.SetRequestHeader(header.Key, header.Value); }
                ConfigureRequest?.Invoke(request);
                ConfigureRequestWithContext?.Invoke(request, context);
            }
            if (request.downloadHandler != handler || request.uploadHandler != upload || request.url != url || request.method != method) {
                throw new InvalidOperationException("请求配置不能替换处理器、地址或 HTTP 方法。");
            }
            // Unity 自动重定向无法在每一跳重新检查凭据域；由调用方显式选择可信源。
            if (Headers.Count > 0 || ConfigureRequest != null || ConfigureRequestWithContext != null || context.Kind == ResourceRequestKind.SignedRelease) {
                request.redirectLimit = 0;
            }
        }

        public bool AllowsCredentials(string url, string originalUrl)
        {
            return !Uri.TryCreate(url, UriKind.Absolute, out Uri target) || (target.Scheme != "https" && target.Scheme != "http")
                ? false
                : m_credentialOrigins != null
                ? m_credentialOrigins.Contains(target.GetLeftPart(UriPartial.Authority))
                : Uri.TryCreate(originalUrl, UriKind.Absolute, out Uri source) && source.Scheme == target.Scheme &&
                source.Host == target.Host && source.Port == target.Port;
        }
    }
}

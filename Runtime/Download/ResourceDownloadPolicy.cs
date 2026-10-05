using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Networking;

namespace ZRAsset
{
    /// <summary>一次失败的只读上下文。Attempt 从 1 开始；ResponseCode 为 0 表示没有 HTTP 状态。</summary>
    public readonly struct ResourceDownloadRetryContext
    {
        public string BundleName { get; }
        public string Url { get; }
        public int Attempt { get; }
        public long ResponseCode { get; }
        public Exception Error { get; }
        public string RetryAfter { get; }
        public bool DefaultRetryAllowed { get; }
        public TimeSpan DefaultDelay { get; }

        internal ResourceDownloadRetryContext(string bundleName, string url, int attempt, long responseCode,
            Exception error, string retryAfter, bool defaultRetryAllowed, TimeSpan defaultDelay)
        {
            BundleName = bundleName; Url = url; Attempt = attempt; ResponseCode = responseCode;
            Error = error; RetryAfter = retryAfter; DefaultRetryAllowed = defaultRetryAllowed; DefaultDelay = defaultDelay;
        }
    }

    /// <summary>在 Unity 主线程决定重试；不能突破 MaxRetries，也不会接收取消或磁盘空间不足错误。</summary>
    public interface IResourceDownloadRetryPolicy
    {
        bool TryGetRetryDelay(in ResourceDownloadRetryContext context, out TimeSpan delay);
    }

    public interface IResourceDownloadUrlPolicy
    {
        string GetUrl(string originalUrl, int attempt);
    }

    /// <summary>保持原始相对路径、查询参数，在重试时轮换镜像根目录。</summary>
    public sealed class ResourceMirrorPolicy: IResourceDownloadUrlPolicy
    {
        private readonly Uri m_origin;
        private readonly Uri[] m_mirrors;
        public ResourceMirrorPolicy(string originalRoot, params string[] mirrorRoots)
        {
            m_origin = Root(originalRoot);
            m_mirrors = (mirrorRoots ?? throw new ArgumentNullException(nameof(mirrorRoots))).Select(Root).ToArray();
        }
        private static Uri Root(string value)
        {
            var uri = new Uri(value.TrimEnd('/') + "/", UriKind.Absolute);
            return (uri.Scheme != "https" && uri.Scheme != "http") || uri.Query.Length != 0 || uri.Fragment.Length != 0
                ? throw new ArgumentException("镜像根必须为不带查询参数的 HTTP(S) 目录。")
                : uri;
        }
        public string GetUrl(string originalUrl, int attempt)
        {
            var source = new Uri(originalUrl, UriKind.Absolute);
            return !m_origin.IsBaseOf(source)
                ? throw new ArgumentException("下载地址不在镜像原始根目录中。")
                : attempt <= 1 || m_mirrors.Length == 0
                ? originalUrl
                : new Uri(m_mirrors[(attempt - 2) % m_mirrors.Length], m_origin.MakeRelativeUri(source)).AbsoluteUri;
        }
    }

    /// <summary>不可变网络配置；自定义请求回调在发送前执行，不应替换下载处理器。</summary>
    public sealed partial class ResourceDownloadPolicy
    {
        public int MaxRequestsPerFrame { get; }
        public double WatchdogTimeoutSeconds { get; }
        public IResourceDownloadUrlPolicy UrlPolicy { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public Action<UnityWebRequest> ConfigureRequest { get; }
        public Func<IDownloadTransport> TransportFactory { get; }
        public IResourceDownloadRetryPolicy RetryPolicy { get; }
        private readonly HashSet<string> m_credentialOrigins;
        public Action<UnityWebRequest, ResourceRequestContext> ConfigureRequestWithContext { get; }
        public ResourceDownloadPolicy(int maxRequestsPerFrame = 8, double watchdogTimeoutSeconds = 30,
            IResourceDownloadUrlPolicy urlPolicy = null, IDictionary<string, string> headers = null,
            Action<UnityWebRequest> configureRequest = null, Func<IDownloadTransport> transportFactory = null,
            IResourceDownloadRetryPolicy retryPolicy = null,
            Action<UnityWebRequest, ResourceRequestContext> configureRequestWithContext = null,
            IEnumerable<string> credentialOrigins = null)
        {
            if (maxRequestsPerFrame < 1) {
                throw new ArgumentOutOfRangeException(nameof(maxRequestsPerFrame));
            }

            if (double.IsNaN(watchdogTimeoutSeconds) || double.IsInfinity(watchdogTimeoutSeconds) || watchdogTimeoutSeconds < 0) {
                throw new ArgumentOutOfRangeException(nameof(watchdogTimeoutSeconds));
            }

            var copy = new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in copy) {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null || pair.Key.IndexOfAny(new[] { '\r', '\n' }) >= 0 ||
                    pair.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0 || pair.Key.Equals("Range", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Equals("If-Range", StringComparison.OrdinalIgnoreCase)) {
                    throw new ArgumentException("请求头无效，Range/If-Range 由续传协议管理。");
                }
            }

            MaxRequestsPerFrame = maxRequestsPerFrame; WatchdogTimeoutSeconds = watchdogTimeoutSeconds;
            UrlPolicy = urlPolicy; Headers = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(copy);
            ConfigureRequest = configureRequest;
            TransportFactory = transportFactory;
            RetryPolicy = retryPolicy;
            ConfigureRequestWithContext = configureRequestWithContext;
            if (credentialOrigins != null) {
                m_credentialOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var origin in credentialOrigins) {
                    if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri uri) || (uri.Scheme != "https" && uri.Scheme != "http") ||
                        uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0) {
                        throw new ArgumentException("凭据来源必须是 HTTP(S) origin，不能包含路径、查询或用户信息。", nameof(credentialOrigins));
                    }
                    m_credentialOrigins.Add(uri.GetLeftPart(UriPartial.Authority));
                }
            }
        }
    }
}

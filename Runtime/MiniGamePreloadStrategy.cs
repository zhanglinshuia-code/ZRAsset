using System;
using System.Threading;
using UnityEngine.Networking;

namespace ZRAsset
{
    /// <summary>通过宿主 SDK 适配的 UnityWebRequest 预下载。工厂负责平台头/标志，不能使用 SetPreloadList 冒充完成。</summary>
    public sealed class MiniGamePreloadStrategy: IMiniGamePreloadStrategy
    {
        private readonly Func<string, UnityWebRequest> m_createRequest;
        private readonly Func<string, bool> m_isCached;
        private readonly int m_timeoutSeconds;
        private readonly ResourceDownloadPolicy m_network;
        private readonly string m_originalUrl;

        public MiniGamePreloadStrategy(Func<string, UnityWebRequest> createRequest, Func<string, bool> isCached = null, int timeoutSeconds = 60,
            ResourceDownloadPolicy networkPolicy = null, string originalUrl = null)
        {
            m_createRequest = createRequest ?? throw new ArgumentNullException(nameof(createRequest));
            m_isCached = isCached;
            if (timeoutSeconds < 1) { throw new ArgumentOutOfRangeException(nameof(timeoutSeconds)); }
            m_timeoutSeconds = timeoutSeconds;
            m_network = networkPolicy; m_originalUrl = originalUrl;
        }

        public bool IsCached(string url, BundleInfo info)
        {
            return m_isCached?.Invoke(url) ?? false;
        }

        public ResourceOperationBase PreloadAsync(string url, BundleInfo info, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnityWebRequest request = m_createRequest(url) ?? throw new InvalidOperationException("预下载工厂返回了空请求。");
            try {
                m_network?.Configure(request, new ResourceRequestContext(url, m_originalUrl ?? url, ResourceRequestKind.MiniGamePreload));
                return OperationSystem.Start(new PreloadRequest(request, m_timeoutSeconds, cancellationToken));
            }
            catch { request.Dispose(); throw; }
        }

        private sealed class PreloadRequest: ResourceOperationBase
        {
            private readonly UnityWebRequest m_request;
            private readonly CancellationToken m_token;
            private readonly double m_deadline;
            private UnityWebRequestAsyncOperation m_operation;

            internal PreloadRequest(UnityWebRequest request, int timeout, CancellationToken token)
            {
                m_request = request;
                m_token = token;
                m_request.timeout = timeout;
                m_deadline = UnityEngine.Time.realtimeSinceStartupAsDouble + timeout;
            }

            protected override void OnUpdate()
            {
                m_token.ThrowIfCancellationRequested();
                if (UnityEngine.Time.realtimeSinceStartupAsDouble >= m_deadline) {
                    throw ResourceFailure.Annotate(new TimeoutException("宿主预下载超时。"), ResourceErrorCode.Timeout, ResourceStage.Download);
                }
                m_operation ??= m_request.SendWebRequest();
                if (!m_operation.isDone) { return; }
                if (m_request.result != UnityWebRequest.Result.Success) {
                    throw new ResourceDownloadException("宿主预下载失败：" + m_request.error,
                        m_request.responseCode, m_request.GetResponseHeader("Retry-After"));
                }
                Succeed();
            }

            protected override void OnCleanup()
            {
                if (m_operation != null && !m_operation.isDone) { m_request.Abort(); }
                m_request.Dispose();
            }
        }
    }
}

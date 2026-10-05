using System;
using System.IO;

namespace ZRAsset
{
    /// <summary>保留传输状态与 Retry-After；自定义宿主预下载策略也可返回此异常。</summary>
    public sealed class ResourceDownloadException: IOException
    {
        public bool Retryable { get; }
        public string RetryAfter { get; }
        public long ResponseCode { get; }

        public ResourceDownloadException(string message, long responseCode, string retryAfter = null)
            : this(message, responseCode == 0 || (responseCode >= 200 && responseCode < 300) ||
                responseCode == 408 || responseCode == 429 || responseCode >= 500,
                retryAfter, responseCode)
        {
            // Successful headers do not imply a complete response body: a later transport failure can still retry.
            ResourceFailure.Annotate(this, ResourceFailure.HttpErrorCode(responseCode, Retryable), ResourceStage.Download, responseCode);
        }

        internal ResourceDownloadException(string message, bool retryable, string retryAfter = null, long responseCode = 0) : base(message)
        {
            Retryable = retryable;
            RetryAfter = retryAfter;
            ResponseCode = responseCode;
        }
    }

    // A single decision path keeps the file downloader and native host cache in agreement.
    internal static class ResourceDownloadRetry
    {
        internal static bool TryGetDelay(string bundleName, string url, int attempt, Exception error,
            ResourceRetryPolicy retries, IResourceDownloadRetryPolicy policy, out TimeSpan delay)
        {
            delay = default;
            if (attempt > retries.MaxRetries || !(error is IOException || error is TimeoutException) ||
                error is ResourceInsufficientSpaceException || (error is IOException io && ResourceDiskPolicy.IsDiskFull(io))) {
                return false;
            }
            var failure = error as ResourceDownloadException;
            var retryable = failure == null || failure.Retryable;
            if (policy == null && !retryable) { return false; }
            delay = retries.GetDelay(attempt, failure?.RetryAfter);
            if (policy != null) {
                var context = new ResourceDownloadRetryContext(bundleName, url, attempt,
                    failure?.ResponseCode ?? 0, error, failure?.RetryAfter, retryable, delay);
                if (!policy.TryGetRetryDelay(in context, out delay)) { return false; }
                if (delay < TimeSpan.Zero || delay > TimeSpan.FromMinutes(5)) {
                    throw new InvalidOperationException("下载重试策略必须返回 0 到 5 分钟之间的等待时间。");
                }
            }
            return true;
        }
    }
}

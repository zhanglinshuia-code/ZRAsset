using System;

namespace ZRAsset
{
    public enum ResourceFilePreparationState { Waiting, Preparing, Downloading, Verifying, Ready, Failed, Canceled }

    /// <summary>文件准备快照。ReceivedBytes 包含续传前缀和共享下载，不是实际网络流量。</summary>
    public readonly struct ResourceFilePreparationEvent
    {
        public string BundleName { get; }
        public long TotalBytes { get; }
        public long ReceivedBytes { get; }
        public int Attempt { get; }
        public ResourceFilePreparationState State { get; }
        public Exception Error { get; }
        public ResourceFailure Failure
        {
            get
            {
                return ResourceFailure.FromException(Error, ResourceStage.Prepare);
            }
        }

        internal ResourceFilePreparationEvent(string name, long total, long received, int attempt,
            ResourceFilePreparationState state, Exception error)
        {
            BundleName = name; TotalBytes = total; ReceivedBytes = received;
            Attempt = attempt; State = state; Error = error;
        }
    }

    /// <summary>
    /// CurrentBytes 包含已准备文件和在途文件字节，PreparedBytes 仅在校验完成后增加。
    /// DownloadedBytes 是本组观察到的当前下载进度（包括续传和共享前缀），不累计重试流量。
    /// 只有 HasDownloadTelemetry 为真时才区分 DownloadedBytes 和 ReusedBytes。
    /// </summary>
    public readonly struct ResourceFileBatchProgress
    {
        public int TotalCount { get; }
        public int PreparedCount { get; }
        public int ActiveCount { get; }
        public int VerifyingCount { get; }
        public long TotalBytes { get; }
        public long PreparedBytes { get; }
        public long CurrentBytes { get; }
        public long DownloadedBytes { get; }
        public long ReusedBytes { get; }
        public bool HasDownloadTelemetry { get; }
        public float Progress
        {
            get
            {
                return TotalBytes > 0 ? (float)((double)CurrentBytes / TotalBytes) :
            TotalCount > 0 ? (float)PreparedCount / TotalCount : 1;
            }
        }

        internal ResourceFileBatchProgress(int total, int prepared, int active, int verifying,
            long bytes, long preparedBytes, long current, long downloaded, long reused, bool telemetry)
        {
            TotalCount = total; PreparedCount = prepared; ActiveCount = active; VerifyingCount = verifying;
            TotalBytes = bytes; PreparedBytes = preparedBytes; CurrentBytes = current;
            DownloadedBytes = downloaded; ReusedBytes = reused; HasDownloadTelemetry = telemetry;
        }
    }
}

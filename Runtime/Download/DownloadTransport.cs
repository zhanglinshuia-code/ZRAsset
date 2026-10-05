using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine.Networking;

namespace ZRAsset
{
    /// <summary>单次 HTTP 传输参数。响应始终写到独立文件，协议校验通过后才并入断点文件。</summary>
    public sealed class DownloadTransportRequest
    {
        public string Url { get; }
        public string OutputPath { get; }
        public long Offset { get; }
        public string Validator { get; }
        public int TimeoutSeconds { get; }
        public long MaximumBytes { get; }
        public Action<DownloadTransportResponse, long> Checkpoint { get; }
        public ResourceDownloadPolicy NetworkPolicy { get; }
        public string OriginalUrl { get; }

        public DownloadTransportRequest(string url, string outputPath, long offset, string validator, int timeoutSeconds,
            long maximumBytes = long.MaxValue, Action<DownloadTransportResponse, long> checkpoint = null, ResourceDownloadPolicy networkPolicy = null,
            string originalUrl = null)
        {
            if (maximumBytes < 0) {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            Url = url; OutputPath = outputPath; Offset = offset; Validator = validator; TimeoutSeconds = timeoutSeconds;
            MaximumBytes = maximumBytes; Checkpoint = checkpoint;
            NetworkPolicy = networkPolicy ?? new ResourceDownloadPolicy();
            OriginalUrl = originalUrl ?? url;
        }
    }

    /// <summary>即使连接中断，也返回已收到的状态和响应头，让下载层判断哪些字节可以安全续传。</summary>
    public sealed class DownloadTransportResponse
    {
        public long StatusCode { get; set; }
        public string Error { get; set; }
        public bool Cancelled { get; set; }
        public string ETag { get; set; }
        public string LastModified { get; set; }
        public string ContentRange { get; set; }
        public long ContentLength { get; set; } = -1;
        public string ContentEncoding { get; set; }
        public string RetryAfter { get; set; }
        public bool ExceededLimit { get; set; }
    }

    /// <summary>
    /// 可注入的传输边界。实现只负责单次请求，不负责重试、发布或清理断点。
    /// 方法和进度回调均在创建下载队列的 Unity 主线程调用；进度为本次响应体字节数。
    /// </summary>
    public interface IDownloadTransport
    {
        ResourceOperationBase<DownloadTransportResponse> SendAsync(DownloadTransportRequest request, Action<long> onProgress,
            CancellationToken cancellationToken);
    }

    /// <summary>UnityWebRequest 文件传输；不把完整 Bundle 放进托管内存。</summary>
    public sealed class UnityWebRequestDownloadTransport: IDownloadTransport
    {
        public ResourceOperationBase<DownloadTransportResponse> SendAsync(DownloadTransportRequest parameters,
            Action<long> onProgress, CancellationToken cancellationToken)
        {
            return OperationSystem.Start(new WebDownloadOperation(parameters, onProgress, cancellationToken));
        }

        private sealed class WebDownloadOperation: ResourceOperationBase<DownloadTransportResponse>
        {
            private readonly DownloadTransportRequest m_parameters;
            private readonly Action<long> m_onProgress;
            private readonly CancellationToken m_token;
            private UnityWebRequest m_request;
            private UnityWebRequestAsyncOperation m_native;
            private bool m_aborted;
            private BoundedFileDownload m_handler;
            private long m_checkpointBytes;
            private ulong m_lastReceived;
            private bool m_stalled;
            private readonly System.Diagnostics.Stopwatch m_watchdog = new();
            private readonly System.Diagnostics.Stopwatch m_checkpointClock = System.Diagnostics.Stopwatch.StartNew();
            internal WebDownloadOperation(DownloadTransportRequest parameters, Action<long> onProgress, CancellationToken token)
            { m_parameters = parameters; m_onProgress = onProgress; m_token = token; }

            protected override void OnUpdate()
            {
                try {
                    if (m_request == null) {
                        m_token.ThrowIfCancellationRequested();
                        m_handler = BoundedFileDownload.Create(m_parameters.OutputPath, m_parameters.MaximumBytes);
                        m_request = new UnityWebRequest(m_parameters.Url, UnityWebRequest.kHttpVerbGET, m_handler, null)
                        {
                            timeout = m_parameters.TimeoutSeconds
                        };
                        m_parameters.NetworkPolicy.Configure(m_request, new ResourceRequestContext(m_parameters.Url,
                            m_parameters.OriginalUrl, ResourceRequestKind.BundleDownload));

                        if (m_parameters.Offset > 0) {
                            m_request.SetRequestHeader("Range", $"bytes={m_parameters.Offset}-");
                            m_request.SetRequestHeader("If-Range", m_parameters.Validator);
                        }
                        m_native = m_request.SendWebRequest();
                        m_watchdog.Restart();
                    }
                    // 逐帧检查取消，不从 CancellationToken 的工作线程回调调用 Unity API。
                    if (m_token.IsCancellationRequested && !m_aborted && !m_native.isDone) { m_aborted = true; m_request.Abort(); }
                    if (m_lastReceived != m_request.downloadedBytes) { m_lastReceived = m_request.downloadedBytes; m_watchdog.Restart(); }
                    if (!m_native.isDone && !m_aborted && !m_stalled && m_parameters.NetworkPolicy.WatchdogTimeoutSeconds > 0 &&
                        m_watchdog.Elapsed.TotalSeconds >= m_parameters.NetworkPolicy.WatchdogTimeoutSeconds) { m_stalled = true; m_request.Abort(); }
                    Progress = m_native.progress;
                    m_onProgress?.Invoke((long)m_request.downloadedBytes);
                    if (!m_native.isDone) {
                        if (m_parameters.Checkpoint != null && m_handler.Length > m_checkpointBytes &&
                            (m_handler.Length - m_checkpointBytes >= 1024 * 1024 || m_checkpointClock.Elapsed.TotalSeconds >= 1)) {
                            m_handler.FlushCheckpoint();
                            m_parameters.Checkpoint(ReadResponse(), m_handler.Length);
                            m_checkpointBytes = m_handler.Length; m_checkpointClock.Restart();
                        }
                        return;
                    }
                    DownloadTransportResponse response = ReadResponse();
                    Exception error = m_handler.Error;
                    m_handler.Close();
                    m_request.Dispose(); m_request = null; m_native = null;
                    if (error != null) {
                        throw error;
                    }

                    Succeed(response);
                }
                catch {
                    m_handler?.Close();
                    m_request?.Dispose(); m_request = null; m_native = null;
                    throw;
                }
            }

            private DownloadTransportResponse ReadResponse()
            {
                long.TryParse(m_request.GetResponseHeader("Content-Length"), out var length);
                return new DownloadTransportResponse
                {
                    StatusCode = m_request.responseCode,
                    Error = m_stalled ? "下载长时间没有收到数据。" : m_request.result == UnityWebRequest.Result.Success ? null : m_request.error,
                    Cancelled = m_aborted || m_token.IsCancellationRequested,
                    ETag = m_request.GetResponseHeader("ETag"),
                    LastModified = m_request.GetResponseHeader("Last-Modified"),
                    ContentRange = m_request.GetResponseHeader("Content-Range"),
                    ContentLength = m_request.GetResponseHeader("Content-Length") == null ? -1 : length,
                    ContentEncoding = m_request.GetResponseHeader("Content-Encoding"),
                    RetryAfter = m_request.GetResponseHeader("Retry-After"),
                    ExceededLimit = m_handler.Exceeded
                };
            }
        }

        // 单个固定接收缓冲；先检查长度再写入，错误响应和 chunked 响应也不能填满磁盘。
        private sealed class BoundedFileDownload: DownloadHandlerScript
        {
            private readonly FileStream m_file;
            private readonly long m_limit;
            internal long Length { get; private set; }
            internal bool Exceeded { get; private set; }
            internal Exception Error { get; private set; }
            private BoundedFileDownload(FileStream file, long limit) : base(new byte[64 * 1024])
            { m_limit = limit; m_file = file; }
            internal static BoundedFileDownload Create(string path, long limit)
            {
                // 文件打开失败时不能留下尚未完成构造的 Unity 原生下载处理器。
                var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                try { return new BoundedFileDownload(file, limit); }
                catch { file.Dispose(); throw; }
            }
            protected override bool ReceiveData(byte[] data, int count)
            {
                if (data == null || count < 0 || count > m_limit - Length) { Exceeded = true; return false; }
                try { m_file.Write(data, 0, count); Length += count; return true; }
                catch (IOException error) {
                    Error = ResourceDiskPolicy.IsDiskFull(error) ? new ResourceInsufficientSpaceException(count, 0, error) : error;
                    return false;
                }
                catch (Exception error) { Error = error; return false; }
            }
            internal void FlushCheckpoint()
            {
                m_file.Flush(true);
            }

            internal void Close()
            {
                m_file.Dispose();
            }
        }
    }
}

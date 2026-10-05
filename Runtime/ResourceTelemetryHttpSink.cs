using System;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace ZRAsset
{
    public interface IResourceTelemetryTransport
    {
        ResourceOperationBase<long> SendAsync(byte[] utf8Json, CancellationToken token);
    }

    /// <summary>HTTPS JSON 批量上报。凭证由应用配置，不从资源下载请求复制。</summary>
    public sealed class ResourceTelemetryHttpTransport: IResourceTelemetryTransport
    {
        private sealed class ResponseLimit: DownloadHandlerScript
        {
            private int m_received;
            internal ResponseLimit() : base(new byte[4096]) { }
            protected override bool ReceiveData(byte[] data, int count)
            {
                m_received += count;
                return count >= 0 && m_received <= 16384;
            }
        }
        private readonly string m_endpoint;
        private readonly string m_token;

        public ResourceTelemetryHttpTransport(string endpoint, string token = null, bool allowHttpLoopback = false)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Fragment) || (uri.Scheme != Uri.UriSchemeHttps &&
                !(allowHttpLoopback && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp))) {
                throw new ArgumentException("遥测端点必须为 HTTPS；本地测试可显式允许 HTTP loopback。");
            }
            m_endpoint = uri.AbsoluteUri;
            m_token = token;
        }

        public async ResourceOperationBase<long> SendAsync(byte[] utf8Json, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var request = new UnityWebRequest(m_endpoint, "POST", new ResponseLimit(), new UploadHandlerRaw(utf8Json));
            request.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(m_token)) {
                request.SetRequestHeader("Authorization", "Bearer " + m_token);
            }

            request.timeout = 15;
            request.redirectLimit = 0;
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            while (!operation.isDone) {
                if (token.IsCancellationRequested) {
                    request.Abort();
                }

                await ResourceOperationBase.Yield();
            }
            token.ThrowIfCancellationRequested();
            return request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.DataProcessingError ? 0 : request.responseCode;
        }
    }

    /// <summary>固定容量事件缓冲、单批在途、指数退避；可选持久化在途批次，重启后保持 BatchId 重试。</summary>
    public sealed class ResourceTelemetryHttpSink: IResourceTelemetrySink, IResourceTelemetryPump, IDisposable
    {
        [Serializable]
        private sealed class EventData
        {
            public int Kind;
            public string PackageName;
            public string PackageVersion;
            public string Address;
            public int ErrorCode;
            public int Stage;
            public long ResponseCode;
            public double DurationMilliseconds;
            public long Bytes;
            public int Attempt;
        }

        [Serializable]
        private sealed class Batch
        {
            public int Format = 1;
            public string BatchId;
            public string ApplicationId;
            public long CreatedUtcSeconds;
            public EventData[] Events;
        }

        private readonly IResourceTelemetryTransport m_transport;
        private readonly string m_applicationId;
        private readonly string m_spoolPath;
        private readonly ResourceFileLock m_spoolLock;
        private readonly ResourceTelemetryEvent[] m_events;
        private readonly int m_batchSize;
        private readonly int m_maxAttempts;
        private readonly double m_interval;
        private readonly CancellationTokenSource m_cancellation = new();
        private ResourceOperationBase<long> m_sending;
        private ResourceOperationBase<bool> m_persisting;
        private ResourceOperationBase m_disposal;
        private byte[] m_batch;
        private int m_head;
        private int m_attempt;
        private double m_nextSend;
        private bool m_disposed;
        private bool m_flush;
        private bool m_captureExceptions;

        public long DroppedCount { get; private set; }
        public long SentBatchCount { get; private set; }
        public long FailedBatchCount { get; private set; }
        public int PendingCount { get; private set; }
        public bool HasPendingBatch { get { return m_batch != null; } }

        public ResourceTelemetryHttpSink(IResourceTelemetryTransport transport, string applicationId, string spoolDirectory = null,
            int capacity = 512, int batchSize = 32, double intervalSeconds = 5, int maxAttempts = 8)
        {
            ResourcePackages.CheckThread();
            if (string.IsNullOrEmpty(applicationId) || applicationId.Length > 128) {
                throw new ArgumentException(nameof(applicationId));
            }

            if (capacity < 1 || capacity > 65536 || batchSize < 1 || batchSize > 128 || batchSize > capacity ||
                !double.IsFinite(intervalSeconds) || intervalSeconds < 0 || maxAttempts < 1 || maxAttempts > 32) {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            m_transport = transport ?? throw new ArgumentNullException(nameof(transport));
            m_applicationId = applicationId;
            m_events = new ResourceTelemetryEvent[capacity];
            m_batchSize = batchSize;
            m_interval = intervalSeconds;
            m_maxAttempts = maxAttempts;
            m_nextSend = Time.realtimeSinceStartupAsDouble + intervalSeconds;
            if (spoolDirectory != null) {
                var root = Path.GetFullPath(spoolDirectory);
                Directory.CreateDirectory(root);
                m_spoolPath = DownloadStorage.ValidatePath(root, Path.Combine(root, "pending.json"));
                m_spoolLock = ResourceFileLock.Open(DownloadStorage.ValidatePath(root, Path.Combine(root, "telemetry.lock")));
                try {
                    if (File.Exists(m_spoolPath)) {
                        if (new FileInfo(m_spoolPath).Length > 1024 * 1024) {
                            throw new InvalidDataException("遥测缓存批次过大。");
                        }

                        var json = File.ReadAllText(m_spoolPath);
                        Batch saved = JsonUtility.FromJson<Batch>(json);
                        if (saved == null || saved.Format != 1 || saved.ApplicationId != applicationId ||
                            !Guid.TryParseExact(saved.BatchId, "N", out _) || saved.Events == null || saved.Events.Length > 128) {
                            throw new InvalidDataException("遥测缓存批次身份无效。");
                        }
                        m_batch = Encoding.UTF8.GetBytes(json);
                    }
                }
                catch {
                    m_spoolLock.Dispose();
                    throw;
                }
            }
        }

        /// <summary>仅记录异常类别，不上传日志正文、堆栈或用户数据；原生崩溃由宿主 SDK 负责。</summary>
        public void CaptureManagedExceptions(bool enabled)
        {
            Check();
            if (m_captureExceptions == enabled) {
                return;
            }

            m_captureExceptions = enabled;
            if (enabled) {
                Application.logMessageReceived += OnLog;
            }
            else {
                Application.logMessageReceived -= OnLog;
            }
        }

        private void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Assert) {
                Emit(new ResourceTelemetryEvent(ResourceTelemetryKind.ManagedException,
                    failure: new ResourceFailure(ResourceErrorCode.Unknown)));
            }
        }

        public void Emit(in ResourceTelemetryEvent value)
        {
            Check();
            if (PendingCount == m_events.Length) {
                m_events[m_head] = default;
                m_head = (m_head + 1) % m_events.Length;
                PendingCount--;
                DroppedCount++;
            }
            m_events[(m_head + PendingCount) % m_events.Length] = value;
            PendingCount++;
        }

        public void Update()
        {
            Check();
            var now = Time.realtimeSinceStartupAsDouble;
            if (m_sending != null) {
                if (!m_sending.IsDone) {
                    return;
                }

                var status = m_sending.Status == OperationStatus.Succeeded ? m_sending.Result : 0;
                m_sending = null;
                if (status >= 200 && status <= 299) {
                    FinishBatch(true);
                }
                else if (m_attempt >= m_maxAttempts || (status >= 400 && status < 500 && status != 408 && status != 429)) {
                    FinishBatch(false);
                }
                else {
                    m_nextSend = now + Math.Min(60, Math.Pow(2, m_attempt - 1));
                    return;
                }
            }
            if (m_batch == null) {
                if (PendingCount == 0 || (!m_flush && PendingCount < m_batchSize && now < m_nextSend)) {
                    return;
                }

                BuildBatch();
                m_attempt = 0;
                m_nextSend = now;
            }
            if (m_persisting != null) {
                if (!m_persisting.IsDone) {
                    return;
                }
                // No network send before durable spooling; failures remain visible to FlushAsync.
                m_persisting.GetAwaiter().GetResult();
                m_persisting = null;
            }
            if (now < m_nextSend) {
                return;
            }

            m_attempt++;
            try {
                m_sending = m_transport.SendAsync(m_batch, m_cancellation.Token);
            }
            catch {
                if (m_attempt >= m_maxAttempts) {
                    FinishBatch(false);
                }
                else {
                    m_nextSend = now + Math.Min(60, Math.Pow(2, m_attempt - 1));
                }
            }
        }

        public async ResourceOperationBase FlushAsync(CancellationToken token = default)
        {
            Check();
            if (m_flush) {
                throw new InvalidOperationException("已有遥测 Flush 正在执行。");
            }

            m_flush = true;
            try {
                do {
                    token.ThrowIfCancellationRequested();
                    Update();
                    if (PendingCount != 0 || m_batch != null) {
                        await ResourceOperationBase.Yield();
                    }
                } while (PendingCount != 0 || m_batch != null);
            }
            finally {
                m_flush = false;
            }
        }

        private void BuildBatch()
        {
            var count = Math.Min(PendingCount, m_batchSize);
            var items = new EventData[count];
            for (var i = 0; i < count; i++) {
                ResourceTelemetryEvent value = m_events[(m_head + i) % m_events.Length];
                items[i] = new EventData
                {
                    Kind = (int)value.Kind,
                    PackageName = Limit(value.PackageName),
                    PackageVersion = Limit(value.PackageVersion),
                    Address = Limit(value.Address),
                    ErrorCode = (int)value.Failure.Code,
                    Stage = (int)value.Failure.Stage,
                    ResponseCode = value.Failure.ResponseCode,
                    DurationMilliseconds = double.IsFinite(value.DurationMilliseconds) ? Math.Max(0, value.DurationMilliseconds) : 0,
                    Bytes = Math.Max(0, value.Bytes),
                    Attempt = Math.Max(0, value.Attempt)
                };
            }
            var json = JsonUtility.ToJson(new Batch
            {
                BatchId = Guid.NewGuid().ToString("N"),
                ApplicationId = m_applicationId,
                CreatedUtcSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Events = items
            });
            if (m_spoolPath != null) {
                m_persisting = ResourceFileIO.Shared.WriteAtomicAsync(m_spoolPath, json);
            }

            m_batch = Encoding.UTF8.GetBytes(json);
            for (var i = 0; i < count; i++) {
                m_events[m_head] = default;
                m_head = (m_head + 1) % m_events.Length;
            }
            PendingCount -= count;
        }

        private void FinishBatch(bool success)
        {
            if (m_spoolPath != null) {
                DownloadStorage.DeleteFile(m_spoolPath);
            }

            if (success) {
                SentBatchCount++;
            }
            else {
                FailedBatchCount++;
            }

            m_batch = null;
            m_attempt = 0;
            m_nextSend = Time.realtimeSinceStartupAsDouble + m_interval;
        }

        private static string Limit(string value)
        {
            return value == null || value.Length <= 256 ? value : value.Substring(0, 256);
        }

        public void Dispose()
        {
            if (m_disposed) {
                return;
            }

            Check();
            CaptureManagedExceptions(false);
            // 取消在途请求；已有持久化批次保留供下次会话重发。
            m_disposed = true;
            m_cancellation.Cancel();
            m_disposal = CompleteDisposalAsync();
        }

        /// <summary>等待已排队的持久化完成再释放缓存锁；需立即重开同一目录时必须等待。</summary>
        public ResourceOperationBase DisposeAsync()
        {
            Dispose();
            return m_disposal ?? ResourceOperationBase.CompletedOperation;
        }

        private async ResourceOperationBase CompleteDisposalAsync()
        {
            try {
                if (m_persisting != null) {
                    await m_persisting;
                }
            }
            finally { m_spoolLock?.Dispose(); m_cancellation.Dispose(); }
        }

        private void Check()
        {
            ResourcePackages.CheckThread();
            if (m_disposed) {
                throw new ObjectDisposedException(nameof(ResourceTelemetryHttpSink));
            }
        }
    }
}

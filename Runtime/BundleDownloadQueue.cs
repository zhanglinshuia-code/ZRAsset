using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>数字越小越先下载；正在传输的任务不会被高优先级请求抢占。</summary>
    public enum DownloadPriority { Critical = 0, High = 1, Normal = 2, Low = 3 }
    public enum BundleDownloadState { Queued, Downloading, Verifying, Succeeded, Failed, Cancelled }

    /// <summary>单个 Bundle 的进度快照，接收字节数包含已经确认可续传的本地断点。</summary>
    public readonly struct BundleDownloadProgress
    {
        public readonly string BundleName;
        public readonly BundleDownloadState State;
        public readonly long ReceivedBytes;
        public readonly long TotalBytes;
        public readonly int Attempt;
        public readonly string Error;
        public float Progress
        {
            get
            {
                return TotalBytes <= 0 ? 0 : Mathf.Clamp01((float)ReceivedBytes / TotalBytes);
            }
        }

        public BundleDownloadProgress(string name, BundleDownloadState state, long received, long total, int attempt, string error = null)
        { BundleName = name; State = state; ReceivedBytes = received; TotalBytes = total; Attempt = attempt; Error = error; }
    }

    /// <summary>下载选项在创建后不可变；缓存根之外的任何路径都不会交给传输实现。</summary>
    public sealed class BundleDownloadOptions
    {
        public string RemoteBaseUrl { get; }
        public string CacheRoot { get; }
        public string CacheVersion { get; }
        public int MaxConcurrentDownloads { get; }
        public int RequestTimeoutSeconds { get; }
        public ResourceRetryPolicy RetryPolicy { get; }
        public ResourceDiskPolicy DiskPolicy { get; }
        public ResourceDownloadPolicy NetworkPolicy { get; }
        public ResourceSourceOptions SourceOptions { get; }

        public BundleDownloadOptions(string remoteBaseUrl, string cacheVersion,
            string cacheRoot = null, int maxConcurrentDownloads = 3, int requestTimeoutSeconds = 60,
            ResourceRetryPolicy retryPolicy = null, ResourceDiskPolicy diskPolicy = null, ResourceDownloadPolicy networkPolicy = null,
            ResourceSourceOptions sourceOptions = null)
        {
            ResourcePlatform.RequireDiskDownloads();
            if (!Uri.TryCreate(remoteBaseUrl, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) {
                throw new ArgumentException("远端根地址必须是无查询参数和片段的 HTTP 或 HTTPS URL。", nameof(remoteBaseUrl));
            }

            if (!IsSafeSegment(cacheVersion)) {
                throw new ArgumentException("缓存版本必须是安全的目录名。", nameof(cacheVersion));
            }

            if (DownloadStorage.PathComparer.Equals(cacheVersion, CacheDestinationLock.InfrastructureDirectory)) {
                throw new ArgumentException("缓存版本不能使用内部写入锁目录名称。", nameof(cacheVersion));
            }

            if (maxConcurrentDownloads < 1 || maxConcurrentDownloads > 32) {
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentDownloads));
            }

            if (requestTimeoutSeconds < 1 || requestTimeoutSeconds > 600) {
                throw new ArgumentOutOfRangeException(nameof(requestTimeoutSeconds));
            }

            RemoteBaseUrl = remoteBaseUrl.TrimEnd('/') + "/";
            CacheVersion = cacheVersion;
            CacheRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(cacheRoot)
                ? Path.Combine(Application.persistentDataPath, "ZRAssetCache") : cacheRoot);
            MaxConcurrentDownloads = maxConcurrentDownloads;
            RequestTimeoutSeconds = requestTimeoutSeconds;
            RetryPolicy = retryPolicy ?? new ResourceRetryPolicy(2, 0.5);
            DiskPolicy = diskPolicy ?? new ResourceDiskPolicy();
            NetworkPolicy = networkPolicy ?? new ResourceDownloadPolicy();
            SourceOptions = sourceOptions;
            ResourcePersistence.RegisterCacheRoot(CacheRoot);
        }

        internal BundleDownloadOptions ForVersion(string version)
        {
            return new BundleDownloadOptions(RemoteBaseUrl, version,
            CacheRoot, MaxConcurrentDownloads, RequestTimeoutSeconds, RetryPolicy, DiskPolicy, NetworkPolicy, SourceOptions);
        }

        internal static bool IsSafeSegment(string value)
        {
            return DownloadStorage.IsSafeSegment(value);
        }
    }

    /// <summary>
    /// 主线程下载队列：同一目标文件只允许一种内容身份，多个消费者共享一个任务。
    /// 取消一个消费者只解除该消费者；最后一个消费者离开时才取消传输。
    /// 完整响应先写 incoming，检查协议后合并 part，最后校验 SHA-256 并同目录改名发布。
    /// </summary>
    public sealed class BundleDownloadQueue
    {
        private sealed class Job
        {
            public BundleInfo Info;
            public string Url, SourceUrl, Destination, PartialPath, MetadataPath, IncomingPath;
            public DownloadPriority Priority;
            public long Sequence, Received;
            public long StartedTimestamp;
            public int Attempt, Waiters;
            public bool Finished;
            public bool Started, HoldsSlot;
            public OperationCompletionSource<bool> Resume;
            public double LastProgressTime;
            public TimeSpan RetryDelay;
            public BundleDownloadState State;
            public readonly CancellationTokenSource Cancellation = new();
            public readonly OperationCompletionSource<string> Completion = new();
        }

        [Serializable]
        private sealed class PartialMetadata
        {
            public string Url, Sha256, Validator;
            public long Size;
        }
        [Serializable]
        private sealed class IncomingCheckpoint
        {
            public string Url, Sha256, Validator;
            public long Size, Offset, Length;
        }

        private readonly List<Job> m_pending = new();
        private readonly Dictionary<string, Job> m_jobs = new(DownloadStorage.PathComparer);
        private readonly Dictionary<string, BundleDownloadProgress> m_snapshots = new(DownloadStorage.PathComparer);
        private readonly BundleDownloadOptions m_options;
        private readonly IDownloadTransport m_transport;
        private readonly CacheUsageLease m_usageLease;
        private readonly int m_threadId;
        private long m_sequence;
        private int m_active;
        private int m_waitingRetries;
        private bool m_disposed;
        private bool m_paused;
        private long m_requestTick = -1;
        private int m_requestsThisTick;
        private ResourceOperationBase m_disposalTask;
        private Action<BundleDownloadProgress> m_progressChanged;
        private Delegate[] m_progressListeners = Array.Empty<Delegate>();
        public event Action<BundleDownloadProgress> ProgressChanged
        {
            add { CheckThread(); m_progressChanged += value; m_progressListeners = m_progressChanged?.GetInvocationList() ?? Array.Empty<Delegate>(); }
            remove { CheckThread(); m_progressChanged -= value; m_progressListeners = m_progressChanged?.GetInvocationList() ?? Array.Empty<Delegate>(); }
        }
        public int ActiveDownloads { get { CheckThread(); return m_active; } }
        public int QueuedDownloads { get { CheckThread(); return m_pending.Count + m_waitingRetries; } }
        public bool IsPaused { get { CheckThread(); return m_paused; } }
        /// <summary>暂停后不启动新 HTTP 请求；已经传输的请求正常完成。</summary>
        public void Pause() { CheckThread(); if (m_disposed) { throw new ObjectDisposedException(nameof(BundleDownloadQueue)); } m_paused = true; }
        public void Resume() { CheckThread(); if (m_disposed) { throw new ObjectDisposedException(nameof(BundleDownloadQueue)); } m_paused = false; Pump(); }

        public BundleDownloadQueue(BundleDownloadOptions options, IDownloadTransport transport = null)
        {
            m_options = options ?? throw new ArgumentNullException(nameof(options));
            m_transport = transport ?? options.NetworkPolicy.TransportFactory?.Invoke() ?? new UnityWebRequestDownloadTransport();
            m_threadId = Thread.CurrentThread.ManagedThreadId;
            // 队列整个生命周期都算缓存使用者，包括尚未开始下载但以后可能请求的旧版本。
            m_usageLease = CacheUsageLease.Acquire(options.CacheRoot);
        }

        internal IDisposable AcquireCacheUsageLease()
        {
            CheckThread();
            return m_disposed ? throw new ObjectDisposedException(nameof(BundleDownloadQueue)) : (IDisposable)CacheUsageLease.Acquire(m_options.CacheRoot);
        }

        public IReadOnlyList<BundleDownloadProgress> GetProgress()
        { CheckThread(); return m_snapshots.Values.ToArray(); }

        // 只查询在途工作，不把之前的成功快照误计为本次任务组的网络下载。
        internal bool TryGetActiveProgress(BundleInfo info, out BundleDownloadProgress progress)
        {
            CheckThread();
            foreach (Job job in m_jobs.Values) {
                if (!job.Finished && job.Info.Name == info.Name && job.Info.Size == info.Size &&
                    string.Equals(job.Info.Sha256, info.Sha256, StringComparison.OrdinalIgnoreCase)) {
                    progress = new BundleDownloadProgress(info.Name, job.State, job.Received, info.Size, job.Attempt);
                    return true;
                }
            }
            progress = default;
            return false;
        }

        public async ResourceOperationBase<string> DownloadAsync(BundleInfo info, string url, string destination,
            DownloadPriority priority = DownloadPriority.Normal, CancellationToken cancellationToken = default)
        {
            CheckThread();
            if (m_disposed) {
                throw new ObjectDisposedException(nameof(BundleDownloadQueue));
            }

            cancellationToken.ThrowIfCancellationRequested();
            ValidateInfo(info);
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
                throw new ArgumentException("Bundle 下载地址必须是 HTTP 或 HTTPS URL。", nameof(url));
            }

            if (!Enum.IsDefined(typeof(DownloadPriority), priority)) {
                throw new ArgumentOutOfRangeException(nameof(priority));
            }

            var fullDestination = DownloadStorage.ValidatePath(m_options.CacheRoot, destination);
            if (m_jobs.TryGetValue(fullDestination, out Job job)) {
                if (job.Info.Size != info.Size || !string.Equals(job.Info.Sha256, info.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(job.SourceUrl, url, StringComparison.Ordinal)) {
                    throw new InvalidOperationException($"同一缓存目标已有不同内容或来源的下载：{info.Name}");
                }

                if (job.Cancellation.IsCancellationRequested) {
                    // 最后一位旧消费者已取消，先等旧写入者完全退出，不能与新请求同时写 part。
                    try { await ResourceOperations.WaitAsync(job.Completion.Operation, cancellationToken); }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
                    return await DownloadAsync(info, url, destination, priority, cancellationToken);
                }
                if ((int)priority < (int)job.Priority) {
                    job.Priority = priority;
                }

                job.Waiters++;
            }
            else {
                job = new Job
                {
                    // 复制调用方可变的 BundleInfo，防止下载期间清单被改动导致校验身份漂移。
                    Info = new BundleInfo { Name = info.Name, Size = info.Size, Sha256 = info.Sha256.ToLowerInvariant(), Crc = info.Crc, FileType = info.FileType },
                    Url = url,
                    SourceUrl = url,
                    Destination = fullDestination,
                    PartialPath = fullDestination + ".part",
                    MetadataPath = fullDestination + ".part.json",
                    IncomingPath = fullDestination + ".part.incoming",
                    Priority = priority,
                    Sequence = m_sequence++,
                    State = BundleDownloadState.Queued,
                    Waiters = 1
                };
                m_jobs.Add(fullDestination, job);
                m_pending.Add(job);

                Publish(job);
                Pump();
            }
            try {
                await ResourceOperations.WaitAsync(job.Completion.Operation, cancellationToken);
                return await job.Completion.Operation;
            }
            finally {
                CheckThread();
                job.Waiters--;
                if (job.Waiters == 0 && !job.Finished) {
                    job.Cancellation.Cancel();
                    if (m_pending.Remove(job)) {
                        CancelPending(job);
                    }
                }
            }
        }

        /// <summary>停止接受请求，取消排队和活动传输，并等待文件句柄与临时文件收尾完成。</summary>
        public ResourceOperationBase DisposeAsync()
        {
            CheckThread();
            if (m_disposalTask != null) {
                return m_disposalTask;
            }

            m_disposed = true;
            var completion = new OperationCompletionSource<bool>();
            m_disposalTask = completion.Operation;
            _ = CompleteDisposalAsync(completion);
            return m_disposalTask;
        }

        private async ResourceOperationBase CompleteDisposalAsync(OperationCompletionSource<bool> completion)
        {
            try { await DisposeCoreAsync(); completion.TrySetResult(true); }
            catch (Exception error) { completion.TrySetException(error); }
        }

        private async ResourceOperationBase DisposeCoreAsync()
        {
            List<Exception> cancellationErrors = null;
            try {
                Job[] current = m_jobs.Values.ToArray();
                foreach (Job job in current) {
                    // SDK 取消回调失败不能跳过其余传输，也不能提前释放缓存维护保护。
                    try { job.Cancellation.Cancel(); }
                    catch (Exception error) { (cancellationErrors ??= new List<Exception>()).Add(error); }
                    if (m_pending.Remove(job)) {
                        CancelPending(job);
                    }
                }
                try { await ResourceOperationBase.WhenAll(current.Select(job => job.Completion.Operation)); }
                catch (Exception) { /* 关闭操作等待所有终态，原始错误仍由每个请求传播给消费者。 */ }
            }
            finally { m_usageLease.Dispose(); }
            if (cancellationErrors != null) { throw new AggregateException("下载已排空，但取消回调失败。", cancellationErrors); }
        }

        private void FinishQueuedCancellation(Job job)
        {
            job.Finished = true;
            m_jobs.Remove(job.Destination);
            job.State = BundleDownloadState.Cancelled;
            Publish(job);
            job.Completion.TrySetCanceled();
            job.Cancellation.Dispose();
        }

        private void CancelPending(Job job)
        {
            if (job.Started) { job.Resume.TrySetCanceled(); }
            else { FinishQueuedCancellation(job); }
        }

        private void ReleaseSlot(Job job)
        {
            if (!job.HoldsSlot) { return; }
            job.HoldsSlot = false;
            m_active--;
        }

        private async ResourceOperationBase WaitForRetryAsync(Job job, string error)
        {
            var queued = false;
            job.Resume = new OperationCompletionSource<bool>();
            job.State = BundleDownloadState.Queued;
            m_waitingRetries++;
            ReleaseSlot(job);
            Publish(job, error);
            Pump();
            try {
                if (job.RetryDelay > TimeSpan.Zero) { await ResourceOperationBase.Delay(job.RetryDelay, job.Cancellation.Token); }
                else { await ResourceOperationBase.Yield(); }
                job.Cancellation.Token.ThrowIfCancellationRequested();
                m_waitingRetries--;
                queued = true;
                m_pending.Add(job);
                Pump();
                await ResourceOperations.WaitAsync(job.Resume.Operation, job.Cancellation.Token);
            }
            finally {
                if (!queued) { m_waitingRetries--; }
                if (m_pending.Remove(job)) { job.Resume.TrySetCanceled(); }
                job.Resume = null;
            }
        }

        private void Pump()
        {
            CheckThread();
            while (!m_disposed && !m_paused && m_active < m_options.MaxConcurrentDownloads && m_pending.Count > 0) {
                var best = 0;
                for (var i = 1; i < m_pending.Count; i++) {
                    if (m_pending[i].Priority < m_pending[best].Priority ||
                        (m_pending[i].Priority == m_pending[best].Priority && m_pending[i].Sequence < m_pending[best].Sequence)) {
                        best = i;
                    }
                }

                Job next = m_pending[best];
                m_pending.RemoveAt(best);
                m_active++;
                next.HoldsSlot = true;
                if (next.Started) { next.Resume.TrySetResult(true); }
                else { next.Started = true; _ = RunAsync(next); }
            }
        }

        private async ResourceOperationBase RunAsync(Job job)
        {
            // 保证 DownloadAsync 已建立消费者等待，且同步失败不会递归占满调用栈。
            await ResourceOperationBase.Yield();
            job.StartedTimestamp = ResourceTelemetry.StartTimer();
            ResourceFileLock destinationLock = null;
            try {
                CheckThread();
                job.Cancellation.Token.ThrowIfCancellationRequested();
                DownloadStorage.ValidatePath(m_options.CacheRoot, job.Destination);
                destinationLock = await CacheDestinationLock.AcquireAsync(m_options.CacheRoot, job.Destination, job.Cancellation.Token);
                // 目标锁位于独立目录；真正写入前仍须创建并检查目标包目录。
                DownloadStorage.ValidatePath(m_options.CacheRoot, job.Destination);
                Directory.CreateDirectory(Path.GetDirectoryName(job.Destination));
                CacheOwnership.Register(m_options.CacheRoot, job.Destination, job.Info);
                if (await IsVerifiedAsync(job.Destination, job.Info, job.Cancellation.Token)) {
                    ClearPartial(job);
                    Succeed(job);
                    return;
                }
                await RecoverIncomingAsync(job);
                for (job.Attempt = 1; ; job.Attempt++) {
                    try {
                        job.Cancellation.Token.ThrowIfCancellationRequested();
                        await DownloadAttemptAsync(job);
                        job.Cancellation.Token.ThrowIfCancellationRequested();
                        job.State = BundleDownloadState.Verifying;
                        Publish(job);
                        if (!await IsVerifiedAsync(job.PartialPath, job.Info, job.Cancellation.Token)) {
                            ClearPartial(job);
                            throw ResourceFailure.Annotate(new ResourceDownloadException($"Bundle 文件大小或 SHA-256 不匹配：{job.Info.Name}", true), ResourceErrorCode.Integrity, ResourceStage.Verify);
                        }
                        job.Cancellation.Token.ThrowIfCancellationRequested();
                        DownloadStorage.ValidatePath(m_options.CacheRoot, job.Destination);
                        // 临时文件与目标文件同目录；只有完整性校验通过后才出现可消费的正式文件名。
                        if (File.Exists(job.Destination)) {
                            DownloadStorage.DeleteFile(job.Destination);
                        }

                        File.Move(job.PartialPath, job.Destination);
                        CacheContentIndex.Published(m_options.CacheRoot, job.Destination, job.Info);
                        DownloadStorage.DeleteFile(job.MetadataPath);
                        await ResourcePersistence.FlushAsync();
                        Succeed(job);
                        return;
                    }
                    catch (IOException exception) when (!(exception is ResourceInsufficientSpaceException) &&
                        !ResourceDiskPolicy.IsDiskFull(exception)) {
                        job.Cancellation.Token.ThrowIfCancellationRequested();
                        if (!TryGetRetryDelay(job, exception, out job.RetryDelay)) {
                            throw;
                        }

                        await WaitForRetryAsync(job, exception.Message);
                    }
                }
            }
            catch (OperationCanceledException) {
                job.State = BundleDownloadState.Cancelled;
                ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.Download,
                    packageVersion: m_options.CacheVersion, address: job.Info.Name,
                    failure: new ResourceFailure(ResourceErrorCode.Cancelled, ResourceStage.Download),
                    durationMilliseconds: ResourceTelemetry.Elapsed(job.StartedTimestamp), bytes: job.Received, attempt: job.Attempt));
                Publish(job);
                job.Completion.TrySetCanceled();
            }
            catch (Exception exception) {
                if (exception is IOException io && ResourceDiskPolicy.IsDiskFull(io)) {
                    exception = new ResourceInsufficientSpaceException(job.Info.Size, 0, io);
                }

                if (exception is ResourceDownloadException downloadError) {
                    ResourceFailure.Annotate(exception, ResourceFailure.HttpErrorCode(downloadError.ResponseCode, downloadError.Retryable), ResourceStage.Download, downloadError.ResponseCode);
                }
                ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.Download,
                    packageVersion: m_options.CacheVersion, address: job.Info.Name,
                    failure: ResourceFailure.FromException(exception, ResourceStage.Download), durationMilliseconds: ResourceTelemetry.Elapsed(job.StartedTimestamp), bytes: job.Received, attempt: job.Attempt));
                job.State = BundleDownloadState.Failed;
                Publish(job, exception.Message);
                job.Completion.TrySetException(exception);
            }
            finally {
                // 有持久化检查点的 incoming 留给下次恢复验证，空间不足或取消不能丢弃它。
                // 无检查点的 incoming 尚未经过协议判断，退出时丢弃；二者均不能直接加载。
                // 等目标锁时取消的任务不拥有此 incoming，不能删除另一个进程仍在写入的文件。
                try {
                    if (destinationLock != null && !File.Exists(job.IncomingPath + ".json")) {
                        DownloadStorage.DeleteFile(job.IncomingPath);
                    }
                }
                catch (IOException) { /* 下一次请求仍会先删除此隔离文件；不隐藏原始下载错误。 */ }
                catch (UnauthorizedAccessException) { }
                job.Finished = true;
                m_jobs.Remove(job.Destination);
                ReleaseSlot(job);
                job.Cancellation.Dispose();
                destinationLock?.Dispose();
                Pump();
            }
        }

        private bool TryGetRetryDelay(Job job, IOException error, out TimeSpan delay)
        {
            return ResourceDownloadRetry.TryGetDelay(job.Info.Name, job.Url, job.Attempt, error,
                m_options.RetryPolicy, m_options.NetworkPolicy.RetryPolicy, out delay);
        }

        private void Succeed(Job job)
        {
            job.Received = job.Info.Size;
            job.State = BundleDownloadState.Succeeded;
            ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.Download,
                packageVersion: m_options.CacheVersion, address: job.Info.Name, durationMilliseconds: ResourceTelemetry.Elapsed(job.StartedTimestamp), bytes: job.Info.Size, attempt: job.Attempt));
            Publish(job);
            job.Completion.TrySetResult(job.Destination);
        }

        private async ResourceOperationBase DownloadAttemptAsync(Job job)
        {
            while (true) {
                job.Cancellation.Token.ThrowIfCancellationRequested();
                if (m_requestTick != OperationSystem.TickIndex) { m_requestTick = OperationSystem.TickIndex; m_requestsThisTick = 0; }
                if (!m_paused && m_requestsThisTick < m_options.NetworkPolicy.MaxRequestsPerFrame) { m_requestsThisTick++; break; }
                await ResourceOperationBase.Yield();
            }
            var url = m_options.NetworkPolicy.UrlPolicy?.GetUrl(job.SourceUrl, job.Attempt) ?? job.SourceUrl;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri remote) || (remote.Scheme != "http" && remote.Scheme != "https")) {
                throw new InvalidOperationException("下载 URL 策略必须返回 HTTP(S) 地址。");
            }

            if (!string.Equals(job.Url, url, StringComparison.Ordinal)) { ClearPartial(job); job.Url = url; }
            PartialMetadata metadata = ReadPartial(job);
            var offset = metadata == null ? 0 : new FileInfo(job.PartialPath).Length;
            job.Received = offset;
            if (offset == job.Info.Size && File.Exists(job.PartialPath)) {
                return;
            }

            DownloadStorage.DeleteFile(job.IncomingPath);
            // 新下载只写一份 incoming，再改名；续传还需覆盖追加峰值及服务器忽略 Range 的完整响应。
            // 已完整的 part 直接进入校验，不需要再次预留整个文件；重试等待期间释放预留。
            var required = offset == 0 ? job.Info.Size : Math.Max(job.Info.Size, checked((job.Info.Size - offset) * 2));
            using IDisposable reservation = m_options.DiskPolicy.Reserve(m_options.CacheRoot, required,
                (job.IncomingPath, 0), (job.PartialPath, offset));
            job.State = BundleDownloadState.Downloading;
            Publish(job);
            DownloadTransportResponse response = await m_transport.SendAsync(
                new DownloadTransportRequest(job.Url, job.IncomingPath, offset, metadata?.Validator, m_options.RequestTimeoutSeconds,
                    job.Info.Size, (headers, length) => SaveCheckpoint(job, metadata, offset, headers, length), m_options.NetworkPolicy, job.SourceUrl),
                bytes => { job.Received = Math.Min(job.Info.Size, offset + Math.Max(0, bytes)); Publish(job, progressOnly: true); },
                job.Cancellation.Token) ?? throw new IOException("传输实现没有返回 HTTP 响应。");
            DownloadStorage.DeleteFile(job.IncomingPath + ".json");
            var canceled = response.Cancelled || job.Cancellation.IsCancellationRequested;
            if (response.StatusCode != 200 && response.StatusCode != 206) {
                DownloadStorage.DeleteFile(job.IncomingPath);
                if (canceled) {
                    throw new OperationCanceledException(job.Cancellation.Token);
                }

                var retryable = response.StatusCode == 0 || response.StatusCode == 408 || response.StatusCode == 429 || response.StatusCode >= 500;
                if (!retryable) {
                    ClearPartial(job);
                }

                throw new ResourceDownloadException($"Bundle 下载失败（HTTP {response.StatusCode}）：{response.Error}", retryable, response.RetryAfter, response.StatusCode);
            }
            // 错误页仍受传输大小限制，但 429/503 等错误页的截断不能覆盖 HTTP 重试策略。
            if (response.ExceededLimit) {
                ClearPartial(job);
                if (canceled) {
                    throw new OperationCanceledException(job.Cancellation.Token);
                }

                throw new ResourceDownloadException("HTTP 响应超过 Bundle 字节上限。", false);
            }
            if (!string.IsNullOrEmpty(response.ContentEncoding) && !string.Equals(response.ContentEncoding, "identity", StringComparison.OrdinalIgnoreCase)) {
                ClearPartial(job);
                throw new ResourceDownloadException("Bundle 端点不能使用改变字节偏移的 Content-Encoding；请关闭 CDN 自动压缩。", false);
            }
            if (response.StatusCode == 200) {
                // Range 被忽略或 If-Range 不再匹配时，200 响应本身就是从头下载，不能追加到旧断点。
                ClearPartial(job, false);
                offset = 0;
            }
            else if (!RangeMatches(response.ContentRange, offset, job.Info.Size) ||
                (offset > 0 && !ValidatorMatches(metadata.Validator, response))) {
                ClearPartial(job);
                throw new ResourceDownloadException("服务器返回的 Content-Range 或实体校验标记与本地断点不匹配。", true);
            }
            var expected = job.Info.Size - offset;
            var received = File.Exists(job.IncomingPath) ? new FileInfo(job.IncomingPath).Length : 0;
            if (received > expected || (response.ContentLength >= 0 && response.ContentLength != expected)) {
                ClearPartial(job);
                throw new ResourceDownloadException("HTTP 响应长度与 Bundle 清单不一致。", true);
            }
            var validator = GetValidator(response);
            if (received > 0 || (expected == 0 && File.Exists(job.IncomingPath))) {
                // 先写身份元数据再合并连续前缀；进程中断只可能留下未验证前缀，下次仍需最终 SHA 检查。
                ResourceVersionManager.WriteAtomic(job.MetadataPath, JsonUtility.ToJson(new PartialMetadata
                { Url = job.Url, Sha256 = job.Info.Sha256, Size = job.Info.Size, Validator = validator }));
                if (offset == 0) {
                    File.Move(job.IncomingPath, job.PartialPath);
                }
                else {
                    await DownloadStorage.AppendAsync(job.IncomingPath, job.PartialPath, job.Cancellation.Token);
                    DownloadStorage.DeleteFile(job.IncomingPath);
                }
            }
            else {
                DownloadStorage.DeleteFile(job.IncomingPath);
            }

            job.Received = offset + received;
            // 没有强 ETag 或 Last-Modified 时不跨请求拼接，避免服务器内容变化导致混合实体。
            if (job.Received < job.Info.Size && string.IsNullOrEmpty(validator)) {
                ClearPartial(job);
            }

            if (canceled) {
                throw new OperationCanceledException(job.Cancellation.Token);
            }

            if (job.Received != job.Info.Size || !string.IsNullOrEmpty(response.Error)) {
                throw new ResourceDownloadException($"Bundle 传输中断：{response.Error ?? "响应体不完整"}", true);
            }
        }

        private static void SaveCheckpoint(Job job, PartialMetadata previous, long offset, DownloadTransportResponse response, long length)
        {
            if (response.ExceededLimit || length <= 0 || string.IsNullOrEmpty(GetValidator(response)) ||
                (!string.IsNullOrEmpty(response.ContentEncoding) && !string.Equals(response.ContentEncoding, "identity", StringComparison.OrdinalIgnoreCase))) {
                return;
            }

            if (response.StatusCode == 200) {
                offset = 0;
            }
            else if (response.StatusCode != 206 || !RangeMatches(response.ContentRange, offset, job.Info.Size) ||
                (offset > 0 && (previous == null || !ValidatorMatches(previous.Validator, response)))) {
                return;
            }

            var expected = job.Info.Size - offset;
            if (length > expected || (response.ContentLength >= 0 && response.ContentLength != expected)) {
                return;
            }

            ResourceVersionManager.WriteAtomic(job.IncomingPath + ".json", JsonUtility.ToJson(new IncomingCheckpoint
            { Url = job.Url, Sha256 = job.Info.Sha256, Size = job.Info.Size, Validator = GetValidator(response), Offset = offset, Length = length }));
        }

        private async ResourceOperationBase RecoverIncomingAsync(Job job)
        {
            var path = job.IncomingPath + ".json";
            DownloadStorage.RejectLinks(path); DownloadStorage.RejectLinks(job.IncomingPath);
            IncomingCheckpoint checkpoint = null;
            try {
                if (File.Exists(path) && new FileInfo(path).Length <= 16 * 1024) {
                    checkpoint = JsonUtility.FromJson<IncomingCheckpoint>(File.ReadAllText(path));
                }
            }
            catch (ArgumentException) { }
            var valid = checkpoint != null && checkpoint.Url == job.Url && checkpoint.Size == job.Info.Size &&
                checkpoint.Sha256 == job.Info.Sha256 && IsValidator(checkpoint.Validator) && checkpoint.Offset >= 0 &&
                checkpoint.Length > 0 && checkpoint.Offset < job.Info.Size && checkpoint.Length <= job.Info.Size - checkpoint.Offset &&
                File.Exists(job.IncomingPath) && new FileInfo(job.IncomingPath).Length >= checkpoint.Length;
            if (valid && checkpoint.Offset > 0) {
                PartialMetadata previous = ReadPartial(job, false);
                valid = previous != null && previous.Validator == checkpoint.Validator &&
                    new FileInfo(job.PartialPath).Length >= checkpoint.Offset;
            }
            if (valid) {
                // incoming 已占用的空间不再计算；仅为追加到旧 part 的新增字节保留空间。
                using IDisposable reservation = checkpoint.Offset == 0 ? null : m_options.DiskPolicy.Reserve(m_options.CacheRoot, checkpoint.Length,
                    (job.PartialPath, checkpoint.Offset));
                // 元数据只承认已 fsync 的连续前缀；崩溃后的尾部不参与续传。
                using (var file = new FileStream(job.IncomingPath, FileMode.Open, FileAccess.Write, FileShare.None)) {
                    file.SetLength(checkpoint.Length);
                }

                ResourceVersionManager.WriteAtomic(job.MetadataPath, JsonUtility.ToJson(new PartialMetadata
                { Url = job.Url, Size = job.Info.Size, Sha256 = job.Info.Sha256, Validator = checkpoint.Validator }));
                if (checkpoint.Offset == 0) { DownloadStorage.DeleteFile(job.PartialPath); File.Move(job.IncomingPath, job.PartialPath); }
                else {
                    // 若上一次崩溃发生在合并途中，先截回原偏移，保证重复恢复不重复追加。
                    using (var file = new FileStream(job.PartialPath, FileMode.Open, FileAccess.Write, FileShare.None)) {
                        file.SetLength(checkpoint.Offset);
                    }

                    await DownloadStorage.AppendAsync(job.IncomingPath, job.PartialPath, job.Cancellation.Token);
                }
            }
            DownloadStorage.DeleteFile(path);
            DownloadStorage.DeleteFile(job.IncomingPath);
        }

        private PartialMetadata ReadPartial(Job job, bool clearInvalid = true)
        {
            DownloadStorage.RejectLinks(job.PartialPath);
            DownloadStorage.RejectLinks(job.MetadataPath);
            try {
                if (File.Exists(job.PartialPath) && File.Exists(job.MetadataPath)) {
                    PartialMetadata metadata = JsonUtility.FromJson<PartialMetadata>(File.ReadAllText(job.MetadataPath));
                    var size = new FileInfo(job.PartialPath).Length;
                    if (metadata != null && metadata.Url == job.Url && metadata.Size == job.Info.Size &&
                        string.Equals(metadata.Sha256, job.Info.Sha256, StringComparison.OrdinalIgnoreCase) &&
                        size > 0 && size <= job.Info.Size && (size == job.Info.Size || IsValidator(metadata.Validator))) {
                        return metadata;
                    }
                }
            }
            catch (ArgumentException) { /* 元数据可能因进程退出只写了一部分，丢弃后从头下载。 */ }
            if (clearInvalid) {
                ClearPartial(job);
            }

            return null;
        }

        private static void ClearPartial(Job job, bool includeIncoming = true)
        {
            DownloadStorage.DeleteFile(job.PartialPath);
            DownloadStorage.DeleteFile(job.MetadataPath);
            if (includeIncoming) {
                DownloadStorage.DeleteFile(job.IncomingPath);
            }

            if (includeIncoming) {
                DownloadStorage.DeleteFile(job.IncomingPath + ".json");
            }
        }

        private static bool RangeMatches(string value, long offset, long total)
        {
            if (string.IsNullOrEmpty(value)) {
                return false;
            }

            Match match = Regex.Match(value, @"^bytes (\d+)-(\d+)/(\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            return match.Success && long.TryParse(match.Groups[1].Value, out var start) &&
                long.TryParse(match.Groups[2].Value, out var end) && long.TryParse(match.Groups[3].Value, out var length) &&
                start == offset && end == total - 1 && length == total && end >= start;
        }

        private static string GetValidator(DownloadTransportResponse response)
        {
            return IsStrongETag(response.ETag) ? response.ETag : IsHttpDate(response.LastModified) ? response.LastModified : null;
        }
        private static bool IsStrongETag(string value)
        {
            return value != null && value.Length >= 2 &&
            value[0] == '"' && value[value.Length - 1] == '"' && !value.Contains("\r") && !value.Contains("\n");
        }

        private static bool IsHttpDate(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && !value.Contains("\r") && !value.Contains("\n") &&
                    DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);
        }

        private static bool IsValidator(string value)
        {
            return IsStrongETag(value) || IsHttpDate(value);
        }

        private static bool ValidatorMatches(string validator, DownloadTransportResponse response)
        {
            return string.Equals(validator, IsStrongETag(validator) ? response.ETag : response.LastModified, StringComparison.Ordinal);
        }

        internal static void ValidateInfo(BundleInfo info)
        {
            if (info == null) {
                throw new ArgumentNullException(nameof(info));
            }

            if (!ResourceManifest.IsSafeBundleName(info.Name) || !DownloadStorage.IsSafeSegment(info.Name) ||
                info.Size < 0 || (info.Size == 0 && info.FileType != ResourceFileType.RawFile) ||
                !Enum.IsDefined(typeof(ResourceFileType), info.FileType) || !DownloadStorage.IsSha256(info.Sha256)) {
                throw new InvalidDataException($"远程下载需要安全文件名、有效大小（仅 RawFile 可为空）和 64 位 SHA-256：{info.Name}");
            }
        }

        internal static async ResourceOperationBase<bool> IsVerifiedAsync(string path, BundleInfo info, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path) || new FileInfo(path).Length != info.Size) {
                return false;
            }

            var actual = await BundleFileIntegrity.ComputeSha256Async(path, cancellationToken);
            return string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        private void Publish(Job job, string error = null, bool progressOnly = false)
        {
            var snapshot = new BundleDownloadProgress(job.Info.Name, job.State, job.Received, job.Info.Size, job.Attempt, error);
            m_snapshots[job.Destination] = snapshot;
            var now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            if (progressOnly && now - job.LastProgressTime < 0.1) {
                return;
            }

            job.LastProgressTime = now;
            foreach (Action<BundleDownloadProgress> listener in m_progressListeners) {
                try { listener(snapshot); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }

        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != m_threadId) {
                throw new InvalidOperationException("BundleDownloadQueue 必须在创建它的 Unity 主线程使用。");
            }
        }

    }

    /// <summary>
    /// 文件定位顺序：目标缓存、与清单相符的内置文件、其他版本相同内容的缓存、远端下载。
    /// SHA 对应的独立子目录隔离不同内容版本；此类不负责 V5 的清单激活与版本回滚。
    /// </summary>
    public sealed partial class BundleDownloadCache
    {
        private readonly string m_target;
        private readonly BundleDownloadOptions m_options;
        private readonly BuiltInResourceFileSystem m_builtIn;
        private readonly int m_threadId;
        private CacheContentIndex m_reuseIndex;
        private readonly OperationSemaphore m_indexGate = new(1, 1);
        private bool m_disposed;
        public BundleDownloadQueue Queue { get; }
        internal ResourceFileCapabilities FileCapabilities { get; }
        private ResourceOperationBase m_disposeOperation;

        public BundleDownloadCache(string builtInRoot, BundleDownloadOptions options, string buildTarget = "legacy",
            ResourceManifest builtInManifest = null, IDownloadTransport transport = null, IDownloadTransport unpackTransport = null)
            : this(builtInRoot, options, buildTarget, builtInManifest == null ? null : ResourceManifestPreparation.Copy(builtInManifest), transport, unpackTransport) { }

        internal BundleDownloadCache(string builtInRoot, BundleDownloadOptions options, string buildTarget,
            ResourceManifestPreparation prepared, IDownloadTransport transport = null, IDownloadTransport unpackTransport = null,
            bool offline = false)
        {
            if (builtInRoot == null) {
                throw new ArgumentNullException(nameof(builtInRoot));
            }

            m_options = options ?? throw new ArgumentNullException(nameof(options));
            if (!BundleDownloadOptions.IsSafeSegment(buildTarget)) {
                throw new ArgumentException("构建目标必须是安全目录名。", nameof(buildTarget));
            }

            m_target = buildTarget;
            m_offline = offline;
            m_threadId = Thread.CurrentThread.ManagedThreadId;
            if (builtInRoot.Length > 0) {
                m_builtIn = new BuiltInResourceFileSystem(builtInRoot,
                prepared?.Manifest.BuildTarget == buildTarget ? prepared : null,
                ResourcePath.Classify(builtInRoot) == ResourceLocationKind.JarUri ? new ResourceUnpackOptions(options.CacheRoot,
                    maxConcurrentUnpacks: options.MaxConcurrentDownloads, requestTimeoutSeconds: options.RequestTimeoutSeconds, diskPolicy: options.DiskPolicy, networkPolicy: options.NetworkPolicy) : null,
                unpackTransport, options.NetworkPolicy);
            }

            FileCapabilities = builtInRoot.Length > 0 && ResourcePath.Classify(builtInRoot) == ResourceLocationKind.JarUri ?
                ResourceFileCapabilities.None : ResourceFileLocation.DescribeCapabilities(ResourceLocationKind.LocalFile);
            Queue = new BundleDownloadQueue(options, transport);
            try {
                if (options.SourceOptions != null) {
                    m_additionalSource = options.SourceOptions.Factory(new ResourceSourceContext(buildTarget, options, offline)) ??
                        throw new InvalidOperationException("组合来源工厂返回了空实例。");
                    if (!(m_additionalSource is IResourceFileInspector)) { throw new ArgumentException("组合来源必须实现 IResourceFileInspector。"); }
                    FileCapabilities &= m_additionalSource.FileCapabilities;
                }
            }
            catch {
                _ = Queue.DisposeAsync();
                if (m_builtIn != null) { _ = m_builtIn.DisposeAsync(); }
                if (m_additionalSource != null) { _ = m_additionalSource.DisposeAsync(); }
                throw;
            }
        }

        public async ResourceOperationBase<string> ResolvePathAsync(BundleInfo info, DownloadPriority priority = DownloadPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            CheckThread();
            if (m_disposed) {
                throw new ObjectDisposedException(nameof(BundleDownloadCache));
            }

            cancellationToken.ThrowIfCancellationRequested();
            // 校验缓存/首包发生在 queue.jobs 之前；即使调用方同时关闭空队列，这个读取者仍持有租约。
            using var usage = CacheUsageLease.Acquire(m_options.CacheRoot);
            info = Snapshot(info);
            (ResourcePreparationBundle location, ResourceFileLocation verified) = await InspectCoreAsync(info, cancellationToken);
            if (location.Source == ResourcePreparationSource.TargetCache) {
                RecordCache(true, info);
                return location.SourcePath;
            }
            if (location.Source == ResourcePreparationSource.BuiltIn) {
                ResourceFileLocation installed = verified?.LocalPath != null ? verified :
                    m_builtIn == null ? null : await m_builtIn.TryResolveVerifiedAsync(info, cancellationToken);
                installed ??= await InspectAdditionalAsync(info, cancellationToken);
                // 延续旧版行为：真实定位会移除已损坏的目标文件；只读计划不会执行这一步。
                if (installed != null && DownloadStorage.IsSha256(info.Sha256)) {
                    var invalid = GetCachePath(info);
                    if (File.Exists(invalid)) {
                        using ResourceFileLock held = await CacheDestinationLock.AcquireAsync(m_options.CacheRoot, invalid, cancellationToken);
                        if (!await IsUsableFileAsync(invalid, info, cancellationToken)) {
                            DownloadStorage.DeleteFile(invalid);
                        }
                    }
                }
                if (installed != null) { RecordCache(true, info); return installed.LocalPath ?? installed.Location; }
            }
            var cache = GetCachePath(info);
            if (location.Source == ResourcePreparationSource.CrossVersionCache) {
                var copied = await TryCopyAsync(location.SourcePath, cache, info, cancellationToken);
                if (copied != null) { RecordCache(true, info); return copied; }
            }
            ResourceFileLocation delivered = await PrepareAdditionalAsync(info, priority, cancellationToken);
            if (delivered != null) { RecordCache(true, info); return delivered.LocalPath; }
            BundleDownloadQueue.ValidateInfo(info);
            var url = new Uri(new Uri(m_options.RemoteBaseUrl), Uri.EscapeDataString(info.Name)).AbsoluteUri;
            RecordCache(false, info);
            return await Queue.DownloadAsync(info, url, cache, priority, cancellationToken);
        }

        private void RecordCache(bool hit, BundleInfo info)
        {
            ResourceTelemetry.Record(new ResourceTelemetryEvent(hit ? ResourceTelemetryKind.CacheHit : ResourceTelemetryKind.CacheMiss,
                packageVersion: m_options.CacheVersion, address: info.Name, bytes: info.Size));
        }

        /// <summary>与真实定位共用检查逻辑，但不登记所有权、不修复文件，也不创建目标目录。</summary>
        internal async ResourceOperationBase<ResourcePreparationBundle> InspectAsync(BundleInfo info, CancellationToken cancellationToken)
        {
            CheckThread();
            if (m_disposed) {
                throw new ObjectDisposedException(nameof(BundleDownloadCache));
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var usage = CacheUsageLease.Acquire(m_options.CacheRoot);
            return (await InspectCoreAsync(Snapshot(info), cancellationToken)).Entry;
        }

        private static BundleInfo Snapshot(BundleInfo info)
        {
            return ResourceFileSystem.Snapshot(info);
        }

        private async ResourceOperationBase<(ResourcePreparationBundle Entry, ResourceFileLocation Verified)> InspectCoreAsync(BundleInfo info, CancellationToken cancellationToken)
        {
            var cache = DownloadStorage.IsSha256(info.Sha256) ? GetCachePath(info) : null;
            if (cache != null && await IsUsableFileAsync(cache, info, cancellationToken)) {
                return (new ResourcePreparationBundle(info.Name, info.Size, ResourcePreparationSource.TargetCache, cache), null);
            }

            // 首包读法由只读文件系统负责；空根明确禁用首包，HTTP 不被当成已安装文件。
            ResourceFileLocation installed = m_builtIn == null ? null : await m_builtIn.TryInspectVerifiedAsync(info, cancellationToken);
            installed ??= await InspectAdditionalAsync(info, cancellationToken);
            if (installed != null) {
                return (new ResourcePreparationBundle(info.Name, info.Size, ResourcePreparationSource.BuiltIn, installed.LocalPath ?? installed.Location), installed);
            }

            BundleDownloadQueue.ValidateInfo(info);
            // 初次扫描后，下载、解包和导入发布直接增量更新索引。
            if (m_reuseIndex == null) {
                await m_indexGate.WaitAsync(cancellationToken);
                try { m_reuseIndex ??= await CacheContentIndex.BuildAsync(m_options.CacheRoot, m_target, cancellationToken); }
                finally { m_indexGate.Release(); }
            }
            var source = await m_reuseIndex.FindVerifiedAsync(m_options.CacheVersion, cache, info, cancellationToken);
            return (new ResourcePreparationBundle(info.Name, info.Size,
                source == null ? ResourcePreparationSource.Download : ResourcePreparationSource.CrossVersionCache, source), null);
        }

        private static async ResourceOperationBase<bool> IsUsableFileAsync(string path, BundleInfo info, CancellationToken token)
        {
            try {
                DownloadStorage.RejectLinks(path);
                return await BundleDownloadQueue.IsVerifiedAsync(path, info, token);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException) { return false; }
        }

        private async ResourceOperationBase<string> TryCopyAsync(string source, string destination, BundleInfo info, CancellationToken token)
        {
            try { return await CopyVerifiedAsync(source, destination, info, token); }
            catch (Exception exception) when (!(exception is ResourceInsufficientSpaceException) &&
                !(exception is IOException io && ResourceDiskPolicy.IsDiskFull(io)) &&
                (exception is IOException || exception is InvalidDataException || exception is UnauthorizedAccessException)) { return null; /* 复用源消失或损坏时允许正常下载；显式导入则保留原始错误。 */ }
        }

        internal async ResourceOperationBase<ResourceFileLocation> ImportAsync(BundleInfo info, string source, CancellationToken token)
        {
            CheckThread();
            if (m_disposed) {
                throw new ObjectDisposedException(nameof(BundleDownloadCache));
            }

            token.ThrowIfCancellationRequested();
            using var usage = CacheUsageLease.Acquire(m_options.CacheRoot);
            info = Snapshot(info);
            var destination = GetCachePath(info);
            await CopyVerifiedAsync(source, destination, info, token, externalSource: true);
            await ResourcePersistence.FlushAsync();
            return new ResourceFileLocation(destination);
        }

        /// <summary>缓存复用和外部导入共用一次流式复制+哈希；同目标与下载共用锁。</summary>
        private async ResourceOperationBase<string> CopyVerifiedAsync(string source, string destination, BundleInfo info, CancellationToken token,
            bool externalSource = false)
        {
            string temporary = null;
            try {
                using CacheUsageLease sourceLease = externalSource ? null : HotUpdate.HostCacheLifecycle.AcquireSourceLease(m_options.CacheRoot, source);
                using ResourceFileLock held = await CacheDestinationLock.AcquireAsync(m_options.CacheRoot, destination, token);
                // 另一缓存实例或进程可能已在等待期间完成该目标，不能覆盖它。
                if (await IsUsableFileAsync(destination, info, token)) {
                    return destination;
                }

                DownloadStorage.RejectLinks(source);
                if (DownloadStorage.PathComparer.Equals(source, destination)) {
                    throw new InvalidDataException("不能从损坏的目标缓存导入自身。");
                }

                if (!File.Exists(source)) {
                    throw new FileNotFoundException("导入源文件不存在。", source);
                }

                if (new FileInfo(source).Length != info.Size) {
                    throw new InvalidDataException("导入源长度与清单不一致：" + info.Name);
                }

                token.ThrowIfCancellationRequested();
                DownloadStorage.ValidatePath(m_options.CacheRoot, destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                CacheOwnership.Register(m_options.CacheRoot, destination, info);
                temporary = DownloadStorage.ValidatePath(m_options.CacheRoot, destination + ".reuse." + Guid.NewGuid().ToString("N") + ".tmp");
                using IDisposable reservation = m_options.DiskPolicy.Reserve(m_options.CacheRoot, info.Size, (temporary, 0));
                var copiedHash = await ResourceFileIO.Shared.CopyAndHashAsync(source, temporary, token, info.Size);
                if (new FileInfo(temporary).Length != info.Size || !string.Equals(copiedHash, info.Sha256, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("导入文件大小或 SHA-256 与清单不一致：" + info.Name);
                }

                token.ThrowIfCancellationRequested();
                DownloadStorage.ValidatePath(m_options.CacheRoot, destination);
                // 只发布经过重新哈希的完整副本；源文件从不移动、写入或建立硬链接。
                if (File.Exists(destination)) {
                    DownloadStorage.DeleteFile(destination);
                }

                File.Move(temporary, destination);
                CacheContentIndex.Published(m_options.CacheRoot, destination, info);
                return destination;
            }
            finally {
                if (temporary != null) {
                    try { DownloadStorage.DeleteFile(temporary); }
                    catch (IOException) { /* 无法删除的隔离临时文件不作为正式缓存使用。 */ }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        public string GetCachePath(BundleInfo info)
        {
            CheckThread();
            BundleDownloadQueue.ValidateInfo(info);
            return DownloadStorage.ValidatePath(m_options.CacheRoot,
                Path.Combine(m_options.CacheRoot, m_options.CacheVersion, m_target, info.Sha256.ToLowerInvariant(), info.Name));
        }

        internal ResourceFileLocation ResolveLocal(BundleInfo info)
        {
            CheckThread();
            if (m_disposed) {
                throw new ObjectDisposedException(nameof(BundleDownloadCache));
            }

            using var usage = CacheUsageLease.Acquire(m_options.CacheRoot);
            info = Snapshot(info);
            if (DownloadStorage.IsSha256(info.Sha256)) {
                var file = new ResourceFileLocation(GetCachePath(info));
                try {
                    DownloadStorage.RejectLinks(file.LocalPath);
                    BuiltInResourceFileSystem.ValidateLocalFile(file, info);
                    return file;
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException) { }
            }
            ResourceFileLocation installed = m_builtIn?.TryResolveLocal(info);
            if (installed == null && HasAdditionalSource(info) && m_additionalSource is ISynchronousResourceFileSystem synchronous) {
                try {
                    installed = synchronous.Resolve(info);
                    if (installed?.LocalPath == null) { throw new InvalidOperationException("组合来源必须返回本地文件。"); }
                    BuiltInResourceFileSystem.ValidateLocalFile(installed, info);
                }
                catch (FileNotFoundException) { installed = null; }
                catch (InvalidDataException) { installed = null; }
            }
            return installed ?? throw new FileNotFoundException("没有可同步读取的完整文件，请先调用 PrepareDependenciesAsync：" + info.Name);
        }

        public ResourceOperationBase DisposeAsync()
        {
            CheckThread();
            if (m_disposeOperation != null) {
                return m_disposeOperation;
            }

            m_disposed = true;
            return m_disposeOperation = DisposeCoreAsync();
        }

        private async ResourceOperationBase DisposeCoreAsync()
        {
            Exception failure = null;
            try { await Queue.DisposeAsync(); }
            catch (Exception error) { failure = error; }
            try { if (m_builtIn != null) { await m_builtIn.DisposeAsync(); } }
            catch (Exception error) { failure = failure == null ? error : ResourceFailure.WithCleanup(failure, error); }
            try { if (m_additionalSource != null) { await m_additionalSource.DisposeAsync(); } }
            catch (Exception error) { failure = failure == null ? error : ResourceFailure.WithCleanup(failure, error); }
            if (failure != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        }

        public event Action<BundleDownloadProgress> ProgressChanged
        {
            add
            {
                Queue.ProgressChanged += value;
            }

            remove
            {
                Queue.ProgressChanged -= value;
            }
        }

        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != m_threadId) {
                throw new InvalidOperationException("BundleDownloadCache 必须在创建它的 Unity 主线程使用。");
            }
        }
    }
}

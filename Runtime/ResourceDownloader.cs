using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ZRAsset
{
    /// <summary>下载和导入共用的有界文件任务组；不为尚未派发的文件创建操作或取消注册。</summary>
    public abstract class ResourceFileBatchOperation: ResourceOperationBase, IDisposable
    {
        private readonly object m_owner;
        private readonly Func<BundleInfo, CancellationToken, ResourceOperationBase<ResourceFileLocation>> m_prepare;
        private readonly Dictionary<string, BundleInfo> m_files;
        private readonly List<(BundleInfo info, ResourceOperationBase<ResourceFileLocation> operation)> m_active = new();
        private readonly CancellationTokenSource m_cancellation = new();
        private readonly int m_concurrency;
        private readonly BundleDownloadQueue m_downloadQueue;
        private readonly Dictionary<string, FileProgress> m_fileProgress = new(StringComparer.Ordinal);
        private readonly Queue<FileProgress> m_changedFiles = new();
        private long m_currentBytes, m_downloadedBytes, m_reusedBytes;
        private int m_verifyingCount;
        private sealed class FileProgress
        {
            internal BundleInfo Info;
            internal bool Dispatched, Finished, UsedDownload, Dirty;
            internal long Received;
            internal int Attempt;
            internal ResourceFilePreparationState State;
            internal Exception Error;
            internal long AccountedCurrent, AccountedDownload, AccountedReuse;
            internal int AccountedVerifying;
        }
        // 订阅变化时复制观察者列表，逐帧派发不再为 GetInvocationList 分配数组。
        private sealed class Observers<T>
        {
            private Action<T> m_handlers;
            private Delegate[] m_snapshot = Array.Empty<Delegate>();
            internal void Add(Action<T> handler) { m_handlers += handler; m_snapshot = m_handlers?.GetInvocationList() ?? Array.Empty<Delegate>(); }
            internal void Remove(Action<T> handler) { m_handlers -= handler; m_snapshot = m_handlers?.GetInvocationList() ?? Array.Empty<Delegate>(); }
            internal void Notify(T value)
            {
                Delegate[] listeners = m_snapshot;
                foreach (Action<T> listener in listeners) {
                    try { listener(value); } catch (Exception error) { UnityEngine.Debug.LogException(error); }
                }
            }
        }
        private readonly Observers<string> m_startedObservers = new();
        private readonly Observers<ResourceFileBatchProgress> m_progressObservers = new();
        private readonly Observers<ResourceFilePreparationEvent> m_fileObservers = new(), m_failureObservers = new();
        private Queue<BundleInfo> m_pending;
        private Exception m_failure;
        private bool m_started, m_disposed;
        private bool m_progressDirty = true, m_subscribed;
        public bool IsPaused { get; private set; }
        public int TotalCount
        {
            get
            {
                return m_files.Count;
            }
        }

        public int CompletedCount { get; private set; }
        public long TotalBytes { get; private set; }
        public long CompletedBytes { get; private set; }
        public ResourceFileBatchProgress ByteProgress { get; private set; }
        public event Action<string> FileStarted
        {
            add
            {
                m_startedObservers.Add(value);
            }

            remove
            {
                m_startedObservers.Remove(value);
            }
        }
        public event Action<ResourceFileBatchProgress> ProgressChanged
        {
            add
            {
                m_progressObservers.Add(value);
            }

            remove
            {
                m_progressObservers.Remove(value);
            }
        }
        public event Action<ResourceFilePreparationEvent> FileProgressChanged
        {
            add
            {
                m_fileObservers.Add(value);
            }

            remove
            {
                m_fileObservers.Remove(value);
            }
        }
        public event Action<ResourceFilePreparationEvent> FileFailed
        {
            add
            {
                m_failureObservers.Add(value);
            }

            remove
            {
                m_failureObservers.Remove(value);
            }
        }
        internal ResourceFileBatchOperation(object owner, IEnumerable<BundleInfo> files,
            Func<BundleInfo, CancellationToken, ResourceOperationBase<ResourceFileLocation>> prepare, int concurrency,
            BundleDownloadQueue downloadQueue = null)
        {
            if (concurrency < 1 || concurrency > 32) {
                throw new ArgumentOutOfRangeException(nameof(concurrency));
            }

            m_owner = owner; m_prepare = prepare; m_concurrency = concurrency;
            m_downloadQueue = downloadQueue;
            m_files = files.ToDictionary(f => f.Name, ResourceFileSystem.Snapshot, StringComparer.Ordinal);
            foreach (BundleInfo file in m_files.Values) {
                TotalBytes = checked(TotalBytes + file.Size);
            }

            RefreshTotals();
        }
        protected void CombineFiles(ResourceFileBatchOperation other)
        {
            if (m_started || m_disposed || other == null || other.m_started || other.m_disposed || !ReferenceEquals(m_owner, other.m_owner)) {
                throw new InvalidOperationException("仅可合并同一管理器尚未开始的下载器。");
            }

            var total = TotalBytes;
            foreach (BundleInfo file in other.m_files.Values) {
                if (!m_files.ContainsKey(file.Name)) {
                    total = checked(total + file.Size);
                }
            }

            foreach (BundleInfo file in other.m_files.Values) {
                m_files[file.Name] = file;
            }

            TotalBytes = total;
            RefreshTotals();
        }
        protected void StartFiles()
        {
            if (m_disposed) {
                throw new ObjectDisposedException(GetType().Name);
            }

            if (!m_started) {
                m_started = true;
                m_pending = new Queue<BundleInfo>(m_files.Values.OrderBy(f => f.Name, StringComparer.Ordinal));
                foreach (BundleInfo file in m_files.Values) {
                    m_fileProgress.Add(file.Name, new FileProgress { Info = file });
                }

                if (m_downloadQueue != null) { m_downloadQueue.ProgressChanged += OnDownloadProgress; m_subscribed = true; }
                OperationSystem.Start(this);
            }
        }
        public void Pause()
        {
            IsPaused = true;
        }

        public void Resume()
        {
            IsPaused = false;
        }

        public void Cancel()
        {
            if (IsDone || m_cancellation.IsCancellationRequested) {
                return;
            }
            // 用户或平台回调可能抛异常；取消后仍须等待已派发操作退出并释放租约。
            try { m_cancellation.Cancel(); } catch (Exception error) { m_failure ??= error; }
        }
        public void Dispose()
        {
            if (m_disposed) {
                return;
            }

            m_disposed = true; Cancel();
            if (!m_started) { m_cancellation.Dispose(); Fail(new OperationCanceledException()); }
        }
        protected override void OnUpdate()
        {
            for (var i = m_active.Count - 1; i >= 0; i--) {
                (BundleInfo info, ResourceOperationBase<ResourceFileLocation> operation) = m_active[i];
                if (m_downloadQueue != null && m_downloadQueue.TryGetActiveProgress(info, out BundleDownloadProgress current)) {
                    OnDownloadProgress(current);
                }

                if (!operation.IsDone) {
                    continue;
                }

                m_active.RemoveAt(i);
                FileProgress progress = m_fileProgress[info.Name];
                progress.Finished = true;
                if (operation.Status == OperationStatus.Succeeded) {
                    CompletedCount++; CompletedBytes += info.Size;
                    progress.State = ResourceFilePreparationState.Ready;
                    if (progress.UsedDownload) {
                        progress.Received = info.Size;
                    }
                }
                else {
                    progress.Error = operation.Error;
                    progress.State = operation.IsCanceled ? ResourceFilePreparationState.Canceled : ResourceFilePreparationState.Failed;
                    m_failure ??= operation.Error; Cancel();
                }
                MarkChanged(progress);
                if (progress.State == ResourceFilePreparationState.Failed) {
                    m_failureObservers.Notify(Snapshot(progress));
                }
            }
            if (m_cancellation.IsCancellationRequested) {
                PublishProgress();
                if (m_active.Count != 0) {
                    return;
                }

                FinishBatch(m_failure ?? new OperationCanceledException()); return;
            }
            while (!IsPaused && !m_cancellation.IsCancellationRequested && m_active.Count < m_concurrency && m_pending.Count > 0) {
                BundleInfo file = m_pending.Dequeue();
                FileProgress progress = m_fileProgress[file.Name];
                progress.Dispatched = true; progress.State = ResourceFilePreparationState.Preparing;
                MarkChanged(progress);
                try {
                    ResourceOperationBase<ResourceFileLocation> operation = m_prepare(file, m_cancellation.Token);
                    operation.ParentDiagnosticId = DiagnosticId; operation.DebugName = file.Name;
                    m_active.Add((file, operation));
                    if (!operation.IsDone && m_downloadQueue != null && m_downloadQueue.TryGetActiveProgress(file, out BundleDownloadProgress current)) {
                        OnDownloadProgress(current);
                    }

                    m_startedObservers.Notify(file.Name);
                }
                catch (Exception error) {
                    progress.Finished = true; progress.Error = error;
                    progress.State = error is OperationCanceledException ? ResourceFilePreparationState.Canceled : ResourceFilePreparationState.Failed;
                    MarkChanged(progress);
                    m_failure = error; Cancel();
                    if (progress.State == ResourceFilePreparationState.Failed) {
                        m_failureObservers.Notify(Snapshot(progress));
                    }

                    break;
                }
            }
            PublishProgress();
            if (m_pending.Count == 0 && m_active.Count == 0 && !m_cancellation.IsCancellationRequested) { FinishBatch(null); }
        }

        private void OnDownloadProgress(BundleDownloadProgress value)
        {
            if (!m_fileProgress.TryGetValue(value.BundleName, out FileProgress progress) || !progress.Dispatched || progress.Finished) {
                return;
            }

            var received = Math.Max(0, Math.Min(progress.Info.Size, value.ReceivedBytes));
            ResourceFilePreparationState state = value.State == BundleDownloadState.Downloading ? ResourceFilePreparationState.Downloading :
                value.State == BundleDownloadState.Verifying || value.State == BundleDownloadState.Succeeded ? ResourceFilePreparationState.Verifying :
                ResourceFilePreparationState.Preparing;
            if (progress.Received == received && progress.Attempt == value.Attempt && progress.State == state) {
                return;
            }

            progress.UsedDownload |= value.Attempt > 0;
            progress.Received = received; progress.Attempt = value.Attempt; progress.State = state;
            MarkChanged(progress);
        }

        private void MarkChanged(FileProgress file)
        {
            var current = file.State == ResourceFilePreparationState.Ready ? file.Info.Size : file.Received;
            var downloaded = file.UsedDownload ? file.Received : 0;
            var reused = !file.UsedDownload && m_downloadQueue != null && file.State == ResourceFilePreparationState.Ready ? file.Info.Size : 0;
            var verifying = file.State == ResourceFilePreparationState.Verifying ? 1 : 0;
            m_currentBytes += current - file.AccountedCurrent;
            m_downloadedBytes += downloaded - file.AccountedDownload;
            m_reusedBytes += reused - file.AccountedReuse;
            m_verifyingCount += verifying - file.AccountedVerifying;
            file.AccountedCurrent = current; file.AccountedDownload = downloaded;
            file.AccountedReuse = reused; file.AccountedVerifying = verifying;
            if (!file.Dirty) { file.Dirty = true; m_changedFiles.Enqueue(file); }
            m_progressDirty = true;
        }

        private void RefreshTotals()
        {
            ByteProgress = new ResourceFileBatchProgress(TotalCount, 0, 0, 0, TotalBytes, 0, 0, 0, 0, m_downloadQueue != null);
        }

        private void PublishProgress()
        {
            if (!m_progressDirty) {
                return;
            }

            m_progressDirty = false;
            ByteProgress = new ResourceFileBatchProgress(TotalCount, CompletedCount, m_active.Count, m_verifyingCount,
                TotalBytes, CompletedBytes, m_currentBytes, m_downloadedBytes, m_reusedBytes, m_downloadQueue != null);
            Progress = ByteProgress.Progress;
            var count = m_changedFiles.Count;
            while (count-- > 0) {
                FileProgress progress = m_changedFiles.Dequeue();
                progress.Dirty = false;
                m_fileObservers.Notify(Snapshot(progress));
            }
            m_progressObservers.Notify(ByteProgress);
        }

        private static ResourceFilePreparationEvent Snapshot(FileProgress progress)
        {
            return new(progress.Info.Name,
            progress.Info.Size, progress.Received, progress.Attempt, progress.State, progress.Error);
        }

        private void FinishBatch(Exception error)
        {
            if (m_subscribed) { m_downloadQueue.ProgressChanged -= OnDownloadProgress; m_subscribed = false; }
            if (error != null) {
                foreach (FileProgress progress in m_fileProgress.Values) {
                    if (progress.Finished) {
                        continue;
                    }

                    progress.State = ResourceFilePreparationState.Canceled; MarkChanged(progress);
                }
                m_progressDirty = true;
            }
            m_cancellation.Dispose();
            if (error == null) {
                Succeed();
            }
            else {
                Fail(error);
            }

            PublishProgress();
        }

    }

    /// <summary>可合并的准备任务组。暂停仅停止派发新文件，不取消其它消费者共享的传输。</summary>
    public sealed class ResourceDownloader: ResourceFileBatchOperation
    {
        internal ResourceDownloader(object owner, IEnumerable<BundleInfo> files,
            Func<BundleInfo, CancellationToken, ResourceOperationBase<ResourceFileLocation>> prepare, int concurrency,
            BundleDownloadQueue downloadQueue = null)
            : base(owner, files, prepare, concurrency, downloadQueue) { }
        public void Combine(ResourceDownloader other)
        {
            CombineFiles(other);
        }

        public ResourceDownloader StartDownload() { StartFiles(); return this; }
    }

    /// <summary>文件位置及独立读取租约。业务不再使用文件时必须 Dispose。</summary>
    public sealed class ResourceBundleFile: IDisposable
    {
        private IDisposable m_lease;
        public string BundleName { get; }
        public ResourceFileLocation File { get; }
        public string LocalPath
        {
            get
            {
                return File.LocalPath;
            }
        }

        public ResourceFileType FileType { get; }
        public bool IsEncrypted { get; }
        internal ResourceBundleFile(BundleInfo info, ResourceFileLocation file, IDisposable lease)
        { BundleName = info.Name; File = file; FileType = info.FileType; IsEncrypted = info.IsEncrypted; m_lease = lease; }
        public void Dispose()
        {
            Interlocked.Exchange(ref m_lease, null)?.Dispose();
        }
    }

    public sealed partial class ResourceManager
    {
        public ResourceDownloader CreateDownloader(ResourceSelection selection, int maxConcurrentFiles = 3,
            DownloadPriority priority = DownloadPriority.Normal)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            ResourceSelectionResult selected = Select(selection);
            return FileSystem == null
                ? throw new NotSupportedException("当前后端没有文件系统。")
                : new ResourceDownloader(this, selected.BundleNames.Select(n => m_catalog[n]),
                (info, token) =>
                {
                    CheckThread(); return m_closing
                        ? throw new ObjectDisposedException(nameof(ResourceManager))
                        : TrackContentOperation(() => FileSystem.ResolveAsync(info, priority, token));
                }, maxConcurrentFiles, DownloadQueue);
        }
        public ResourceOperationBase<ResourceBundleFile> EnsureBundleFileAsync(string address, DownloadPriority priority = DownloadPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            ResourceAssetInfo asset = GetAssetInfo(address);
            return FileSystem == null
                ? throw new NotSupportedException("当前后端没有文件系统。")
                : TrackContentOperation<ResourceBundleFile>(async () =>
            {
                IDisposable lease = FileSystem.AcquireReadLease();
                try {
                    BundleInfo info = m_catalog[asset.BundleName]; return new ResourceBundleFile(info,
                    await FileSystem.ResolveAsync(info, priority, cancellationToken), lease);
                }
                catch { lease?.Dispose(); throw; }
            });
        }
    }
}

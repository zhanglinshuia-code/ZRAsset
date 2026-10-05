using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ZRAsset
{
    /// <summary>成功必须表示同一 URL 的后续 Bundle 请求可命中宿主缓存。取消后必须使请求进入终态。</summary>
    public interface IMiniGamePreloadStrategy
    {
        bool IsCached(string url, BundleInfo info);
        ResourceOperationBase PreloadAsync(string url, BundleInfo info, CancellationToken cancellationToken);
    }

    /// <summary>需要让文件准备与 Bundle 打开互斥的文件系统可实现此接口。</summary>
    public interface IResourceBundleSource
    {
        ResourceOperationBase<object> LoadBundleAsync(BundleInfo info, IUnityResourceLoader loader);
    }

    /// <summary>宿主 URL 缓存：标准下载器、缓存查询、共享预下载、加载优先以及同包互斥。</summary>
    public sealed class MiniGameWebFileSystem: ResourceFileSystem, IResourceFileInspector, IResourceBundleSource
    {
        private readonly Uri m_root;
        private readonly IMiniGamePreloadStrategy m_strategy;
        private readonly int m_concurrency;
        private readonly int m_maxRequestsPerFrame;
        private readonly IResourceDownloadUrlPolicy m_urlPolicy;
        private readonly bool m_explicitRetryPolicy;
        private ResourceRetryPolicy m_retryPolicy;
        private readonly IResourceDownloadRetryPolicy m_downloadRetryPolicy;
        private readonly Dictionary<string, string> m_resolvedUrls = new(StringComparer.Ordinal);
        private long m_dispatchTick = -1;
        private int m_dispatched;
        private readonly Dictionary<string, Job> m_preloads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Job> m_loads = new(StringComparer.Ordinal);
        private readonly Queue<Job> m_waitingLoads = new();
        private readonly Queue<Job> m_waitingPreloads = new();
        private readonly List<Job> m_active = new();
        private readonly HashSet<string> m_busyBundles = new(StringComparer.Ordinal);
        private PumpOperation m_pump;

        private sealed class Job
        {
            internal BundleInfo Info;
            internal string Url;
            internal IUnityResourceLoader Loader;
            internal int Waiters;
            internal int Attempts;
            internal double ReadyAt;
            internal bool Delivered;
            internal object Undelivered;
            internal readonly OperationCompletionSource<object> Completion = new();
            internal readonly CancellationTokenSource Cancellation = new();
            internal ResourceOperationBase Request;
            internal ResourceOperationBase<object> LoadRequest;
        }

        private sealed class PumpOperation: ResourceOperationBase
        {
            private readonly MiniGameWebFileSystem m_owner;
            private readonly long m_firstTick;

            internal PumpOperation(MiniGameWebFileSystem owner)
            {
                m_owner = owner;
                m_firstTick = OperationSystem.TickIndex + 1;
            }

            protected override void OnUpdate()
            {
                if (OperationSystem.TickIndex < m_firstTick) { return; }
                m_owner.Pump();
                if (m_owner.m_active.Count == 0 && m_owner.m_waitingLoads.Count == 0 && m_owner.m_waitingPreloads.Count == 0) { Succeed(); }
            }
        }

        public MiniGameWebFileSystem(string versionedBaseUrl, IMiniGamePreloadStrategy strategy, int maxConcurrentRequests = 4,
            ResourceRetryPolicy retryPolicy = null, IResourceDownloadUrlPolicy urlPolicy = null, int maxRequestsPerFrame = 4,
            IResourceDownloadRetryPolicy downloadRetryPolicy = null)
        {
            if (!Uri.TryCreate(versionedBaseUrl, UriKind.Absolute, out Uri root) || root.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment) || !string.IsNullOrEmpty(root.UserInfo)) {
                throw new ArgumentException("小游戏预下载要求无查询参数的 HTTPS 版本目录。", nameof(versionedBaseUrl));
            }
            if (maxConcurrentRequests < 1 || maxConcurrentRequests > 32) { throw new ArgumentOutOfRangeException(nameof(maxConcurrentRequests)); }
            if (maxRequestsPerFrame < 1) { throw new ArgumentOutOfRangeException(nameof(maxRequestsPerFrame)); }
            m_root = new Uri(root.AbsoluteUri.TrimEnd('/') + "/");
            m_strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            m_concurrency = maxConcurrentRequests;
            m_maxRequestsPerFrame = maxRequestsPerFrame;
            m_urlPolicy = urlPolicy;
            m_explicitRetryPolicy = retryPolicy != null;
            m_retryPolicy = retryPolicy ?? new ResourceRetryPolicy();
            m_downloadRetryPolicy = downloadRetryPolicy;
        }

        public override bool OwnsRetries
        {
            get
            {
                return true;
            }
        }

        internal void ConfigureRetryPolicy(ResourceRetryPolicy policy)
        {
            if (!m_explicitRetryPolicy) { m_retryPolicy = policy; }
        }

        public override ResourceFileCapabilities FileCapabilities
        {
            get { return ResourceFileCapabilities.None; }
        }
        public override bool SupportsDownloads
        {
            get { return true; }
        }

        public ResourceOperationBase<ResourcePreparationBundle> InspectAsync(BundleInfo info, CancellationToken cancellationToken = default)
        {
            CheckAvailable();
            cancellationToken.ThrowIfCancellationRequested();
            BundleInfo snapshot = Snapshot(info);
            var cached = m_strategy.IsCached(GetUrl(snapshot), snapshot);
            return ResourceOperationBase.FromResult(new ResourcePreparationBundle(snapshot.Name, snapshot.Size,
                cached ? ResourcePreparationSource.TargetCache : ResourcePreparationSource.Download, GetUrl(snapshot)));
        }

        protected override async ResourceOperationBase<ResourceFileLocation> ResolveCoreAsync(BundleInfo info, DownloadPriority priority, CancellationToken token)
        {
            var url = GetUrl(info);
            if (m_strategy.IsCached(url, info)) { return new ResourceFileLocation(url); }
            await JoinAsync(info, null, token);
            return new ResourceFileLocation(GetUrl(info));
        }

        public ResourceOperationBase<object> LoadBundleAsync(BundleInfo info, IUnityResourceLoader loader)
        {
            CheckAvailable();
            if (loader == null) { throw new ArgumentNullException(nameof(loader)); }
            BundleInfo snapshot = Snapshot(info);
            return snapshot.FileType != ResourceFileType.AssetBundle || snapshot.IsEncrypted
                ? throw new NotSupportedException("宿主原生 URL 缓存只支持未加密的 AssetBundle；加密包使用 MiniGamePersistence 文件缓存。")
                : RunTrackedAsync(token => JoinAsync(snapshot, loader, token), default, cancelAfterCompletion: false);
        }

        private async ResourceOperationBase<object> JoinAsync(BundleInfo info, IUnityResourceLoader loader, CancellationToken token)
        {
            Dictionary<string, Job> jobs = loader == null ? m_preloads : m_loads;
            // A canceled writer still owns its slot until its SDK request ends.
            // A new consumer must not inherit the old writer's canceled token.
            while (jobs.TryGetValue(info.Name, out Job retiring) && retiring.Cancellation.IsCancellationRequested) {
                try { await ResourceOperations.WaitAsync(retiring.Completion.Operation, token); }
                catch (Exception) when (!token.IsCancellationRequested) { }
                token.ThrowIfCancellationRequested();
            }
            return await WaitAsync(GetJob(info, GetUrl(info), loader), token);
        }

        private string GetUrl(BundleInfo info)
        {
            return m_resolvedUrls.TryGetValue(info.Name, out var resolved) ? resolved : SelectUrl(info, 1);
        }

        private string SelectUrl(BundleInfo info, int attempt)
        {
            var original = new Uri(m_root, Uri.EscapeDataString(info.Name)).AbsoluteUri;
            var selected = m_urlPolicy?.GetUrl(original, attempt) ?? original;
            return !Uri.TryCreate(selected, UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
                ? throw new InvalidOperationException("小游戏 URL 策略必须返回有效的 HTTPS 地址。")
                : uri.AbsoluteUri;
        }

        private Job GetJob(BundleInfo info, string url, IUnityResourceLoader loader)
        {
            Dictionary<string, Job> jobs = loader == null ? m_preloads : m_loads;
            if (jobs.TryGetValue(info.Name, out Job existing)) {
                return existing.Info.Sha256 != info.Sha256 || existing.Info.Hash != info.Hash || existing.Info.Size != info.Size
                    ? throw new InvalidDataException("同一版本目录不能请求不同内容的同名资源包。")
                    : existing;
            }
            var job = new Job { Info = info, Url = url, Loader = loader };
            jobs.Add(info.Name, job);
            (loader == null ? m_waitingPreloads : m_waitingLoads).Enqueue(job);
            if (m_pump == null || m_pump.IsDone) { m_pump = OperationSystem.Start(new PumpOperation(this)); }
            return job;
        }

        private async ResourceOperationBase<object> WaitAsync(Job job, CancellationToken token)
        {
            job.Waiters++;
            try {
                await ResourceOperations.WaitAsync(job.Completion.Operation, token);
                token.ThrowIfCancellationRequested();
                if (job.Loader != null) { job.Delivered = true; job.Undelivered = null; }
                return job.Completion.Operation.Result;
            }
            finally {
                if (--job.Waiters == 0) {
                    if (!job.Completion.Operation.IsDone) { job.Cancellation.Cancel(); }
                    ReleaseUndelivered(job);
                }
            }
        }

        private void Pump()
        {
            for (var i = m_active.Count - 1; i >= 0; i--) {
                Job job = m_active[i];
                if (!job.Request.IsDone) { continue; }
                m_active.RemoveAt(i);
                m_busyBundles.Remove(job.Info.Name);
                Exception error = job.Request.Error;
                var result = error == null ? job.LoadRequest?.Result : null;
                if (error == null && job.Loader != null && result == null) { error = new IOException("加载器返回了空 Bundle。"); }
                if (TryRetry(job, error)) { continue; }
                if (error == null) { m_resolvedUrls[job.Info.Name] = job.Url; }
                Complete(job, result, error);
                // 同一包已完成实际加载，尚未发出的预下载无需再发出重复请求。
                if (error == null && job.Loader != null && m_preloads.TryGetValue(job.Info.Name, out Job preload) && preload.Request == null) {
                    preload.Url = job.Url;
                    Complete(preload, null, null);
                }
            }
            Dispatch(m_waitingLoads);
            Dispatch(m_waitingPreloads);
        }

        private void Dispatch(Queue<Job> pending)
        {
            if (m_dispatchTick != OperationSystem.TickIndex) { m_dispatchTick = OperationSystem.TickIndex; m_dispatched = 0; }
            var count = pending.Count;
            while (count-- > 0 && m_active.Count < m_concurrency && pending.Count > 0 && m_dispatched < m_maxRequestsPerFrame) {
                Job job = pending.Dequeue();
                if (job.Completion.Operation.IsDone) { continue; }
                if (job.Cancellation.IsCancellationRequested) { Complete(job, null, new OperationCanceledException()); continue; }
                if (job.ReadyAt > UnityEngine.Time.realtimeSinceStartupAsDouble || m_busyBundles.Contains(job.Info.Name) || (job.Loader == null && m_loads.ContainsKey(job.Info.Name))) {
                    pending.Enqueue(job);
                    continue;
                }
                try {
                    job.Attempts++;
                    m_dispatched++;
                    job.Url = job.Attempts == 1 ? GetUrl(job.Info) : SelectUrl(job.Info, job.Attempts);
                    if (job.Loader == null) {
                        job.Request = m_strategy.PreloadAsync(job.Url, job.Info, job.Cancellation.Token);
                    }
                    else {
                        job.LoadRequest = job.Loader.LoadBundleAsync(new ResourceFileLocation(job.Url), job.Info);
                        job.Request = job.LoadRequest;
                    }
                    if (job.Request == null) { throw new InvalidOperationException("宿主返回了空请求。"); }
                    m_busyBundles.Add(job.Info.Name);
                    m_active.Add(job);
                }
                catch (Exception error) { if (!TryRetry(job, error)) { Complete(job, null, error); } }
            }
        }

        private bool TryRetry(Job job, Exception error)
        {
            if (error == null || job.Cancellation.IsCancellationRequested || job.Waiters == 0) {
                return false;
            }
            TimeSpan delay;
            try {
                if (!ResourceDownloadRetry.TryGetDelay(job.Info.Name, job.Url, job.Attempts, error,
                    m_retryPolicy, m_downloadRetryPolicy, out delay)) { return false; }
            }
            catch (Exception policyError) {
                // A bad user policy must finish this job, not abandon every waiter in the pump.
                Complete(job, null, policyError);
                return true;
            }
            job.Request = null;
            job.LoadRequest = null;
            job.ReadyAt = UnityEngine.Time.realtimeSinceStartupAsDouble + delay.TotalSeconds;
            (job.Loader == null ? m_waitingPreloads : m_waitingLoads).Enqueue(job);
            return true;
        }

        private static void ReleaseUndelivered(Job job)
        {
            if (job.Delivered || job.Waiters != 0 || job.Undelivered == null) { return; }
            var bundle = job.Undelivered;
            job.Undelivered = null;
            job.Loader.UnloadBundle(bundle);
        }

        private void Complete(Job job, object value, Exception error)
        {
            (job.Loader == null ? m_preloads : m_loads).Remove(job.Info.Name);
            job.Cancellation.Dispose();
            job.Undelivered = value;
            ReleaseUndelivered(job);
            if (error == null) { job.Completion.TrySetResult(value); }
            else { job.Completion.TrySetException(error); }
        }

        protected override async ResourceOperationBase ShutdownAsync()
        {
            foreach (Job job in m_preloads.Values) { job.Cancellation.Cancel(); }
            foreach (Job job in m_loads.Values) { job.Cancellation.Cancel(); }
            if (m_pump != null) { await m_pump; }
        }
    }
}

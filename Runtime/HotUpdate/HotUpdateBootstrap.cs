using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace ZRAsset.HotUpdate
{
    public enum HotUpdateFailureKind { None, HostUpgradeRequired, Cancelled, RestartRequired, Failed }

    public enum HotUpdateBootstrapMode { SignedUpdate, OfflineActive }
    public enum HotUpdateBootstrapState
    { Idle, CheckingEnvironment, CheckingRelease, Planning, Preparing, Activating, OpeningResources, StartingCode, Started, Failed, RequiresRestart, Closed, AwaitingDownloadConsent, AwaitingReady }

    /// <summary>一次启动的不可变配置；只有显式允许时，发布检查和准备失败才选择已激活的离线资源。</summary>
    public sealed class HotUpdateBootstrapOptions
    {
        public string PlayerBuildId { get; }
        public string PackageName { get; }
        public IResourceDecryptionServices DecryptionServices { get; }
        public IResourceKeyProvider ManifestKeys { get; }
        public IResourceManifestCodec ManifestCodec { get; }
        public bool EncodedBuiltInManifest { get; }
        internal string HostCacheRoot { get; }
        public BundleDownloadOptions Downloads { get; }
        public ResourceReleaseTrustOptions Trust { get; }
        public string ReleaseUrl { get; }
        public string BuiltInRoot { get; }
        public string ManifestAddress { get; }
        public HotUpdateBootstrapMode Mode { get; }
        public bool AllowOfflineFallback { get; }
        public double UnloadDelaySeconds { get; }
        public ResourceLoadOptions LoadOptions { get; }
        public string HostCacheParent { get; }
        public string BuiltInVersion { get; }
        public string BuiltInManifestSha256 { get; }
        public Func<ResourcePreparationPlan, CancellationToken, ResourceOperationBase> BeforePrepareAsync { get; }

        /// <param name="downloads">缓存根为宿主目录；提供 packageName 时自动追加 ZRAssetPackages/包名。</param>
        /// <param name="builtInRoot">明确提供时为首包所在目录；null 按包名选择默认 StreamingAssets 目录。</param>
        public HotUpdateBootstrapOptions(string playerBuildId, BundleDownloadOptions downloads,
            ResourceReleaseTrustOptions trust = null, string releaseUrl = null, string builtInRoot = null,
            HotUpdateBootstrapMode mode = HotUpdateBootstrapMode.SignedUpdate, bool allowOfflineFallback = false,
            string manifestAddress = "hotupdate/manifest", double unloadDelaySeconds = 5, ResourceLoadOptions loadOptions = null,
            string hostCacheParent = null, string builtInVersion = null, string builtInManifestSha256 = null,
            Func<ResourcePreparationPlan, CancellationToken, ResourceOperationBase> beforePrepareAsync = null,
            string packageName = null, IResourceDecryptionServices decryptionServices = null,
            IResourceKeyProvider manifestKeys = null, IResourceManifestCodec manifestCodec = null, bool encodedBuiltInManifest = false)
        {
            _ = new HotUpdateOptions(playerBuildId);
            Downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
            HostCacheRoot = downloads.CacheRoot;
            if (packageName != null) {
                ResourcePackageIdentity.ValidateName(packageName);
                // Match ResourcePackage's namespace; the host lease continues to protect the parent root.
                Downloads = new BundleDownloadOptions(downloads.RemoteBaseUrl, downloads.CacheVersion,
                    ResourcePackageIdentity.GetCacheRoot(downloads.CacheRoot, packageName), downloads.MaxConcurrentDownloads,
                    downloads.RequestTimeoutSeconds, downloads.RetryPolicy, downloads.DiskPolicy, downloads.NetworkPolicy, downloads.SourceOptions);
            }
            PackageName = packageName;
            DecryptionServices = decryptionServices;
            ManifestKeys = manifestKeys;
            ManifestCodec = manifestCodec;
            EncodedBuiltInManifest = encodedBuiltInManifest;
            if (!Enum.IsDefined(typeof(HotUpdateBootstrapMode), mode)) {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            if (string.IsNullOrWhiteSpace(manifestAddress)) {
                throw new ArgumentException("代码清单地址不能为空。", nameof(manifestAddress));
            }

            if (double.IsNaN(unloadDelaySeconds) || double.IsInfinity(unloadDelaySeconds) || unloadDelaySeconds < 0) {
                throw new ArgumentOutOfRangeException(nameof(unloadDelaySeconds));
            }

            if (mode == HotUpdateBootstrapMode.SignedUpdate) {
                if (trust == null) {
                    throw new ArgumentNullException(nameof(trust));
                }

                trust.ValidateSource(releaseUrl);
            }
            if (builtInVersion != null && (!DownloadStorage.IsSafeSegment(builtInVersion) || !DownloadStorage.IsSha256(builtInManifestSha256))) {
                throw new ArgumentException("首次离线启动必须配置首包版本与清单 SHA-256。");
            }

            HostCacheParent = hostCacheParent == null ? null : Path.GetFullPath(hostCacheParent); BuiltInVersion = builtInVersion; BuiltInManifestSha256 = builtInManifestSha256;
            BeforePrepareAsync = beforePrepareAsync;
            PlayerBuildId = playerBuildId; Trust = trust; ReleaseUrl = releaseUrl;
            BuiltInRoot = builtInRoot ?? (packageName == null ? BundleLoader.Combine(Application.streamingAssetsPath, "ZRAsset") :
                BundleLoader.Combine(BundleLoader.Combine(Application.streamingAssetsPath, "ZRAsset/Packages"), packageName));
            ManifestAddress = manifestAddress; Mode = mode; AllowOfflineFallback = allowOfflineFallback;
            UnloadDelaySeconds = unloadDelaySeconds; LoadOptions = loadOptions ?? new ResourceLoadOptions();
        }
    }

    public sealed class HotUpdateBootstrapResult
    {
        public ResourceManager Resources { get; internal set; }
        public HotUpdateResult Code { get; internal set; }
        public string Version { get; internal set; }
        public bool UsedOfflineFallback { get; internal set; }
        public ResourcePreparationPlan PreparationPlan { get; internal set; }
        /// <summary>启动后独立进行的旧宿主清理；无清理任务时结果为 null。需要统计时可显式等待。</summary>
        public ResourceOperationBase<HostCacheCleanupResult> CacheCleanupOperation { get; internal set; }
        public HostCacheCleanupResult CacheCleanup
        {
            get
            {
                return CacheCleanupOperation?.Status == OperationStatus.Succeeded ? CacheCleanupOperation.Result : null;
            }
        }
    }

    /// <summary>
    /// 主线程一次性启动协调器：签名检查、磁盘准备、激活、打开资源和启动代码。
    /// 不创建业务入口，不安装 HybridCLR，不自动恢复已经提交的代码；业务仍负责资源的使用与释放。
    /// </summary>
    public sealed class HotUpdateBootstrap
    {
        private readonly HotUpdateBootstrapOptions m_options;
        private readonly IHotUpdateRuntime m_runtime;
        private readonly HotUpdateLoader m_loader;
        private readonly int m_threadId = Thread.CurrentThread.ManagedThreadId;
        private ResourceOperationBase<HotUpdateBootstrapResult> m_startup;
        private bool m_disposed;
        private CacheUsageLease m_hostLease;
        private readonly Func<ResourceOperationBase<HostCacheCleanupResult>> m_cleanOldHosts;
        public HostCacheCleanupResult CacheCleanup { get; private set; }
        public ResourceOperationBase<HostCacheCleanupResult> CacheCleanupOperation { get; private set; } = ResourceOperationBase.FromResult<HostCacheCleanupResult>(null);
        public HotUpdateFailureKind FailureKind { get; private set; }
        public HotUpdateBootstrapState State { get; private set; }
        public Exception Error { get; private set; }
        public ResourceFailure Failure { get { return ResourceFailure.FromException(Error); } }
        private Func<ResourceManager, CancellationToken, ResourceOperationBase> m_confirmReadyAsync;

        /// <summary>必须在 StartAsync 前配置；回调成功后才清除启动恢复记录。回调应响应取消。</summary>
        public Func<ResourceManager, CancellationToken, ResourceOperationBase> ConfirmReadyAsync
        {
            get { return m_confirmReadyAsync; }
            set
            {
                CheckThread();
                if (m_startup != null) {
                    throw new InvalidOperationException("启动后不能更改业务就绪回调。");
                }

                m_confirmReadyAsync = value;
            }
        }
        public Exception UpdateError { get; private set; }
        public ResourceManager Resources { get; private set; }
        public ResourcePreparationPlan PreparationPlan { get; private set; }
        public event Action<HotUpdateBootstrapState> StateChanged;
        public event Action<BundleDownloadProgress> DownloadProgressChanged;
        public event Action<ResourcePreparationProgress> PreparationProgressChanged;
        public ResourcePreparationProgress PreparationProgress { get; private set; }

        public HotUpdateBootstrap(HotUpdateBootstrapOptions options, IHotUpdateRuntime runtime = null)
            : this(options, runtime, null) { }

        internal HotUpdateBootstrap(HotUpdateBootstrapOptions options, IHotUpdateRuntime runtime,
            Func<ResourceOperationBase<HostCacheCleanupResult>> cleanOldHosts)
        {
            m_options = options ?? throw new ArgumentNullException(nameof(options));
            m_runtime = runtime ?? new HybridClrHotUpdateRuntime();
            m_cleanOldHosts = cleanOldHosts ?? (() => ResourceOperationBase.Run(() => HostCacheLifecycle.CleanOldHosts(options.HostCacheParent, options.HostCacheRoot)));
            m_loader = new HotUpdateLoader(m_runtime);
        }

        /// <summary>多次调用返回同一个任务；第一次调用的 token 决定提交代码前的取消边界。</summary>
        public ResourceOperationBase<HotUpdateBootstrapResult> StartAsync(CancellationToken cancellationToken = default)
        {
            CheckThread();
            return m_disposed
                ? throw new ObjectDisposedException(nameof(HotUpdateBootstrap))
                : m_startup ?? (m_startup = RunAsync(cancellationToken));
        }

        private async ResourceOperationBase<HotUpdateBootstrapResult> RunAsync(CancellationToken token)
        {
            // 先让 StartAsync 保存共享任务，事件回调即使再次调用 StartAsync 也不会重入第二次启动。
            await ResourceOperationBase.Yield();
            var started = ResourceTelemetry.StartTimer();
            ResourceVersionManager versions = null;
            bool fallback = false, pendingBoot = false;
            HotUpdateResult code = null;
            try {
                token.ThrowIfCancellationRequested();
                ChangeState(HotUpdateBootstrapState.CheckingEnvironment);
                // 先暴露后端或安装问题，不能下载并激活后才告诉用户当前进程不支持热更。
                m_runtime.ValidateEnvironment();
                if (m_options.HostCacheParent != null) {
                    m_hostLease = HostCacheLifecycle.Register(m_options.HostCacheParent, m_options.HostCacheRoot, m_options.PlayerBuildId);
                }

                versions = await ResourceVersionManager.CreateAsync(ResourcePlatform.Current.BuildTarget, m_options.Downloads, m_options.BuiltInRoot,
                    m_options.PackageName, m_options.DecryptionServices, m_options.ManifestKeys, m_options.ManifestCodec, m_options.EncodedBuiltInManifest, token);
                await versions.RecoverInterruptedBootAsync(token);
                versions.DownloadProgressChanged += OnDownload;
                versions.PreparationProgressChanged += OnPreparation;
                if (m_options.Mode == HotUpdateBootstrapMode.SignedUpdate) {
                    ResourceSignedUpdateCandidate candidate = null;
                    try {
                        ChangeState(HotUpdateBootstrapState.CheckingRelease);
                        candidate = await versions.FetchSignedReleaseAsync(m_options.ReleaseUrl, m_options.Trust, token, m_options.PlayerBuildId);
                        await versions.RequireBootAllowedAsync(candidate.Version, token);
                        ChangeState(HotUpdateBootstrapState.Planning);
                        PreparationPlan = await versions.PlanPrepareSignedReleaseAsync(candidate, token);
                        if (m_options.BeforePrepareAsync != null) {
                            ChangeState(HotUpdateBootstrapState.AwaitingDownloadConsent);
                            await m_options.BeforePrepareAsync(PreparationPlan, token);
                        }
                        token.ThrowIfCancellationRequested();
                        ChangeState(HotUpdateBootstrapState.Preparing);
                        await versions.PrepareSignedReleaseAsync(candidate, cancellationToken: token);
                        ChangeState(HotUpdateBootstrapState.OpeningResources);
                        Resources = await ResourceManager.CreateWithDownloadsAsync(candidate.OwnedManifest,
                            m_options.Downloads.ForVersion(candidate.Version), m_options.BuiltInRoot,
                            m_options.UnloadDelaySeconds, loadOptions: m_options.LoadOptions, decryptionServices: m_options.DecryptionServices,
                            manifestKeys: m_options.ManifestKeys, manifestCodec: m_options.ManifestCodec,
                            encodedBuiltInManifest: m_options.EncodedBuiltInManifest, cancellationToken: token);
                        Resources.DiagnosticName = "热更资源 " + candidate.Version;
                        Resources.DownloadProgressChanged += OnDownload;
                        // Loader 先验证宿主、所有 DLL 的哈希与程序集身份；只有全部通过才执行此提交回调。
                        code = await m_loader.StartWithCommitAsync(Resources, m_options.ManifestAddress,
                            new HotUpdateOptions(m_options.PlayerBuildId), async () =>
                            {
                                token.ThrowIfCancellationRequested();
                                if (candidate.IsNewVersion) { await versions.BeginBootAsync(candidate.Version, token); pendingBoot = true; }
                                ChangeState(HotUpdateBootstrapState.Activating);
                                await versions.ActivateAsync(candidate.Version, token);
                                ChangeState(HotUpdateBootstrapState.StartingCode);
                            }, token);
                    }
                    catch (Exception exception) when (!(exception is OperationCanceledException) &&
                        !(exception is HotUpdateRestartRequiredException) && !(exception is HotUpdateHostMismatchException) && !pendingBoot &&
                        m_loader.State != HotUpdateState.Started && m_options.AllowOfflineFallback) {
                        await versions.RefreshAsync(token);
                        if (versions.ActiveVersion == null && m_options.BuiltInVersion == null) {
                            throw;
                        }

                        if (Resources != null) {
                            Resources.DownloadProgressChanged -= OnDownload;
                            await Resources.DisposeAsync(); Resources = null;
                        }
                        UpdateError = exception;
                        fallback = true;
                    }
                }
                if (code == null) {
                    token.ThrowIfCancellationRequested();
                    ChangeState(HotUpdateBootstrapState.OpeningResources);
                    var offline = m_options.Mode == HotUpdateBootstrapMode.OfflineActive || fallback;
                    if (offline && versions.ActiveVersion == null && m_options.BuiltInVersion != null) {
                        ChangeState(HotUpdateBootstrapState.Preparing);
                        await versions.RequireBootAllowedAsync(m_options.BuiltInVersion, token);
                        ResourceManifest manifest = await versions.PrepareLocalBuiltInAsync(m_options.BuiltInVersion, m_options.BuiltInManifestSha256, token);
                        Resources = await ResourceManager.CreateWithDownloadsCoreAsync(manifest, m_options.Downloads.ForVersion(m_options.BuiltInVersion),
                            m_options.BuiltInRoot, m_options.UnloadDelaySeconds, null, m_options.LoadOptions, m_options.DecryptionServices,
                            m_options.ManifestKeys, m_options.ManifestCodec, m_options.EncodedBuiltInManifest ? "manifest.zrme" : "manifest.json", token, true);
                        code = await m_loader.StartWithCommitAsync(Resources, m_options.ManifestAddress, new HotUpdateOptions(m_options.PlayerBuildId), async () =>
                        {
                            await versions.BeginBootAsync(m_options.BuiltInVersion, token); pendingBoot = true;
                            ChangeState(HotUpdateBootstrapState.Activating);
                            await versions.ActivateLocalAsync(m_options.BuiltInVersion, token);
                            ChangeState(HotUpdateBootstrapState.StartingCode);
                        }, token);
                    }
                    else {
                        Resources = offline ? await versions.CreateOfflineManagerAsync(m_options.UnloadDelaySeconds, m_options.LoadOptions, token) :
                        await versions.CreateActiveManagerAsync(m_options.UnloadDelaySeconds, loadOptions: m_options.LoadOptions, cancellationToken: token);
                    }

                    Resources.DiagnosticName = "热更资源 " + versions.ActiveVersion;
                    Resources.DownloadProgressChanged += OnDownload;
                    if (code == null) {
                        ChangeState(HotUpdateBootstrapState.StartingCode);
                        code = await m_loader.StartAsync(Resources, m_options.ManifestAddress, new HotUpdateOptions(m_options.PlayerBuildId), token);
                    }
                }
                if (m_confirmReadyAsync != null) {
                    ChangeState(HotUpdateBootstrapState.AwaitingReady);
                    await m_confirmReadyAsync(Resources, token);
                    token.ThrowIfCancellationRequested();
                }
                if (pendingBoot) { await versions.CompleteBootAsync(); pendingBoot = false; }
                ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.Startup,
                    Resources.PackageName, versions.ActiveVersion, durationMilliseconds: ResourceTelemetry.Elapsed(started)));
                ChangeState(HotUpdateBootstrapState.Started);
                if (m_options.HostCacheParent != null) {
                    CacheCleanupOperation = CleanOldHostsAsync();
                }

                return new HotUpdateBootstrapResult
                {
                    Resources = Resources,
                    Code = code,
                    Version = versions.ActiveVersion,
                    UsedOfflineFallback = fallback,
                    PreparationPlan = PreparationPlan,
                    CacheCleanupOperation = CacheCleanupOperation
                };
            }
            catch (Exception exception) {
                Exception failure = exception;
                if (pendingBoot && m_loader.State == HotUpdateState.Idle) {
                    try { await versions.AbortBootBeforeCommitAsync(); pendingBoot = false; }
                    catch (Exception cleanupError) {
                        failure = new HotUpdateRestartRequiredException("代码尚未提交，但启动记录清理未完成，请重启后恢复。",
                            new AggregateException(exception, cleanupError));
                    }
                }
                Error = failure;
                FailureKind = failure is HotUpdateHostMismatchException ? HotUpdateFailureKind.HostUpgradeRequired :
                    failure is OperationCanceledException ? HotUpdateFailureKind.Cancelled : HotUpdateFailureKind.Failed;
                var restart = pendingBoot || m_loader.State == HotUpdateState.Started ||
                    m_loader.State == HotUpdateState.RequiresRestart || failure is HotUpdateRestartRequiredException;
                if (restart) {
                    FailureKind = HotUpdateFailureKind.RestartRequired;
                    ResourceFailure.Annotate(failure, ResourceErrorCode.RestartRequired,
                        State == HotUpdateBootstrapState.AwaitingReady ? ResourceStage.HealthCheck : ResourceStage.StartCode, overwrite: true);
                }
                else {
                    ResourceStage stage = State switch
                    {
                        HotUpdateBootstrapState.CheckingRelease => ResourceStage.CheckRelease,
                        HotUpdateBootstrapState.Planning or HotUpdateBootstrapState.Preparing or
                            HotUpdateBootstrapState.OpeningResources or HotUpdateBootstrapState.AwaitingDownloadConsent => ResourceStage.Prepare,
                        HotUpdateBootstrapState.Activating => ResourceStage.Activate,
                        HotUpdateBootstrapState.StartingCode => ResourceStage.StartCode,
                        _ => ResourceStage.None
                    };
                    ResourceFailure.Annotate(failure, ResourceFailure.FromException(failure).Code, stage);
                }
                ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.Startup,
                    Resources?.PackageName, versions?.ActiveVersion, failure: ResourceFailure.FromException(failure, ResourceStage.StartCode), durationMilliseconds: ResourceTelemetry.Elapsed(started)));
                ChangeState(restart ? HotUpdateBootstrapState.RequiresRestart : HotUpdateBootstrapState.Failed);
                // 代码可能已经创建业务 Handle。提交失败时保留管理器供业务检查、退出，不擅自卸载这些资源。
                if (!restart && Resources != null) {
                    Resources.DownloadProgressChanged -= OnDownload;
                    try { await Resources.DisposeAsync(); Resources = null; }
                    catch (Exception cleanupError) { Debug.LogException(cleanupError); }
                }
                if (!ReferenceEquals(failure, exception)) {
                    throw failure;
                }

                throw;
            }
            finally {
                if (versions != null) { versions.DownloadProgressChanged -= OnDownload; versions.PreparationProgressChanged -= OnPreparation; }
            }
        }

        internal sealed class OfflineTransport: IDownloadTransport
        {
            public ResourceOperationBase<DownloadTransportResponse> SendAsync(DownloadTransportRequest request, Action<long> progress, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return ResourceOperationBase.FromException<DownloadTransportResponse>(new IOException("离线版本的缓存或首包不完整；离线策略禁止网络补齐。"));
            }
        }

        private async ResourceOperationBase<HostCacheCleanupResult> CleanOldHostsAsync()
        {
            // 先完成启动，让业务可立即继续；清理失败只产生维护警告，不改变已启动状态。
            await ResourceOperationBase.Yield();
            try { CacheCleanup = await m_cleanOldHosts(); }
            catch (Exception error) { CacheCleanup = new HostCacheCleanupResult { Warnings = new[] { error.Message } }; }
            return CacheCleanup;
        }

        /// <summary>启动任务结束且业务凭证全部释放后关闭资源，并排空后台清理；关闭不能撤销已加载代码。</summary>
        public async ResourceOperationBase DisposeAsync()
        {
            CheckThread();
            if (m_disposed) {
                return;
            }

            if (m_startup != null && !m_startup.IsDone) {
                throw new InvalidOperationException("请先取消并等待启动任务结束，再关闭启动器。");
            }

            if (Resources != null) {
                Resources.DownloadProgressChanged -= OnDownload;
                await Resources.DisposeAsync();
                Resources = null;
            }
            await CacheCleanupOperation;
            m_hostLease?.Dispose(); m_hostLease = null;
            m_disposed = true;
            // 不覆盖必须重启的终态，防止关闭资源后误以为代码仍可重新启动。
            if (State != HotUpdateBootstrapState.RequiresRestart) {
                ChangeState(HotUpdateBootstrapState.Closed);
            }
        }

        private void ChangeState(HotUpdateBootstrapState state)
        {
            State = state;
            if (StateChanged == null) {
                return;
            }

            foreach (Action<HotUpdateBootstrapState> callback in StateChanged.GetInvocationList()) {
                try { callback(state); } catch (Exception error) { Debug.LogException(error); }
            }
        }
        private void OnPreparation(ResourcePreparationProgress progress)
        {
            PreparationProgress = progress;
            if (PreparationProgressChanged == null) {
                return;
            }

            foreach (Action<ResourcePreparationProgress> callback in PreparationProgressChanged.GetInvocationList()) {
                try { callback(progress); } catch (Exception error) { Debug.LogException(error); }
            }
        }
        private void OnDownload(BundleDownloadProgress progress)
        {
            if (DownloadProgressChanged == null) {
                return;
            }

            foreach (Action<BundleDownloadProgress> callback in DownloadProgressChanged.GetInvocationList()) {
                try { callback(progress); } catch (Exception error) { Debug.LogException(error); }
            }
        }
        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != m_threadId) {
                throw new InvalidOperationException("启动协调器必须在创建它的 Unity 主线程调用。");
            }
        }
    }
}

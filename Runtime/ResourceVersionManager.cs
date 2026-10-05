using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>发布端的当前版本描述；清单哈希绑定版本号与实际清单文件。</summary>
    [Serializable]
    public sealed class ResourceReleaseInfo
    {
        public string Version;
        public string BuildTarget;
        public string ManifestUrl;
        public string ManifestSha256;

        public static ResourceReleaseInfo ForManifest(string version, string buildTarget,
            string manifestUrl, string manifestJson)
        {
            return new ResourceReleaseInfo
            {
                Version = version,
                BuildTarget = buildTarget,
                ManifestUrl = manifestUrl,
                ManifestSha256 = ResourceVersionManager.ComputeTextSha256(manifestJson)
            };
        }
    }

    /// <summary>已取得且通过发布描述哈希校验的目标版本。</summary>
    public sealed class ResourceUpdateCandidate
    {
        public string Version { get; internal set; }
        public ResourceManifest Manifest { get; internal set; }
        public ResourceVersionDiff Diff { get; internal set; }
        public bool IsNewVersion { get; internal set; }
    }

    /// <summary>版本差异是清单差异；下载列表可能命中已有的内容哈希缓存。</summary>
    public sealed class ResourceVersionDiff
    {
        public string[] DownloadBundles { get; internal set; } = Array.Empty<string>();
        public string[] ReusedBundles { get; internal set; } = Array.Empty<string>();
        public string[] RemovedBundles { get; internal set; } = Array.Empty<string>();
        public string[] AddedAddresses { get; internal set; } = Array.Empty<string>();
        public string[] ChangedAddresses { get; internal set; } = Array.Empty<string>();
        public string[] RemovedAddresses { get; internal set; } = Array.Empty<string>();
        // 兼容旧界面的逻辑差分字节量，不扫描磁盘；实际完整缺失量请使用 PlanPrepareAsync.DownloadBytes。
        public long EstimatedDownloadBytes { get; internal set; }
    }

    /// <summary>
    /// V5 版本目录：先准备所有文件和不可变清单，再原子替换 active.json 指针。
    /// 已创建的 ResourceManager 持有旧清单快照；应用应先释放旧管理器再创建新版。
    /// 所有公开方法都须从创建时的 Unity 主线程调用，不允许两个版本操作并行。
    /// </summary>
    public sealed partial class ResourceVersionManager
    {
        [Serializable]
        private sealed class VersionPointer
        {
            public string Active;
            public string Previous;
            public string ManifestSha256;
            // 显式布尔值区分旧版全量指针与空选择；JsonUtility 会把 null 数组写为 []。
            public bool HasSelection;
            public string[] RequiredBundles;
            public bool PreviousHasSelection;
            public string[] PreviousRequiredBundles;
        }

        private readonly BundleDownloadOptions m_options;
        private readonly string m_builtInRoot;
        private readonly string m_buildTarget;
        private readonly string m_versionRoot;
        private readonly IResourceDecryptionServices m_decryptionServices;
        private readonly IResourceKeyProvider m_manifestKeys;
        private readonly IResourceManifestCodec m_manifestCodec;
        private readonly string m_builtInManifestFileName;
        private readonly int m_threadId;
        internal ResourcePackage SchedulingPackage { get; set; }
        private bool m_busy;
        private bool m_packageOwnerDisposed;
        private ResourceFileLock m_updateLock;
        private CacheUsageLease m_cacheUsageLease;
        private VersionPointer m_pointer;
        private ResourceManifest m_activeManifest;

        public string ActiveVersion { get { CheckThread(); return m_pointer?.Active; } }
        public string PackageName { get; }
        public string CacheRoot
        {
            get
            {
                return m_options.CacheRoot;
            }
        }

        public bool IsBusy { get { CheckThread(); return m_busy; } }
        public string PreviousVersion { get { CheckThread(); return m_pointer?.Previous; } }
        public bool IsActiveVersionPartial
        {
            get { CheckThread(); return m_pointer?.HasSelection == true && m_pointer.RequiredBundles.Length < m_activeManifest.Bundles.Length; }
        }
        public IReadOnlyList<string> ActiveRequiredBundles
        {
            get
            {
                CheckThread();
                return Array.AsReadOnly(m_pointer == null ? Array.Empty<string>() : m_pointer.HasSelection
                    ? (string[])m_pointer.RequiredBundles.Clone() : m_activeManifest.Bundles.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            }
        }
        public ResourceManifest ActiveManifest
        {
            get { CheckThread(); return m_activeManifest?.CopyUnchecked(); }
        }
        public event Action<BundleDownloadProgress> DownloadProgressChanged;

        public ResourceVersionManager(string buildTarget, BundleDownloadOptions options, string builtInRoot = null)
            : this(buildTarget, options, builtInRoot, null) { }

        /// <summary>显式绑定包身份；缓存根由 ResourcePackage 配置为包级独立目录。</summary>
        public ResourceVersionManager(string buildTarget, BundleDownloadOptions options, string builtInRoot, string packageName,
            IResourceDecryptionServices decryptionServices = null, IResourceKeyProvider manifestKeys = null, IResourceManifestCodec manifestCodec = null,
            bool encodedBuiltInManifest = false)
            : this(buildTarget, options, builtInRoot, packageName, decryptionServices, manifestKeys, manifestCodec,
                encodedBuiltInManifest ? ResourceManifestFormat.Encoded : ResourceManifestFormat.Json, true)
        { }

        private ResourceVersionManager(string buildTarget, BundleDownloadOptions options, string builtInRoot, string packageName,
            IResourceDecryptionServices decryptionServices, IResourceKeyProvider manifestKeys, IResourceManifestCodec manifestCodec,
            ResourceManifestFormat builtInFormat, bool refresh)
        {
            ResourcePlatform.RequireVersionTransactions();
            m_options = options ?? throw new ArgumentNullException(nameof(options));
            if (!DownloadStorage.IsSafeSegment(buildTarget)) {
                throw new ArgumentException("BuildTarget 必须是安全的单层目录名。", nameof(buildTarget));
            }

            m_buildTarget = buildTarget;
            if (packageName != null) {
                ResourcePackageIdentity.ValidateName(packageName);
            }

            PackageName = packageName;
            m_decryptionServices = decryptionServices;
            m_manifestKeys = manifestKeys;
            m_manifestCodec = manifestCodec;
            m_builtInManifestFileName = ResourceInitializationOptions.FileName(builtInFormat);
            m_builtInRoot = builtInRoot ?? ResourcePath.Combine(Application.streamingAssetsPath, "ZRAsset");
            m_versionRoot = DownloadStorage.ValidatePath(options.CacheRoot,
                Path.Combine(options.CacheRoot, "ZRAssetVersions", buildTarget, "versions"));
            m_threadId = Thread.CurrentThread.ManagedThreadId;
            if (refresh) { Refresh(); }
        }

        /// <summary>异步读取并校验活动指针；大型清单的启动入口应使用此工厂，避免构造函数同步刷新。</summary>
        public static async ResourceOperationBase<ResourceVersionManager> CreateAsync(string buildTarget, BundleDownloadOptions options,
            string builtInRoot = null, string packageName = null, IResourceDecryptionServices decryptionServices = null,
            IResourceKeyProvider manifestKeys = null, IResourceManifestCodec manifestCodec = null,
            bool encodedBuiltInManifest = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manager = new ResourceVersionManager(buildTarget, options, builtInRoot, packageName,
                decryptionServices, manifestKeys, manifestCodec, encodedBuiltInManifest ? ResourceManifestFormat.Encoded : ResourceManifestFormat.Json, false);
            await manager.RefreshAsync(cancellationToken);
            return manager;
        }

        /// <summary>其他进程可能已提交新指针；调用此方法重新读取磁盘上的活动版本。</summary>
        public void Refresh()
        {
            CheckThread();
            if (m_busy) {
                throw new InvalidOperationException("版本操作尚未完成。");
            }

            using (CacheUsageLease.Acquire(m_options.CacheRoot))
            using (AcquireLock()) {
                RecoverPointer();
            }
        }

        /// <summary>
        /// 从受信任的 HTTP(S) 发布描述取得目标清单，检查同源 URL、平台和清单哈希。
        /// 此操作只检查版本，不准备 Bundle，也不改变活动指针。
        /// </summary>
        public ResourceOperationBase<ResourceUpdateCandidate> FetchReleaseAsync(string releaseUrl)
        {
            return FetchReleaseAsync(releaseUrl, default);
        }

        public ResourceOperationBase<ResourceUpdateCandidate> FetchReleaseAsync(string releaseUrl, CancellationToken cancellationToken)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            return FetchReleaseAsyncScheduled(releaseUrl, cancellationToken);
        }

        private async ResourceOperationBase<ResourceUpdateCandidate> FetchReleaseAsyncScheduled(string releaseUrl, CancellationToken cancellationToken)
        {
            CheckThread();
            await EnterAsync(cancellationToken);
            try {
                if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out Uri source) ||
                    (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps)) {
                    throw new ArgumentException("发布描述地址必须是 HTTP(S) URL。", nameof(releaseUrl));
                }

                var releaseJson = await ResourceFileReader.ReadTextAsync(source.AbsoluteUri, cancellationToken, m_options.NetworkPolicy);
                ResourceReleaseInfo release = JsonUtility.FromJson<ResourceReleaseInfo>(releaseJson);
                ValidateVersion(release?.Version);
                if (!string.Equals(release.BuildTarget, m_buildTarget, StringComparison.Ordinal)) {
                    throw new InvalidDataException("发布描述的平台与当前平台不一致。");
                }

                if (!DownloadStorage.IsSha256(release.ManifestSha256) ||
                    string.IsNullOrWhiteSpace(release.ManifestUrl) ||
                    !Uri.TryCreate(source, release.ManifestUrl, out Uri manifestUri) ||
                    manifestUri.Scheme != source.Scheme || manifestUri.Host != source.Host || manifestUri.Port != source.Port) {
                    throw new InvalidDataException("发布描述的清单地址或哈希无效，或不在同一来源。");
                }

                var manifestJson = await ResourceFileReader.ReadTextAsync(manifestUri.AbsoluteUri, cancellationToken, m_options.NetworkPolicy);
                if (!string.Equals(await HashAsync(manifestJson, cancellationToken), release.ManifestSha256, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("目标清单 SHA-256 与发布描述不一致。");
                }

                ResourceManifest manifest = await ParseTargetAsync(manifestJson, release.Version, cancellationToken, true);
                return string.Equals(release.Version, m_pointer?.Active, StringComparison.Ordinal) &&
                    !await SameManifestAsync(await SerializeAsync(manifest, cancellationToken), await SerializeAsync(m_activeManifest, cancellationToken), cancellationToken)
                    ? throw new InvalidDataException("活动版本号对应了不同清单；发布端必须使用新版本号。")
                    : new ResourceUpdateCandidate
                    {
                        Version = release.Version,
                        Manifest = manifest,
                        Diff = await CompareOwnedAsync(manifest, cancellationToken),
                        IsNewVersion = !string.Equals(release.Version, m_pointer?.Active, StringComparison.Ordinal)
                    };
            }
            finally { Exit(); }
        }

        /// <summary>比较目标清单与当前活动清单，不进行网络或文件写入。</summary>
        public ResourceVersionDiff Compare(ResourceManifest target)
        {
            CheckThread();
            if (!m_busy) {
                Refresh();
            }

            target = ValidateTarget(target);
            ResourceVersionDiff result = null;
            ResourceManifestWork.Drain(DiffSteps(m_activeManifest, target, value => result = value));
            return result;
        }

        /// <summary>准备完整目标版本。失败或取消时保留已校验的内容缓存，但不会发布版本清单和活动指针。</summary>
        internal ResourceOperationBase<ResourceManifest> PrepareLocalBuiltInAsync(string version, string expectedHash, CancellationToken token)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            return PrepareLocalBuiltInAsyncScheduled(version, expectedHash, token);
        }

        private async ResourceOperationBase<ResourceManifest> PrepareLocalBuiltInAsyncScheduled(string version, string expectedHash, CancellationToken token)
        {
            if (string.IsNullOrEmpty(m_builtInRoot) || ResourcePath.Classify(m_builtInRoot) == ResourceLocationKind.HttpUri) {
                throw new InvalidOperationException("首次离线启动必须使用本地首包。");
            }

            var json = await ResourceFileReader.ReadTextAsync(ResourcePath.Combine(m_builtInRoot, m_builtInManifestFileName), token, m_options.NetworkPolicy);
            if (!string.Equals(await HashAsync(json, token), expectedHash, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("首包清单校验失败。");
            }

            ResourceManifest manifest = await ParseTargetAsync(json, version, token, true);
            await PrepareCoreAsync(version, manifest, DownloadPriority.Normal, token, null, true, takeOwnership: true);
            return manifest;
        }

        internal ResourceOperationBase ActivateLocalAsync(string version, CancellationToken token)
        {
            return ActivateCoreAsync(version, token, offline: true);
        }

        public ResourceOperationBase PrepareAsync(string version, ResourceManifest target,
            DownloadPriority priority = DownloadPriority.Normal, CancellationToken cancellationToken = default)
        {
            return PrepareCoreAsync(version, target, priority, cancellationToken, null);
        }

        /// <summary>准备所选依赖并保存完整清单；不会激活。其他内容可在激活后按需下载。</summary>
        public ResourceOperationBase PrepareSelectionAsync(string version, ResourceManifest target, ResourceSelection selection,
            DownloadPriority priority = DownloadPriority.Normal, CancellationToken cancellationToken = default)
        {
            return selection == null
                ? throw new ArgumentNullException(nameof(selection))
                : PrepareCoreAsync(version, target, priority, cancellationToken, null, selection: selection);
        }

        private ResourceOperationBase PrepareCoreAsync(string version, ResourceManifest target,
            DownloadPriority priority, CancellationToken cancellationToken, ResourceSignedUpdateCandidate signed, bool offline = false,
            ResourceSelection selection = null, bool takeOwnership = false)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            return PrepareCoreAsyncScheduled(version, target, priority, cancellationToken, signed, offline, selection, takeOwnership);
        }

        private async ResourceOperationBase PrepareCoreAsyncScheduled(string version, ResourceManifest target,
            DownloadPriority priority, CancellationToken cancellationToken, ResourceSignedUpdateCandidate signed, bool offline = false,
            ResourceSelection selection = null, bool takeOwnership = false)
        {
            CheckThread();
            await EnterAsync(cancellationToken);
            try {
                if (signed != null) {
                    CheckAcceptedRelease(ResourceReleaseAuthentication.Verify(signed.EnvelopeJson, signed.Trust, DateTimeOffset.UtcNow));
                }

                ValidateVersion(version);
                ResourceManifest snapshot = takeOwnership ? await ValidateOwnedTargetAsync(target, version, cancellationToken) : await ValidateTargetAsync(target, version, cancellationToken);
                BundleInfo[] selectedBundles = await SelectBundlesAsync(snapshot, selection, cancellationToken);
                var manifestPath = ManifestPath(version);
                var json = await SerializeAsync(snapshot, cancellationToken);
                if (File.Exists(manifestPath) && !await SameManifestAsync(await ResourceFileReader.ReadTextAsync(manifestPath, cancellationToken), json, cancellationToken)) {
                    throw new InvalidOperationException("同一版本号已经保存了不同清单；请使用新的版本号。");
                }

                cancellationToken.ThrowIfCancellationRequested();
                CachedResourceFileSystem cache = await CreateVersionCacheAsync(version, cancellationToken, offline);
                cache.DownloadQueue.ProgressChanged += OnDownloadProgress;
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
                    try {
                        // 默认准备完整清单；显式选择只准备依赖闭包，但始终保存完整、不可变的发布清单。
                        Action<long> completed = TrackPreparation(ResourcePreparationPhase.Preparing, selectedBundles);
                        await ResourceWorkBatch.RunAsync(selectedBundles, m_options.MaxConcurrentDownloads,
                            (bundle, token) => PrepareBundleAsync(bundle, token), linked.Token);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (signed != null) {
                            AcceptRelease(ResourceReleaseAuthentication.Verify(signed.EnvelopeJson, signed.Trust, DateTimeOffset.UtcNow));
                        }

                        if (!File.Exists(manifestPath)) {
                            await ResourceFileIO.Shared.WriteAtomicAsync(manifestPath, json);
                        }

                        await ResourcePersistence.FlushAsync();

                        async ResourceOperationBase<string> PrepareBundleAsync(BundleInfo bundle, CancellationToken token)
                        {
                            try {
                                ResourceFileLocation file = await cache.ResolveAsync(bundle, priority, token);
                                completed(bundle.Size);
                                return file.Location;
                            }
                            catch {
                                // 单包最终失败时立即取消同批下载；WhenAll 仍负责等所有子操作安全收尾。
                                linked.Cancel();
                                throw;
                            }
                        }
                    }
                    catch {
                        linked.Cancel();
                        throw;
                    }
                    finally {
                        cache.DownloadQueue.ProgressChanged -= OnDownloadProgress;
                        await cache.DisposeAsync();
                    }
                }
            }
            finally { Exit(); }
        }

        /// <summary>重新校验已准备版本后切换活动指针；提交点之后的取消不会撤销已完成的提交。</summary>
        public ResourceOperationBase ActivateAsync(string version, CancellationToken cancellationToken = default)
        {
            return ActivateCoreAsync(version, cancellationToken);
        }

        public ResourceOperationBase ActivateSelectionAsync(string version, ResourceSelection selection, CancellationToken cancellationToken = default)
        {
            return selection == null
                ? throw new ArgumentNullException(nameof(selection))
                : ActivateCoreAsync(version, cancellationToken, selection: selection);
        }

        /// <summary>切回上一次活动版本。旧文件若已损坏，先尝试从首包或远端恢复，失败则保持当前版本。</summary>
        public ResourceOperationBase RollbackAsync(CancellationToken cancellationToken = default)
        {
            return ActivateCoreAsync(null, cancellationToken, true);
        }

        /// <summary>按当前活动清单创建新管理器；切换版本前先释放旧管理器及其所有 Handle。</summary>
        public ResourceOperationBase<ResourceManager> CreateActiveManagerAsync(double unloadDelaySeconds = 5,
            ResourceRetryPolicy retryPolicy = null, ResourceLoadOptions loadOptions = null, IResourceDecryptionServices decryptionServices = null,
            CancellationToken cancellationToken = default)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            return CreateActiveManagerAsyncScheduled(unloadDelaySeconds, retryPolicy, loadOptions, decryptionServices, cancellationToken);
        }

        internal ResourceOperationBase<ResourceManager> CreateOfflineManagerAsync(double unloadDelaySeconds,
            ResourceLoadOptions loadOptions, CancellationToken token)
        {
            return CreateActiveManagerAsyncScheduled(unloadDelaySeconds, null, loadOptions, null, token, true);
        }

        private async ResourceOperationBase<ResourceManager> CreateActiveManagerAsyncScheduled(double unloadDelaySeconds,
            ResourceRetryPolicy retryPolicy, ResourceLoadOptions loadOptions, IResourceDecryptionServices decryptionServices,
            CancellationToken token, bool offline = false)
        {
            CheckThread();
            if (m_busy) {
                throw new InvalidOperationException("版本操作尚未完成。");
            }
            // 从读取活动指针到新管理器建立自己的租约之间，也不能让维护删除所选版本。
            await EnterAsync(token);
            try {
                return m_pointer == null
                    ? throw new InvalidOperationException("尚无活动版本。")
                    : await ResourceManager.CreateWithDownloadsCoreAsync(m_activeManifest, OptionsFor(m_pointer.Active),
                    m_builtInRoot, unloadDelaySeconds, retryPolicy, loadOptions, decryptionServices ?? m_decryptionServices,
                    m_manifestKeys, m_manifestCodec, m_builtInManifestFileName, token, offline);
            }
            finally { Exit(); }
        }

        private ResourceOperationBase ActivateCoreAsync(string version, CancellationToken cancellationToken, bool rollback = false, bool offline = false,
            ResourceSelection selection = null, bool verifyOnly = false)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            return ActivateCoreAsyncScheduled(version, cancellationToken, rollback, offline, selection, verifyOnly);
        }

        private async ResourceOperationBase ActivateCoreAsyncScheduled(string version, CancellationToken cancellationToken, bool rollback = false, bool offline = false,
            ResourceSelection selection = null, bool verifyOnly = false,
            Func<ResourceManifest, CancellationToken, ResourceOperationBase> beforeCommit = null)
        {
            CheckThread();
            await EnterAsync(cancellationToken);
            try {
                if (rollback) {
                    if (m_pointer == null || string.IsNullOrEmpty(m_pointer.Previous)) {
                        throw new InvalidOperationException("没有可回滚的上一版本。");
                    }

                    version = m_pointer.Previous;
                }
                ValidateVersion(version);
                cancellationToken.ThrowIfCancellationRequested();
                var manifestPath = ManifestPath(version);
                if (!File.Exists(manifestPath)) {
                    throw new FileNotFoundException("目标版本尚未保存准备清单。", manifestPath);
                }

                var json = await ResourceFileReader.ReadTextAsync(manifestPath, cancellationToken);
                ResourceManifest manifest = await ParseTargetAsync(json, version, cancellationToken);
                var hasSelection = rollback ? m_pointer.PreviousHasSelection : selection != null;
                BundleInfo[] selectedBundles = rollback && hasSelection ? await ValidateRequiredBundlesAsync(manifest, m_pointer.PreviousRequiredBundles, cancellationToken) :
                    await SelectBundlesAsync(manifest, selection, cancellationToken);
                CachedResourceFileSystem cache = await CreateVersionCacheAsync(version, cancellationToken, offline);
                cache.DownloadQueue.ProgressChanged += OnDownloadProgress;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try {
                    Action<long> completed = TrackPreparation(ResourcePreparationPhase.Activating, selectedBundles);

                    async ResourceOperationBase ValidateBundleAsync(BundleInfo bundle)
                    {
                        try {
                            ResourceFileLocation file = await cache.ResolveAsync(bundle, DownloadPriority.High, linked.Token);
                            if (bundle.IsEncrypted) {
                                using Stream stream = ResourceEncryption.OpenRead(file, bundle, m_decryptionServices);
                                var hash = await ResourceFileIO.Shared.HashRangeAsync(stream, 0, bundle.ContentSize, linked.Token);
                                if (!string.Equals(hash, bundle.ContentSha256, StringComparison.OrdinalIgnoreCase)) {
                                    throw new InvalidDataException("解密后内容校验失败：" + bundle.Name);
                                }
                            }
                            completed(bundle.Size);
                        }
                        catch {
                            // 激活/回滚补齐文件时同样在首个失败后取消同批请求，并等待安全排空。
                            linked.Cancel();
                            throw;
                        }
                    }
                    await ResourceWorkBatch.RunAsync(selectedBundles, m_options.MaxConcurrentDownloads, (bundle, token) => ValidateBundleAsync(bundle), linked.Token);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                finally {
                    cache.DownloadQueue.ProgressChanged -= OnDownloadProgress;
                    await cache.DisposeAsync();
                }
                if (verifyOnly) {
                    return;
                }

                var next = new VersionPointer
                {
                    Active = version,
                    Previous = m_pointer?.Active == version ? m_pointer.Previous : m_pointer?.Active,
                    ManifestSha256 = await HashAsync(json, cancellationToken),
                    HasSelection = hasSelection,
                    RequiredBundles = hasSelection ? selectedBundles.Select(b => b.Name).ToArray() : Array.Empty<string>(),
                    PreviousHasSelection = m_pointer?.Active == version ? m_pointer.PreviousHasSelection : m_pointer?.HasSelection ?? false,
                    PreviousRequiredBundles = m_pointer?.Active == version ? m_pointer.PreviousRequiredBundles : m_pointer?.RequiredBundles
                };
                if (beforeCommit != null) { await beforeCommit(manifest, cancellationToken); }
                cancellationToken.ThrowIfCancellationRequested();
                // 此处是唯一提交点；先前任何失败都不会影响旧指针。
                string pointerPath = PointerPath(), backupPath = BackupPath();
                var oldPointer = File.Exists(pointerPath) ? File.ReadAllText(pointerPath) : null;
                var oldBackup = File.Exists(backupPath) ? File.ReadAllText(backupPath) : null;
                WriteAtomic(pointerPath, JsonUtility.ToJson(next), backupPath);
                try { await ResourcePersistence.FlushAsync(); }
                catch {
                    // IndexedDB/宿主存储拒绝提交时，不能让同进程重开读到尚未确认的活动版本。
                    if (oldPointer == null) {
                        DownloadStorage.DeleteFile(pointerPath);
                    }
                    else {
                        WriteAtomic(pointerPath, oldPointer);
                    }

                    if (oldBackup == null) {
                        DownloadStorage.DeleteFile(backupPath);
                    }
                    else {
                        WriteAtomic(backupPath, oldBackup);
                    }

                    try { await ResourcePersistence.FlushAsync(); } catch { /* 原始持久化错误继续传播。 */ }
                    throw;
                }
                m_pointer = next;
                m_activeManifest = manifest;
            }
            finally { Exit(); }
        }

        private void RecoverPointer()
        {
            var current = PointerPath();
            if (!File.Exists(current) && !File.Exists(BackupPath())) {
                m_pointer = null;
                m_activeManifest = null;
                return;
            }
            if (TryReadPointer(current, out VersionPointer valid)) { m_pointer = valid; return; }
            if (TryReadPointer(BackupPath(), out valid)) {
                // 被中断或损坏的指针退回上一个已提交快照。
                WriteAtomic(current, JsonUtility.ToJson(valid));
                m_pointer = valid;
                return;
            }
            throw new InvalidDataException("活动版本指针及备份都无法通过清单校验。");
        }

        private bool TryReadPointer(string path, out VersionPointer value)
        {
            value = null;
            if (!File.Exists(path)) {
                return false;
            }

            try {
                VersionPointer candidate = JsonUtility.FromJson<VersionPointer>(File.ReadAllText(path));
                ValidateVersion(candidate?.Active);
                if (!string.IsNullOrEmpty(candidate.Previous)) {
                    ValidateVersion(candidate.Previous);
                }

                var json = File.ReadAllText(ManifestPath(candidate.Active));
                if (!string.Equals(Hash(json), candidate.ManifestSha256, StringComparison.OrdinalIgnoreCase)) {
                    return false;
                }

                m_activeManifest = ValidateTarget(ResourceManifest.FromJson(json), candidate.Active);
                if (candidate.HasSelection) {
                    ValidateRequiredBundles(m_activeManifest, candidate.RequiredBundles);
                }

                if (candidate.PreviousHasSelection) {
                    if (string.IsNullOrEmpty(candidate.Previous)) {
                        return false;
                    }

                    ResourceManifest previousManifest = ValidateTarget(ResourceManifest.FromJson(File.ReadAllText(ManifestPath(candidate.Previous))), candidate.Previous);
                    ValidateRequiredBundles(previousManifest, candidate.PreviousRequiredBundles);
                }
                value = candidate;
                return true;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException || error is InvalidOperationException) { return false; }
        }

        private static BundleInfo[] ValidateRequiredBundles(ResourceManifest manifest, string[] names)
        {
            BundleInfo[] result = null;
            ResourceManifestWork.Drain(RequiredBundleSteps(manifest, names, value => result = value));
            return result;
        }

        private async ResourceOperationBase<ResourceManifest> ReadBuiltInManifestAsync(CancellationToken cancellationToken)
        {
            // 与下载工厂一致：空根表示没有首包，不读取当前目录下的同名文件。
            if (m_builtInRoot.Length == 0) {
                return null;
            }

            try {
                if (m_builtInManifestFileName == "manifest.zrmb") {
                    return await ResourceManifestBinary.LoadAsync(ResourcePath.Combine(m_builtInRoot, m_builtInManifestFileName), cancellationToken, m_options.NetworkPolicy);
                }
                var json = await ResourceFileReader.ReadTextAsync(ResourcePath.Combine(m_builtInRoot, m_builtInManifestFileName), cancellationToken, m_options.NetworkPolicy);
                return await ResourceManifest.FromJsonAsync(json, m_manifestKeys, m_manifestCodec, cancellationToken);
            }
            catch (IOException) { return null; }
        }

        private ResourceManifest ValidateTarget(ResourceManifest target, string version = null)
        {
            if (target == null) {
                throw new ArgumentNullException(nameof(target));
            }

            ResourceManifest copy = target.Clone();
            if (PackageName != null) {
                ResourcePackageIdentity.ValidateIdentity(copy, PackageName, version);
            }
            else if (copy.FormatVersion >= 3) {
                throw new InvalidDataException("V3 清单需要绑定 Package 身份的版本管理器。");
            }

            if (!string.Equals(copy.BuildTarget, m_buildTarget, StringComparison.Ordinal)) {
                throw new InvalidDataException($"目标清单平台与版本目录不一致：{copy.BuildTarget} != {m_buildTarget}");
            }

            foreach (BundleInfo bundle in copy.Bundles) {
                BundleDownloadQueue.ValidateInfo(bundle);
            }

            return copy;
        }

        private BundleDownloadOptions OptionsFor(string version)
        {
            return m_options.ForVersion(version);
        }

        private string ManifestPath(string version)
        {
            ValidateVersion(version);
            return DownloadStorage.ValidatePath(m_options.CacheRoot, Path.Combine(m_versionRoot, version, "manifest.json"));
        }
        private string PointerPath()
        {
            return DownloadStorage.ValidatePath(m_options.CacheRoot,
            Path.Combine(m_options.CacheRoot, "ZRAssetVersions", m_buildTarget, "active.json"));
        }

        private string BackupPath()
        {
            return DownloadStorage.ValidatePath(m_options.CacheRoot,
                    Path.Combine(m_options.CacheRoot, "ZRAssetVersions", m_buildTarget, "active.backup.json"));
        }

        internal static void WriteAtomic(string destination, string contents, string backup = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var incoming = destination + ".incoming";
            DownloadStorage.RejectLinks(incoming);
            DownloadStorage.DeleteFile(incoming);
            try {
                using (var stream = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024, true)) { writer.Write(contents); writer.Flush(); }
                    stream.Flush(true);
                }
                if (File.Exists(destination)) {
#if UNITY_WEBGL && !UNITY_EDITOR
                    if (backup != null) {
                        File.Copy(destination, backup, true);
                    }

                    File.Delete(destination); File.Move(incoming, destination);
#else
                    File.Replace(incoming, destination, backup);
#endif
                }
                else {
                    File.Move(incoming, destination);
                }
            }
            finally { DownloadStorage.DeleteFile(incoming); }
        }

        private static string Hash(string text)
        {
            using (var sha = SHA256.Create()) {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
            }
        }
        public static string ComputeTextSha256(string text)
        {
            return text == null ? throw new ArgumentNullException(nameof(text)) : Hash(text);
        }
        private static bool SameContent(BundleInfo left, BundleInfo right)
        {
            return left.FileType == right.FileType && left.Size == right.Size &&
            (left.Encryption ?? "") == (right.Encryption ?? "") && (left.EncryptionKeyId ?? "") == (right.EncryptionKeyId ?? "") &&
            left.ContentSize == right.ContentSize && string.Equals(left.ContentSha256, right.ContentSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateVersion(string version)
        {
            if (!DownloadStorage.IsSafeSegment(version)) {
                throw new ArgumentException("版本号必须是安全的单层目录名。", nameof(version));
            }
        }
        private void OnDownloadProgress(BundleDownloadProgress progress)
        {
            DownloadProgressChanged?.Invoke(progress);
        }

        private void Enter(bool recoverPointer = true)
        {
            if (m_busy) {
                throw new InvalidOperationException("已有版本操作正在进行。");
            }

            m_cacheUsageLease = CacheUsageLease.Acquire(m_options.CacheRoot);
            try {
                m_updateLock = AcquireLock();
                if (recoverPointer) {
                    RecoverPointer();
                }

                m_busy = true;
            }
            catch {
                m_updateLock?.Dispose();
                m_updateLock = null;
                m_cacheUsageLease.Dispose();
                m_cacheUsageLease = null;
                throw;
            }
        }
        private ResourceFileLock AcquireLock()
        {
            var lockPath = DownloadStorage.ValidatePath(m_options.CacheRoot,
                Path.Combine(m_options.CacheRoot, "ZRAssetVersions", m_buildTarget, "update.lock"));
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath));
            // 文件共享锁跨 ResourceVersionManager 实例和进程生效；进程退出后由系统释放。
            return ResourceFileLock.Open(lockPath);
        }
        private void Exit()
        {
            m_busy = false;
            m_updateLock?.Dispose();
            m_updateLock = null;
            m_cacheUsageLease?.Dispose();
            m_cacheUsageLease = null;
        }
        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != m_threadId) {
                throw new InvalidOperationException("ResourceVersionManager 必须在创建它的 Unity 主线程使用。");
            }

            if (m_packageOwnerDisposed) {
                throw new ObjectDisposedException("Package " + PackageName);
            }
        }

        internal void InvalidatePackageOwner()
        {
            CheckThread();
            if (m_busy) {
                throw new InvalidOperationException("Package 版本操作尚未结束。");
            }

            m_packageOwnerDisposed = true;
        }
    }
}

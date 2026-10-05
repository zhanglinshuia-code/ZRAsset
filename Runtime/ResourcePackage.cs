using System;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZRAsset
{
    /// <summary>
    /// 一个命名 Package 拥有一个资源管理器和可选版本管理器。准备下载可与旧版使用并行；
    /// 切换/销毁前调用方必须交回该包所有 Handle。其他包不受影响。
    /// </summary>
    public sealed partial class ResourcePackage
    {
        // 保留已有二进制的加载入口；新重载通过 priority 参数选择实际等待顺序。
        public RawFileHandle LoadRawFileAsync(string address, CancellationToken cancellationToken) { return Resources.LoadRawFileAsync(address, cancellationToken); }
        public AssetHandle<T> LoadAssetAsync<T>(string address, CancellationToken cancellationToken) where T : UnityEngine.Object { return Resources.LoadAssetAsync<T>(address, cancellationToken); }
        public AssetHandle<UnityEngine.Object> LoadAssetAsync(string address, Type type, CancellationToken cancellationToken) { return Resources.LoadAssetAsync(address, type, cancellationToken); }
        public AssetCollectionHandle<UnityEngine.Object> LoadSubAssetsAsync(string address, Type type, CancellationToken cancellationToken) { return Resources.LoadSubAssetsAsync(address, type, cancellationToken); }
        public AssetCollectionHandle<UnityEngine.Object> LoadAllAssetsAsync(string address, Type type, CancellationToken cancellationToken) { return Resources.LoadAllAssetsAsync(address, type, cancellationToken); }
        public AssetCollectionHandle<T> LoadSubAssetsAsync<T>(string address, CancellationToken cancellationToken) where T : UnityEngine.Object { return Resources.LoadSubAssetsAsync<T>(address, cancellationToken); }
        public AssetCollectionHandle<T> LoadAllAssetsAsync<T>(string address, CancellationToken cancellationToken) where T : UnityEngine.Object { return Resources.LoadAllAssetsAsync<T>(address, cancellationToken); }
        public InstanceHandle InstantiateAsync(string address, Transform parent, bool worldPositionStays,
            CancellationToken cancellationToken)
        { return Resources.InstantiateAsync(address, parent, worldPositionStays, cancellationToken); }
        public SceneHandle LoadSceneAsync(string address, LoadSceneMode mode,
            CancellationToken cancellationToken)
        { return Resources.LoadSceneAsync(address, mode, cancellationToken); }
        private ResourceManager m_resources;
        private ResourceVersionManager m_versions;
        private ResourceManifest m_manifest;
        private bool m_busy;
#if UNITY_EDITOR
        internal bool EditorPlaySession { get; } = Application.isPlaying;
        private bool m_endingEditorSession;
        private readonly CancellationTokenSource m_editorSessionCancellation = new();
        internal async ResourceOperationBase ShutdownEditorSessionAsync()
        {
            m_endingEditorSession = true;
            m_editorSessionCancellation.Cancel();
            AbortOperations();
            while (IsBusy) {
                await ResourceOperationBase.Yield();
            }

            await DisposeCoreAsync(true, true);
            m_editorSessionCancellation.Dispose();
        }
#endif
        private double m_unloadDelay = 5;
        private ResourceRetryPolicy m_retryPolicy;
        private ResourceLoadOptions m_loadOptions;
        private IResourceDecryptionServices m_decryptionServices;
        public string Name { get; }
        public bool IsInitialized { get { return m_resources != null && !IsDisposed; } }
        public bool IsDisposed { get; private set; }
        public bool IsBusy { get { return !IsDisposed && (m_busy || (m_versions?.IsBusy ?? false)); } }
        public string LoadedVersion { get { return m_resources?.PackageVersion; } }
        public string CacheRoot { get { return m_versions?.CacheRoot; } }
        public ResourceManifest Manifest
        {
            get { CheckAvailable(); return m_manifest?.CopyUnchecked(); }
        }
        public ResourceOperationBase<ResourceManifest> GetManifestAsync(CancellationToken cancellationToken = default)
        { CheckAvailable(); return m_manifest == null ? ResourceOperationBase.FromResult<ResourceManifest>(null) : m_manifest.CopySnapshotAsync(cancellationToken); }
        /// <summary>访问现有高级资源 API；管理器由 Package 负责关闭，调用方只释放使用凭证。</summary>
        public ResourceManager Resources { get { CheckAvailable(); return m_resources ?? throw new InvalidOperationException("Package 尚未加载资源清单。"); } }
        /// <summary>版本检查、预览、准备和签名 API。激活/回滚推荐使用 Package 包装方法。</summary>
        public ResourceVersionManager Versions { get { CheckAvailable(); return m_versions ?? throw new InvalidOperationException("尚未配置 Package 更新。"); } }

        internal ResourcePackage(string name) { Name = name; }

        public ResourceOperationBase InitializeAsync(string root = null, double unloadDelaySeconds = 5,
            ResourceRetryPolicy retryPolicy = null, ResourceLoadOptions loadOptions = null, IResourceDecryptionServices decryptionServices = null,
            bool binaryManifest = false, bool encodedManifest = false, IResourceKeyProvider manifestKeys = null, IResourceManifestCodec manifestCodec = null, ResourceDownloadPolicy networkPolicy = null)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(this);
            return InitializeAsyncScheduled(root, unloadDelaySeconds, retryPolicy, loadOptions, decryptionServices, binaryManifest, encodedManifest, manifestKeys, manifestCodec, networkPolicy: networkPolicy);
        }

        public ResourceOperationBase InitializeAsync(string root, CancellationToken cancellationToken, double unloadDelaySeconds = 5,
            ResourceRetryPolicy retryPolicy = null, ResourceLoadOptions loadOptions = null, IResourceDecryptionServices decryptionServices = null,
            bool binaryManifest = false, bool encodedManifest = false, IResourceKeyProvider manifestKeys = null, IResourceManifestCodec manifestCodec = null, ResourceDownloadPolicy networkPolicy = null)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(this);
            return InitializeAsyncScheduled(root, unloadDelaySeconds, retryPolicy, loadOptions, decryptionServices, binaryManifest, encodedManifest, manifestKeys, manifestCodec, cancellationToken, networkPolicy);
        }

        private async ResourceOperationBase InitializeAsyncScheduled(string root = null, double unloadDelaySeconds = 5,
            ResourceRetryPolicy retryPolicy = null, ResourceLoadOptions loadOptions = null, IResourceDecryptionServices decryptionServices = null,
            bool binaryManifest = false, bool encodedManifest = false, IResourceKeyProvider manifestKeys = null, IResourceManifestCodec manifestCodec = null,
            CancellationToken cancellationToken = default, ResourceDownloadPolicy networkPolicy = null)
        {
            Begin();
#if UNITY_EDITOR
            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, m_editorSessionCancellation.Token);
            cancellationToken = sessionCancellation.Token;
#endif
            try {
                if (m_resources != null || m_versions != null) {
                    throw new InvalidOperationException("Package 已初始化或配置更新。");
                }
                root ??= ResourcePath.Combine(Application.streamingAssetsPath, "ZRAsset/Packages/" + Name);
                if (Application.platform == RuntimePlatform.WebGLPlayer) {
                    await ResourcePersistence.InitializeAsync();
                }
                if (binaryManifest && encodedManifest) {
                    throw new ArgumentException("只能选择一种清单格式。");
                }
                ResourceManifest snapshot = binaryManifest ? await ResourceManifestBinary.LoadAsync(ResourcePath.Combine(root, "manifest.zrmb"), cancellationToken, networkPolicy) :
                                    await ResourceManifest.FromJsonAsync(await ResourceFileReader.ReadTextAsync(ResourcePath.Combine(root, encodedManifest ? "manifest.zrme" : "manifest.json"), cancellationToken, networkPolicy), manifestKeys, manifestCodec, cancellationToken);
                ResourcePackageIdentity.ValidateIdentity(snapshot, Name);
                ResourcePlatform.ValidateBuildTarget(snapshot.BuildTarget);
                ResourceManifestPreparation prepared = await ResourceManifestPreparation.TakeOwnershipAsync(snapshot, loadOptions?.LocationMatching ?? ResourceLocationMatch.ExactAddress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var files = new BuiltInResourceFileSystem(root, prepared, networkPolicy: networkPolicy);
                try { SetResources(new ResourceManager(prepared, BundleLoader.FromFileSystem(files, new UnityResourceLoader(decryptionServices, networkPolicy, root), decryptionServices: decryptionServices), unloadDelaySeconds, retryPolicy, loadOptions: loadOptions), snapshot); }
                catch { await files.DisposeAsync(); throw; }
            }
            finally { m_busy = false; }
        }

        /// <summary>注入 Editor 模拟/测试后端，仍强制命名包身份和生命周期检查。</summary>
        public void Initialize(ResourceManifest manifest, IResourceBackend backend, double unloadDelaySeconds = 5,
            ResourceRetryPolicy retryPolicy = null, ISceneBackend sceneBackend = null, ResourceLoadOptions loadOptions = null)
        {
            CheckAvailable();
            if (m_resources != null || m_versions != null) {
                throw new InvalidOperationException("Package 已初始化或配置更新。");
            }
            ResourcePackageIdentity.ValidateIdentity(manifest, Name);
            var manager = new ResourceManager(manifest, backend, unloadDelaySeconds, retryPolicy, sceneBackend, loadOptions);
            SetResources(manager, manager.OwnedManifest);
        }

        /// <summary>注入只读/缓存/自定义文件系统。成功后 Package 接管其关闭职责；失败时仍由调用方关闭。</summary>
        public void InitializeFileSystem(ResourceManifest manifest, IResourceFileSystem fileSystem, double unloadDelaySeconds = 5,
            ResourceRetryPolicy retryPolicy = null, IUnityResourceLoader objectLoader = null, ISceneBackend sceneBackend = null,
            ResourceLoadOptions loadOptions = null, IResourceDecryptionServices decryptionServices = null)
        {
            CheckAvailable();
            ResourcePackageIdentity.ValidateIdentity(manifest, Name);
            ResourcePlatform.ValidateBuildTarget(manifest.BuildTarget);
            Initialize(manifest, BundleLoader.FromFileSystem(fileSystem, objectLoader, decryptionServices: decryptionServices), unloadDelaySeconds, retryPolicy, sceneBackend, loadOptions);
        }

        /// <summary>
        /// options.CacheRoot 是公共父目录，内部自动追加 ZRAssetPackages/包名。
        /// remoteBaseUrl 和 builtInRoot 已指向该包的文件目录，不自动改变服务端 URL。
        /// </summary>
        public void ConfigureUpdates(string buildTarget, BundleDownloadOptions options, string builtInRoot = null,
            double unloadDelaySeconds = 5, ResourceRetryPolicy retryPolicy = null, ResourceLoadOptions loadOptions = null,
            IResourceDecryptionServices decryptionServices = null, IResourceKeyProvider manifestKeys = null, IResourceManifestCodec manifestCodec = null)
        {
            ConfigureUpdates(buildTarget, options, new ResourceInitializationOptions
            {
                Root = builtInRoot,
                UnloadDelaySeconds = unloadDelaySeconds,
                RetryPolicy = retryPolicy,
                LoadOptions = loadOptions,
                DecryptionServices = decryptionServices,
                ManifestKeys = manifestKeys,
                ManifestCodec = manifestCodec
            });
        }

        /// <summary>打开磁盘上已激活的版本；可用于重启恢复，或显式 Versions.ActivateAsync 后刷新。</summary>
        public ResourceOperationBase LoadActiveAsync() { return ChangeActiveAsync(null, false, false, CancellationToken.None); }
        public ResourceOperationBase ActivateAsync(string version, CancellationToken cancellationToken = default) { return ChangeActiveAsync(version, true, false, cancellationToken); }
        public ResourceOperationBase ActivateSelectionAsync(string version, ResourceSelection selection, CancellationToken cancellationToken = default)
        {
            return selection == null
                ? throw new ArgumentNullException(nameof(selection))
                : ChangeActiveAsync(version, true, false, cancellationToken, selection);
        }
        public ResourceOperationBase RollbackAsync(CancellationToken cancellationToken = default) { return ChangeActiveAsync(null, true, true, cancellationToken); }
        private ResourceOperationBase ChangeActiveAsync(string version, bool changePointer, bool rollback, CancellationToken token,
            ResourceSelection selection = null)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(this);
            return ChangeActiveAsyncScheduled(version, changePointer, rollback, token, selection);
        }

        private async ResourceOperationBase ChangeActiveAsyncScheduled(string version, bool changePointer, bool rollback, CancellationToken token,
            ResourceSelection selection = null)
        {
            Begin();
            try {
                if (m_versions == null) {
                    throw new InvalidOperationException("尚未配置 Package 更新。");
                }
                if (m_versions.IsBusy) {
                    throw new InvalidOperationException("Package 版本操作尚未结束。");
                }
                token.ThrowIfCancellationRequested();
                // 提前拒绝仍有消费者的切换；真正关闭前会再检查，覆盖异步准备期间的新请求。
                m_resources?.EnsureCanDispose();
                var oldClosed = false;
                async ResourceOperationBase ClosePrevious()
                {
                    if (m_resources == null) { return; }
                    ResourceOperationBase closing = m_resources.DisposeAsync();
                    oldClosed = true;
                    try { await closing; }
                    finally { m_resources = null; m_manifest = null; }
                }
                try {
                    if (changePointer) {
                        ResourceManager next = await m_versions.ActivatePackageAsync(version, rollback, selection,
                            m_unloadDelay, m_retryPolicy, m_loadOptions, m_decryptionServices, ClosePrevious, token);
                        SetResources(next, next.OwnedManifest);
                    }
                    else {
                        ResourceManager next = await m_versions.CreateActiveManagerAsync(m_unloadDelay, m_retryPolicy,
                            m_loadOptions, m_decryptionServices, token);
                        try { await ClosePrevious(); }
                        catch { await next.DisposeAsync(); throw; }
                        SetResources(next, next.OwnedManifest);
                    }
                }
                catch {
                    // 激活失败保留旧磁盘指针，尽力重新打开旧清单。原始错误始终返回给调用方。
                    try { if (oldClosed && m_versions.ActiveVersion != null) { await OpenActiveAsync(); } }
                    catch (Exception restoreError) { Debug.LogWarning("Package 旧版本重开失败：" + restoreError.Message); }
                    throw;
                }
            }
            finally { m_busy = false; }
        }

        private async ResourceOperationBase OpenActiveAsync()
        {
            ResourceManager manager = await m_versions.CreateActiveManagerAsync(m_unloadDelay, m_retryPolicy, m_loadOptions, m_decryptionServices);
            // 使用实际创建的管理器快照，不能在 await 之后重新读取可能已改变的活动指针。
            SetResources(manager, manager.OwnedManifest);
        }

        private void SetResources(ResourceManager manager, ResourceManifest snapshot)
        {
            manager.SchedulingPackage = this;
            m_resources = manager; m_manifest = snapshot;
            manager.DiagnosticName = Name + "@" + manager.PackageVersion;
        }

        public ResourceScope CreateScope(string name, bool captureCreationStacks = false, CancellationToken cancellationToken = default)
        {
            return Resources.CreateScope(name, captureCreationStacks, cancellationToken);
        }

        public ResourceCatalog Catalog { get { return Resources.Catalog; } }
        public ResourceOperationBase<ResourceCatalog> WarmupCatalogAsync(CancellationToken cancellationToken = default)
        { return Resources.WarmupCatalogAsync(cancellationToken); }
        public ResourceOperationBase<ResourceSelectionResult> SelectAsync(ResourceSelection selection, CancellationToken cancellationToken = default)
        { return Resources.SelectAsync(selection, cancellationToken); }
        public RawFileHandle LoadRawFileAsync(string address, CancellationToken cancellationToken = default, int priority = 0) { return Resources.LoadRawFileAsync(address, cancellationToken, priority); }
        public RawFileHandle LoadRawFileSync(string address) { return Resources.LoadRawFileSync(address); }
        public ResourceAssetInfo GetAssetInfo(string address) { return Resources.GetAssetInfo(address); }
        public ResourceAssetInfo GetAssetInfoByGuid(string guid) { return Resources.GetAssetInfoByGuid(guid); }
        public ResourceSelectionResult Select(ResourceSelection selection) { return Resources.Select(selection); }
        public ResourceOperationBase<ResourcePreparationPlan> PlanSelectionAsync(ResourceSelection selection, CancellationToken cancellationToken = default) { return Resources.PlanSelectionAsync(selection, cancellationToken); }
        public ResourceOperationBase PrepareSelectionAsync(ResourceSelection selection, DownloadPriority priority = DownloadPriority.Normal,
            CancellationToken cancellationToken = default)
        { return Resources.PrepareSelectionAsync(selection, priority, cancellationToken); }
        public ResourceDownloader CreateDownloader(ResourceSelection selection, int maxConcurrentFiles = 3,
            DownloadPriority priority = DownloadPriority.Normal)
        { return Resources.CreateDownloader(selection, maxConcurrentFiles, priority); }
        public ResourceImporter CreateImporter(System.Collections.Generic.IEnumerable<ResourceImportFile> files, int maxConcurrentFiles = 3) { return Resources.CreateImporter(files, maxConcurrentFiles); }
        public ResourceOperationBase<ResourceBundleFile> EnsureBundleFileAsync(string address, DownloadPriority priority = DownloadPriority.Normal,
            CancellationToken cancellationToken = default)
        { return Resources.EnsureBundleFileAsync(address, priority, cancellationToken); }
        public AssetHandle<T> LoadAssetAsync<T>(string address, CancellationToken cancellationToken = default, int priority = 0) where T : UnityEngine.Object { return Resources.LoadAssetAsync<T>(address, cancellationToken, priority); }
        public AssetHandle<T> LoadAssetSync<T>(string address) where T : UnityEngine.Object { return Resources.LoadAssetSync<T>(address); }
        public AssetHandle<UnityEngine.Object> LoadAssetAsync(string address, Type type, CancellationToken cancellationToken = default, int priority = 0) { return Resources.LoadAssetAsync(address, type, cancellationToken, priority); }
        public AssetHandle<UnityEngine.Object> LoadAssetSync(string address, Type type) { return Resources.LoadAssetSync(address, type); }
        public AssetCollectionHandle<UnityEngine.Object> LoadSubAssetsAsync(string address, Type type, CancellationToken cancellationToken = default, int priority = 0) { return Resources.LoadSubAssetsAsync(address, type, cancellationToken, priority); }
        public AssetCollectionHandle<UnityEngine.Object> LoadSubAssetsSync(string address, Type type) { return Resources.LoadSubAssetsSync(address, type); }
        public AssetCollectionHandle<UnityEngine.Object> LoadAllAssetsAsync(string address, Type type, CancellationToken cancellationToken = default, int priority = 0) { return Resources.LoadAllAssetsAsync(address, type, cancellationToken, priority); }
        public AssetCollectionHandle<UnityEngine.Object> LoadAllAssetsSync(string address, Type type) { return Resources.LoadAllAssetsSync(address, type); }
        public AssetCollectionHandle<T> LoadSubAssetsAsync<T>(string address, CancellationToken cancellationToken = default, int priority = 0) where T : UnityEngine.Object { return Resources.LoadSubAssetsAsync<T>(address, cancellationToken, priority); }
        public AssetCollectionHandle<T> LoadSubAssetsSync<T>(string address) where T : UnityEngine.Object { return Resources.LoadSubAssetsSync<T>(address); }
        public AssetCollectionHandle<T> LoadAllAssetsAsync<T>(string address, CancellationToken cancellationToken = default, int priority = 0) where T : UnityEngine.Object { return Resources.LoadAllAssetsAsync<T>(address, cancellationToken, priority); }
        public AssetCollectionHandle<T> LoadAllAssetsSync<T>(string address) where T : UnityEngine.Object { return Resources.LoadAllAssetsSync<T>(address); }
        public InstanceHandle InstantiateAsync(string address, Transform parent = null, bool worldPositionStays = false,
            CancellationToken cancellationToken = default, int priority = 0)
        { return Resources.InstantiateAsync(address, parent, worldPositionStays, cancellationToken, priority); }
        public SceneHandle LoadSceneAsync(string address, LoadSceneMode mode = LoadSceneMode.Additive,
            CancellationToken cancellationToken = default, int priority = 0)
        { return Resources.LoadSceneAsync(address, mode, cancellationToken, priority); }
        public SceneHandle LoadSceneAsync(string address, ResourceSceneLoadOptions options, CancellationToken cancellationToken = default) { return Resources.LoadSceneAsync(address, options, cancellationToken); }
        public void UnloadUnused(bool force = false) { Resources.UnloadUnused(force); }
        public bool TryUnloadUnusedAsset(string address) { return Resources.TryUnloadUnusedAsset(address); }
        /// <summary>撤销本包资源凭证并卸载全部资源，保留包注册、当前版本与文件系统；完成后可以重新加载。</summary>
        public ResourceOperationBase UnloadAllAssetsAsync()
        {
            return UnloadAllAssetsAsync(false);
        }

        public ResourceOperationBase UnloadAllAssetsAsync(bool waitForEngine)
        {
            Begin();
            try {
                if (m_versions?.IsBusy == true) {
                    throw new InvalidOperationException("Package 版本操作尚未结束。");
                }
                ResourceOperationBase operation = m_resources?.UnloadAllAssetsAsync(waitForEngine) ?? ResourceOperationBase.CompletedOperation;
                return OperationSystem.Start(new PackageUnloadOperation(this, operation) { SchedulingPackage = this });
            }
            catch { m_busy = false; throw; }
        }

        private sealed class PackageUnloadOperation: ResourceOperationBase
        {
            private readonly ResourcePackage m_package;
            private readonly ResourceOperationBase m_child;
            internal PackageUnloadOperation(ResourcePackage package, ResourceOperationBase child)
            { m_package = package; m_child = child; }
            protected override void OnUpdate()
            {
                Progress = m_child.Progress;
                if (!m_child.IsDone) {
                    return;
                }

                m_child.GetAwaiter().GetResult();
                Succeed();
            }
            // Clear the barrier before observers can see terminal status, including
            // callers polling IsDone instead of awaiting the deferred callbacks.
            protected override void OnCleanup() { m_package.m_busy = false; }
        }

        public ResourceOperationBase DisposeAsync()
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(this);
            return DisposeCoreAsync(false);
        }

        /// <summary>显式撤销本包所有资源凭证；业务必须停止使用资源，并等待此操作完成。</summary>
        public ResourceOperationBase ForceDisposeAsync()
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(this);
            return DisposeCoreAsync(true);
        }

        private async ResourceOperationBase DisposeCoreAsync(bool force, bool editorShutdown = false)
        {
            ResourcePackages.CheckThread();
            if (IsDisposed) {
                return;
            }
            if (editorShutdown) {
                m_busy = true;
            }
            else {
                Begin();
            }

            try {
                if (m_versions?.IsBusy == true) {
                    throw new InvalidOperationException("Package 版本操作尚未结束。");
                }
                if (!force) { m_resources?.EnsureCanDispose(); }
                AbortOperations();
                if (m_resources != null) {
#if UNITY_EDITOR
                    if (editorShutdown) {
                        await m_resources.ShutdownEditorSessionAsync();
                    }
                    else {
                        await (force ? m_resources.ForceDisposeAsync() : m_resources.DisposeAsync());
                    }
#else
                    await (force ? m_resources.ForceDisposeAsync() : m_resources.DisposeAsync());
#endif
                }
                m_versions?.InvalidatePackageOwner();
                m_resources = null; m_manifest = null; IsDisposed = true;
                ResourcePackages.Remove(this);
            }
            finally { m_busy = false; }
        }

        private void Begin() { CheckAvailable(); m_busy = true; }
        private void CheckAvailable()
        {
            ResourcePackages.CheckThread();
            if (IsDisposed) {
                throw new ObjectDisposedException("Package " + Name);
            }
#if UNITY_EDITOR
            if (m_endingEditorSession) {
                throw new ObjectDisposedException("Package " + Name + " (Play Mode ended)");
            }
#endif
            if (m_busy) {
                throw new InvalidOperationException("Package 生命周期操作尚未结束。");
            }
        }
    }
}

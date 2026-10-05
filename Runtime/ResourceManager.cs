using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Unity.Profiling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZRAsset
{
    /// <summary>一个 Bundle 的共享加载任务；引用数来自资源 Provider 或场景，而不是直接来自所有业务 Handle。</summary>
    internal sealed class BundleProvider
    {
        public BundleInfo Info;
        internal ResourceLoadPriority LoadPriority;
        public int References;
        internal OperationCompletionSource<object> Completion;
        internal bool IssuingRequest;
        internal ResourceOperationBase<object> Request;
        public ResourceOperationBase<object> Operation;
    }

    /// <summary>一个路径/类型对应一个 Provider；多个业务 Handle 共用任务和依赖集合。</summary>
    internal sealed class AssetProvider
    {
        internal bool Revoked;
        internal ResourceLoadPriority LoadPriority;
        public (string, Type, ResourceAssetLoadKind) Key;
        public string OwningBundleName;
        public BundleProvider[] Bundles;
        public int References;
        public double ReleasedAt;
        public ProviderState State;
        internal OperationCompletionSource<object> Completion;
        internal bool IssuingRequest;
        internal ResourceOperationBase<object> Request;
        public ResourceOperationBase<object> Operation;
    }

    /// <summary>诊断快照；Handles 包含实例内部持有的资源凭证，场景凭证单独统计。</summary>
    public readonly struct ResourceStats
    {
        public readonly int Assets, Bundles, Handles, LoadingAssets;
        public readonly int Instances, Scenes;
        public readonly int RawFiles, RawReferences;
        public ResourceStats(int assets, int bundles, int handles, int loadingAssets, int instances = 0, int scenes = 0, int rawFiles = 0, int rawReferences = 0)
        { Assets = assets; Bundles = bundles; Handles = handles; LoadingAssets = loadingAssets; Instances = instances; Scenes = scenes; RawFiles = rawFiles; RawReferences = rawReferences; }
        public override string ToString() { return $"Assets: {Assets}, Bundles: {Bundles}, Asset handles: {Handles}, Loading assets: {LoadingAssets}, Instances: {Instances}, Scenes: {Scenes}, Raw files: {RawFiles}, Raw references: {RawReferences}"; }
    }

    /// <summary>资源管理入口，仅在 Unity 主线程使用；同一组 Bundle 应由一个管理器统一管理。</summary>
    public sealed partial class ResourceManager
    {
        internal ResourcePackage SchedulingPackage { get; set; }
        public string PackageName { get; }
        public string PackageVersion { get; }

        public ResourceManifest Manifest { get { CheckThread(); return OwnedManifest.CopyUnchecked(); } }
        /// <summary>返回独立快照；大型清单可用此入口避免同步复制 DTO。</summary>
        public ResourceOperationBase<ResourceManifest> GetManifestAsync(CancellationToken cancellationToken = default)
        { CheckThread(); return OwnedManifest.CopySnapshotAsync(cancellationToken); }
        internal ResourceManifest OwnedManifest { get; }
#if UNITY_EDITOR
        internal bool EditorPlaySession { get; } = Application.isPlaying;
        /// <summary>仅供 Editor 工具注册模拟工厂；Player 编译时此入口不存在。</summary>
        public static Func<double, ResourceRetryPolicy, ResourceManager> EditorSimulationFactory { get; set; }
#endif
        private readonly Dictionary<string, AssetInfo> m_assets;
        private readonly Dictionary<string, BundleInfo> m_catalog;
        private readonly ResourceDependencyGraph m_dependencyGraph;
        private readonly ResourceProviderRetention m_providerRetention;
        private readonly ResourceLocationMatch m_locationMatching;
        private readonly Dictionary<string, AssetInfo> m_locations;
        private readonly Dictionary<(string, Type, ResourceAssetLoadKind), AssetProvider> m_assetCache = new();
        private readonly Dictionary<string, BundleProvider> m_bundleCache = new(StringComparer.Ordinal);
        private readonly IResourceBackend m_backend;
        private readonly int m_threadId;
        private readonly double m_unloadDelay;
        private readonly ResourceRetryPolicy m_retryPolicy;
        private readonly IDisposable m_cacheUsageLease;
        private readonly HashSet<ResourceOperationBase> m_pendingDownloads = new();
        private ResourceOperationLimiter m_bundleLoads, m_assetLoads;
        private readonly List<AssetProvider> m_expiredAssets = new();
        private readonly List<BundleProvider> m_expiredBundles = new();
        private bool m_collecting;
        private static readonly ProfilerMarker s_collectMarker = new("ZRAsset.UnloadUnused");
        private static readonly ProfilerMarker s_statsMarker = new("ZRAsset.GetStats");
        private bool m_closing;
        private ResourceOperationBase m_disposeTask;

        public ResourceManager(ResourceManifest manifest, IResourceBackend backend, double unloadDelaySeconds = 5,
            ResourceRetryPolicy retryPolicy = null, ISceneBackend sceneBackend = null, ResourceLoadOptions loadOptions = null)
            : this(ResourceManifestPreparation.Copy(manifest, loadOptions?.LocationMatching ?? ResourceLocationMatch.ExactAddress),
                backend, unloadDelaySeconds, retryPolicy, sceneBackend, loadOptions)
        { }

        internal ResourceManager(ResourceManifestPreparation prepared, IResourceBackend backend, double unloadDelaySeconds = 5,
            ResourceRetryPolicy retryPolicy = null, ISceneBackend sceneBackend = null, ResourceLoadOptions loadOptions = null)
        {
            m_backend = backend ?? throw new ArgumentNullException(nameof(backend));
            if (double.IsNaN(unloadDelaySeconds) || double.IsInfinity(unloadDelaySeconds) || unloadDelaySeconds < 0) {
                throw new ArgumentOutOfRangeException(nameof(unloadDelaySeconds));
            }            // 复制清单快照，避免调用方后续修改 DTO 破坏正在运行的依赖关系。
            ResourceManifest copy = prepared.Manifest;
            OwnedManifest = copy;
            PackageName = copy.PackageName;
            PackageVersion = copy.PackageVersion;
            m_locationMatching = prepared.Matching;
            m_assets = prepared.Assets;
            m_locations = prepared.Locations;
            m_catalog = prepared.Bundles;
            m_dependencyGraph = new ResourceDependencyGraph(copy, m_catalog);
            if (copy.FormatVersion >= 6) {
                m_providerRetention = new ResourceProviderRetention();
            }
            m_threadId = Thread.CurrentThread.ManagedThreadId;
            m_unloadDelay = unloadDelaySeconds;
            m_retryPolicy = retryPolicy ?? new ResourceRetryPolicy();
            m_sceneBackend = sceneBackend ?? new UnitySceneBackend();
            FileSystem = (backend as IResourceFileBackend)?.FileSystem;
            if (FileSystem is MiniGameWebFileSystem miniGame) { miniGame.ConfigureRetryPolicy(m_retryPolicy); }
            DownloadQueue = (FileSystem as IResourceDownloadSource)?.DownloadQueue;
            ConfigureLoadOptions(loadOptions);
            // 管理器排空之前仍可能读取旧版本文件，缓存清理需持续保护这段生命周期。
            m_cacheUsageLease = FileSystem?.AcquireReadLease();
            RegisterDiagnostics(copy.BuildTarget);
        }

        /// <summary>异步复制、校验及构建索引；完成前不得修改 manifest。失败时后端仍归调用方所有。</summary>
        public static async ResourceOperationBase<ResourceManager> CreateFromManifestAsync(ResourceManifest manifest, IResourceBackend backend,
            double unloadDelaySeconds = 5, ResourceRetryPolicy retryPolicy = null, ISceneBackend sceneBackend = null,
            ResourceLoadOptions loadOptions = null, CancellationToken cancellationToken = default)
        {
            ResourcePackages.CheckThread();
            if (backend == null) {
                throw new ArgumentNullException(nameof(backend));
            }
#if UNITY_EDITOR
            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ResourcePackages.PlaySessionToken);
            cancellationToken = sessionCancellation.Token;
#endif
            ResourceManifestPreparation prepared = await ResourceManifestPreparation.CopyAsync(manifest,
                loadOptions?.LocationMatching ?? ResourceLocationMatch.ExactAddress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new ResourceManager(prepared, backend, unloadDelaySeconds, retryPolicy, sceneBackend, loadOptions);
        }

        /// <summary>从清单创建管理器。默认读取 StreamingAssets；Editor 工作台可切换默认入口到模拟模式。</summary>
        public static async ResourceOperationBase<ResourceManager> CreateAsync(string root = null, double unloadDelaySeconds = 5,
            ResourceRetryPolicy retryPolicy = null, BundleDownloadOptions downloadOptions = null,
            DownloadPriority downloadPriority = DownloadPriority.Normal, ResourceLoadOptions loadOptions = null,
            IResourceDecryptionServices decryptionServices = null, ResourceDownloadPolicy networkPolicy = null)
        {
            CancellationToken token = default;
#if UNITY_EDITOR
            token = ResourcePackages.PlaySessionToken;
            // 显式传入 root 始终读取真实包，避免测试或诊断被全局模拟选项意外接管。
            if (root == null && downloadOptions == null && EditorSimulationFactory != null) {
                ResourceManager simulated = EditorSimulationFactory(unloadDelaySeconds, retryPolicy);
                simulated.ConfigureLoadOptions(loadOptions);
                return simulated;
            }
#endif
            root ??= ResourcePath.Combine(Application.streamingAssetsPath, "ZRAsset");
            if (Application.platform == RuntimePlatform.WebGLPlayer) {
                await ResourcePersistence.InitializeAsync();
            }
            networkPolicy ??= downloadOptions?.NetworkPolicy;
            var json = await ResourceFileReader.ReadTextAsync(ResourcePath.Combine(root, "manifest.json"), token, networkPolicy);
            ResourceManifest manifest = await ResourceManifest.FromJsonAsync(json, cancellationToken: token);
            ResourcePlatform.ValidateBuildTarget(manifest.BuildTarget);
            ResourceManifestPreparation prepared = await ResourceManifestPreparation.TakeOwnershipAsync(manifest, loadOptions?.LocationMatching ?? ResourceLocationMatch.ExactAddress, token);
            token.ThrowIfCancellationRequested();
            IResourceFileSystem files = downloadOptions == null ? new BuiltInResourceFileSystem(root, prepared, networkPolicy: networkPolicy) :
                new CachedResourceFileSystem(new BundleDownloadCache(root, downloadOptions, manifest.BuildTarget, prepared));
            try {
                return new ResourceManager(prepared, BundleLoader.FromFileSystem(files, new UnityResourceLoader(decryptionServices, networkPolicy, root),
                    priority: downloadPriority, decryptionServices: decryptionServices),
                    unloadDelaySeconds, retryPolicy, loadOptions: loadOptions);
            }
            catch (Exception error) {
                // 构造失败时还没有管理器负责关闭队列，必须归还它已取得的缓存租约。
                try { await files.DisposeAsync(); }
                catch (Exception cleanup) { throw ResourceFailure.WithCleanup(error, cleanup); }
                throw;
            }
        }

        /// <summary>
        /// 使用调用方提供的目标清单建立按需下载系统。版本选择和激活由 ResourceVersionManager 负责。
        /// 本地首包目录可只有部分 Bundle；缺失文件从 RemoteBaseUrl 下载到校验后的缓存。
        /// </summary>
        public static ResourceOperationBase<ResourceManager> CreateWithDownloadsAsync(ResourceManifest manifest, BundleDownloadOptions options,
            string builtInRoot = null, double unloadDelaySeconds = 5, ResourceRetryPolicy retryPolicy = null,
            ResourceLoadOptions loadOptions = null, IResourceDecryptionServices decryptionServices = null,
            IResourceKeyProvider manifestKeys = null, IResourceManifestCodec manifestCodec = null,
            bool encodedBuiltInManifest = false, CancellationToken cancellationToken = default)
        {
            return CreateWithDownloadsCoreAsync(manifest, options, builtInRoot, unloadDelaySeconds, retryPolicy,
                loadOptions, decryptionServices, manifestKeys, manifestCodec,
                encodedBuiltInManifest ? "manifest.zrme" : "manifest.json", cancellationToken);
        }

        internal static async ResourceOperationBase<ResourceManager> CreateWithDownloadsCoreAsync(ResourceManifest manifest, BundleDownloadOptions options,
            string builtInRoot, double unloadDelaySeconds, ResourceRetryPolicy retryPolicy, ResourceLoadOptions loadOptions,
            IResourceDecryptionServices decryptionServices, IResourceKeyProvider manifestKeys, IResourceManifestCodec manifestCodec,
            string builtInManifestFileName, CancellationToken token, bool offline = false)
        {
#if UNITY_EDITOR
            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ResourcePackages.PlaySessionToken);
            token = sessionCancellation.Token;
#endif
            if (manifest == null) {
                throw new ArgumentNullException(nameof(manifest));
            }
            if (options == null) {
                throw new ArgumentNullException(nameof(options));
            }
            ResourceManifestPreparation prepared = await ResourceManifestPreparation.CopyAsync(manifest, loadOptions?.LocationMatching ?? ResourceLocationMatch.ExactAddress, token);
            ResourceManifest snapshot = prepared.Manifest;
            ResourcePlatform.ValidateBuildTarget(snapshot.BuildTarget);
            builtInRoot ??= ResourcePath.Combine(Application.streamingAssetsPath, "ZRAsset");
            if (offline && builtInRoot.Length > 0 && ResourcePath.Classify(builtInRoot) == ResourceLocationKind.HttpUri) {
                throw new InvalidOperationException("离线启动的首包目录不能使用 HTTP(S)。");
            }
            ResourceManifestPreparation builtInManifest = null;
            // 空字符串明确禁用首包，不得误读进程工作目录中的 manifest.json。
            if (builtInRoot.Length > 0) {
                try {
                    var location = ResourcePath.Combine(builtInRoot, builtInManifestFileName);
                    ResourceManifest installed = builtInManifestFileName == "manifest.zrmb"
                        ? await ResourceManifestBinary.LoadAsync(location, token, options.NetworkPolicy)
                        : await ResourceManifest.FromJsonAsync(await ResourceFileReader.ReadTextAsync(location, token, options.NetworkPolicy), manifestKeys, manifestCodec, token);
                    builtInManifest = await ResourceManifestPreparation.TakeOwnershipAsync(
                        installed, token: token);
                }
                catch (System.IO.IOException) { /* 无首包清单时仍可从远端准备所有文件。 */ }
            }
            token.ThrowIfCancellationRequested();
            if (offline) {
                options = new BundleDownloadOptions(options.RemoteBaseUrl, options.CacheVersion, options.CacheRoot,
                    options.MaxConcurrentDownloads, options.RequestTimeoutSeconds, new ResourceRetryPolicy(0), options.DiskPolicy, options.NetworkPolicy, options.SourceOptions);
            }
            var cache = new CachedResourceFileSystem(new BundleDownloadCache(builtInRoot, options, snapshot.BuildTarget,
                builtInManifest, offline ? new HotUpdate.HotUpdateBootstrap.OfflineTransport() : null, offline: offline));
            try {
                return new ResourceManager(prepared, BundleLoader.FromFileSystem(cache, new UnityResourceLoader(decryptionServices, options.NetworkPolicy, options.RemoteBaseUrl), decryptionServices: decryptionServices), unloadDelaySeconds, retryPolicy,
                    loadOptions: loadOptions);
            }
            catch (Exception error) {
                try { await cache.DisposeAsync(); }
                catch (Exception cleanup) { throw ResourceFailure.WithCleanup(error, cleanup); }
                throw;
            }
        }

        /// <summary>预下载一组资源/场景的完整依赖闭包，不加载 Unity 对象，也不增加资源 Handle 数。</summary>
        public ResourceOperationBase DownloadDependenciesAsync(IEnumerable<string> addresses, DownloadPriority priority = DownloadPriority.Normal,
            CancellationToken cancellationToken = default)
        { return PrepareDependenciesCore(addresses, priority, cancellationToken, true); }
        /// <summary>通过当前文件系统准备完整依赖闭包；也支持只读本地来源，不打开 Unity 对象。</summary>
        public ResourceOperationBase PrepareDependenciesAsync(IEnumerable<string> addresses, DownloadPriority priority = DownloadPriority.Normal,
            CancellationToken cancellationToken = default)
        { return PrepareDependenciesCore(addresses, priority, cancellationToken, false); }
        private ResourceOperationBase PrepareDependenciesCore(IEnumerable<string> addresses, DownloadPriority priority,
            CancellationToken cancellationToken, bool requireDownloads)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            CheckThread();
            if (m_closing) {
                throw new ObjectDisposedException(nameof(ResourceManager));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (addresses == null) {
                throw new ArgumentNullException(nameof(addresses));
            }
            if (FileSystem == null) {
                throw new InvalidOperationException("当前资源后端未提供文件系统。");
            }
            if (requireDownloads && !FileSystem.SupportsDownloads) {
                throw new InvalidOperationException("请先使用支持下载的文件系统创建资源管理器。");
            }
            var closure = new HashSet<string>(StringComparer.Ordinal);
            // 先验证全部地址，再发出任何网络请求，避免参数错误时已经下载了部分资源。
            foreach (var address in addresses) {
                if (address == null || !m_locations.TryGetValue(address, out AssetInfo info)) {
                    throw new KeyNotFoundException($"Unknown resource address: {address}");
                }
                closure.UnionWith(m_dependencyGraph.GetAssetClosure(info));
            }
            return TrackContentOperation<bool>(async () =>
            {
                await ResourceWorkBatch.RunAsync(closure.ToArray(), 3,
                    (name, token) => PrepareFileAsync(name, priority, token), cancellationToken);
                return true;
            });
        }

        /// <summary>管理器独占的文件服务，仅供能力查询；关闭由管理器负责。未接入文件层的后端返回 null。</summary>
        public IResourceFileSystem FileSystem { get; }
        /// <summary>读取文件系统提供的内置下载队列；自定义下载器或只读来源返回 null。</summary>
        public BundleDownloadQueue DownloadQueue { get; }
        /// <summary>在主线程订阅下载字节数、进度、状态和重试错误。</summary>
        public event Action<BundleDownloadProgress> DownloadProgressChanged
        {
            add { CheckThread(); if (DownloadQueue != null) { DownloadQueue.ProgressChanged += value; } }
            remove { CheckThread(); if (DownloadQueue != null) { DownloadQueue.ProgressChanged -= value; } }
        }

        private void Collect(string name, HashSet<string> closure)
        {
            closure.UnionWith(m_dependencyGraph.GetBundleClosure(name));
        }

        internal void Release(AssetProvider provider)
        {
            if (provider.Revoked) {
                return;
            }
            if (--provider.References == 0) {
                provider.ReleasedAt = Time.realtimeSinceStartupAsDouble;
                m_unusedAssets.Add(provider);
            }
        }

        /// <summary>在 Update 中调用。force 仅跳过回收延迟，不会卸载仍有引用或仍在加载的资源。</summary>
        public void UnloadUnused(bool force = false)
        {
            UnloadUnusedCore(force, null);
        }

        /// <summary>Evict completed, unreferenced providers for one asset; live handles and streams remain valid.</summary>
        public bool TryUnloadUnusedAsset(string address)
        {
            CheckThread();
            return m_closing
                ? throw new ObjectDisposedException(nameof(ResourceManager))
                : address == null || !m_locations.TryGetValue(address, out AssetInfo asset)
                ? throw new KeyNotFoundException("Unknown resource address: " + address)
                : UnloadUnusedCore(true, asset.AssetPath, asset.BundleName);
        }

        private bool UnloadUnusedCore(bool force, string assetPath, string bundleName = null)
        {
            CheckThread();
            if (m_collecting) {
                return false;
            }
            m_collecting = true;
            using ProfilerMarker.AutoScope sample = s_collectMarker.Auto();
            var removed = false;
            try {
                PollLifetimes();
                var now = Time.realtimeSinceStartupAsDouble;
                removed = CollectRawFiles(force, now, assetPath);
                foreach (AssetProvider item in m_unusedAssets) {
                    if ((assetPath == null || item.Key.Item1 == assetPath ||
                                                                                (item.Key.Item3 == ResourceAssetLoadKind.AllAssets && item.OwningBundleName == bundleName)) && item.References == 0 && item.Operation.IsDone &&
                                                                                (force || item.State == ProviderState.Failed || now - item.ReleasedAt >= m_unloadDelay)) {
                        m_expiredAssets.Add(item);
                    }
                }
                m_providerRetention?.FilterCandidates(m_expiredAssets, m_bundleCache);
                foreach (AssetProvider provider in m_expiredAssets) {
                    // 先移除资源缓存，再交回其依赖引用，防止缓存里仍能拿到已卸载资源。
                    m_assetCache.Remove(provider.Key);
                    m_unusedAssets.Remove(provider);
                    removed = true;
                    foreach (BundleProvider bundle in provider.Bundles) {
                        ReleaseBundle(bundle);
                    }
                }
                foreach (BundleProvider item in m_unusedBundles) {
                    if (item.References == 0 && item.Operation.IsDone) {
                        m_expiredBundles.Add(item);
                    }
                }
                foreach (BundleProvider bundle in m_expiredBundles) {
                    // 上一个后端卸载回调可能重新加载本包/依赖；快照不能授权卸载新引用或替换后的 Provider。
                    if (bundle.References != 0 || !m_bundleCache.TryGetValue(bundle.Info.Name, out BundleProvider current) ||
                        !ReferenceEquals(current, bundle)) {
                        continue;
                    }                    // 先断开旧 Provider，后端回调中的新请求才能创建独立的新加载。
                    m_bundleCache.Remove(bundle.Info.Name);
                    m_unusedBundles.Remove(bundle);
                    try {
                        if (bundle.Operation.Status == OperationStatus.Succeeded) {
                            m_backend.UnloadBundle(bundle.Operation.Result);
                        }
                        else {
                            _ = bundle.Operation.Exception;
                        }
                    }
                    catch {
                        // 卸载失败时允许后续重试，但不得覆盖回调中已经建立的新 Provider。
                        if (!m_bundleCache.ContainsKey(bundle.Info.Name)) {
                            m_bundleCache.Add(bundle.Info.Name, bundle);
                            m_unusedBundles.Add(bundle);
                        }
                        throw;
                    }
                }
            }
            finally { m_expiredAssets.Clear(); m_expiredBundles.Clear(); m_collecting = false; }
            return removed;
        }

        public ResourceStats GetStats()
        {
            CheckThread();
            using ProfilerMarker.AutoScope sample = s_statsMarker.Auto();
            PollLifetimes();
            int handles = 0, loading = 0, rawReferences = 0;
            foreach (AssetProvider provider in m_assetCache.Values) { handles += provider.References; if (!provider.Operation.IsDone) { loading++; } }
            foreach (RawFileProvider file in m_rawFiles.Values) {
                rawReferences += file.References;
            }
            return new ResourceStats(m_assetCache.Count, m_bundleCache.Count,
                            handles, loading, m_instances.Count, m_scenes.Count, m_rawFiles.Count, rawReferences);
        }

        /// <summary>观察正在执行和排队的共享加载；计数不随业务 Handle 重复增加。</summary>
        public ResourceLoadStats GetLoadStats()
        {
            CheckThread();
            return new ResourceLoadStats(m_bundleLoads.Active, m_bundleLoads.Queued, m_assetLoads.Active, m_assetLoads.Queued);
        }

        private void ConfigureLoadOptions(ResourceLoadOptions options)
        {
            if (m_assetCache.Count != 0 || m_bundleCache.Count != 0 || m_rawFiles.Count != 0) {
                throw new InvalidOperationException("只能在首次资源请求前配置加载并发数。");
            }
            options ??= new ResourceLoadOptions();
            m_bundleLoads = new ResourceOperationLimiter(options.MaxConcurrentBundleLoads);
            m_assetLoads = new ResourceOperationLimiter(options.MaxConcurrentAssetLoads);
        }

        /// <summary>关闭前需释放全部使用凭证；关闭流程会等已放弃的在途加载结束后再卸载。</summary>
        public ResourceOperationBase DisposeAsync()
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            CheckThread();
            if (m_unloadAll != null && !m_unloadAll.IsDone) {
                throw new InvalidOperationException("请先等待全部资源卸载完成，再关闭管理器。");
            }
            if (m_forceDisposal != null) {
                return m_forceDisposal;
            }
            if (m_disposeTask != null) {
                return m_disposeTask;
            }
            if (m_disposalReservations != 0) { throw new InvalidOperationException("管理器正在参与整组关闭检查。"); }
            using var reservation = new DisposalReservation(this);
            return reservation.Commit();
        }

        internal void EnsureCanDispose()
        {
            CheckThread();
            if (m_scopes != null && m_scopes.Count != 0) {
                throw new InvalidOperationException("关闭资源管理器或切换版本之前必须先关闭全部 ResourceScope。");
            }
            PollLifetimes();
            if (m_assetCache.Values.Any(a => a.References != 0) || m_rawFiles.Values.Any(file => file.References != 0) || m_instances.Count != 0 || m_scenes.Count != 0) {
                throw new InvalidOperationException("Release asset/raw handles and raw streams, and await instance destruction / scene unloading before disposing the resource manager.");
            }
        }

        private async ResourceOperationBase DrainAsync()
        {
            // 已无活跃资源使用者，先停止网络队列，再等待不可取消的 Unity 操作和文件校验收尾。
            Exception shutdownError = null;
            try { if (FileSystem != null) { await FileSystem.DisposeAsync(); } }
            catch (Exception error) { shutdownError = error; }
            try { await ResourceOperationBase.WhenAll(m_assetCache.Values.Select(a => (ResourceOperationBase)a.Operation).Concat(m_bundleCache.Values.Select(b => (ResourceOperationBase)b.Operation)).Concat(m_rawFiles.Values.Select(file => (ResourceOperationBase)file.Operation)).Concat(m_pendingDownloads.ToArray())); }
            catch { /* 原 Handle 任务保留错误信息；某个加载失败不能阻止其余资源清理。 */ }
            UnloadUnused(true);
            UnregisterDiagnostics();
            m_cacheUsageLease?.Dispose();
            if (shutdownError != null) {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(shutdownError).Throw();
            }
        }

        internal void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != m_threadId) {
                throw new InvalidOperationException("ZRAsset must be used on the Unity main thread.");
            }
        }
    }
}

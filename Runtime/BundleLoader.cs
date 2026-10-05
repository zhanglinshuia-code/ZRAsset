using System;
using System.Threading;
using Object = UnityEngine.Object;

namespace ZRAsset
{
    /// <summary>所有方法均在 Unity 主线程执行；返回的 Bundle 凭证由一个管理器独占管理。</summary>
    public interface IResourceBackend
    {
        ResourceOperationBase<object> LoadBundleAsync(BundleInfo info);
        ResourceOperationBase<Object> LoadAssetAsync(object bundle, string assetPath, Type type);
        void UnloadBundle(object bundle);
    }

    /// <summary>连接文件准备和 Unity 对象加载；旧构造函数和静态读取入口保持兼容。</summary>
    public sealed class BundleLoader: IResourceBackend, IResourceFileBackend, ISynchronousResourceBackend,
        IResourceCollectionBackend, ISynchronousResourceCollectionBackend, IRawFileBackend
    {
        private readonly IUnityResourceLoader m_objectLoader;
        private readonly DownloadPriority m_downloadPriority;
        private readonly IResourceDecryptionServices m_decryptionServices;
        public IResourceFileSystem FileSystem { get; }
        public bool SupportsSynchronousLoading { get { return FileSystem is ISynchronousResourceFileSystem && m_objectLoader is ISynchronousUnityResourceLoader; } }
        public bool SupportsAssetCollections { get { return m_objectLoader is IResourceCollectionBackend collections && collections.SupportsAssetCollections; } }
        public bool SupportsSynchronousCollections { get { return m_objectLoader is ISynchronousResourceCollectionBackend collections && collections.SupportsSynchronousCollections; } }
        public BundleLoader(string root, BundleDownloadCache downloadCache = null, DownloadPriority downloadPriority = DownloadPriority.Normal,
            IResourceDecryptionServices decryptionServices = null)
        {
            if (root == null) {
                throw new ArgumentNullException(nameof(root));
            }
            if (string.IsNullOrWhiteSpace(root) && !(root.Length == 0 && downloadCache != null)) {
                throw new ArgumentException("Bundle root is required.", nameof(root));
            }
            FileSystem = downloadCache == null ? new BuiltInResourceFileSystem(root) : new CachedResourceFileSystem(downloadCache);
            m_decryptionServices = decryptionServices;
            m_objectLoader = new UnityResourceLoader(decryptionServices);
            m_downloadPriority = downloadPriority;
        }

        private BundleLoader(IResourceFileSystem fileSystem, IUnityResourceLoader objectLoader, DownloadPriority priority, IResourceDecryptionServices decryptionServices)
        {
            FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            m_decryptionServices = decryptionServices;
            m_objectLoader = objectLoader ?? new UnityResourceLoader(decryptionServices);
            m_downloadPriority = priority;
        }

        /// <summary>成功交给管理器后，由管理器独占文件系统并关闭；构造失败时调用方仍负责关闭。</summary>
        public static BundleLoader FromFileSystem(IResourceFileSystem fileSystem, IUnityResourceLoader objectLoader = null,
            DownloadPriority priority = DownloadPriority.Normal, IResourceDecryptionServices decryptionServices = null)
        { return new BundleLoader(fileSystem, objectLoader, priority, decryptionServices); }
        public ResourceOperationBase<object> LoadBundleAsync(BundleInfo info)
        {
            RequireUnityBundle(info);
            BundleInfo snapshot = ResourceFileSystem.Snapshot(info);
            return FileSystem is IResourceBundleSource source
                ? source.LoadBundleAsync(snapshot, m_objectLoader)
                : OperationSystem.Start(new BundleOpenOperation(FileSystem.ResolveAsync(snapshot, m_downloadPriority), snapshot, m_objectLoader, FileSystem));
        }

        public ResourceOperationBase<Object> LoadAssetAsync(object bundle, string assetPath, Type type) { return m_objectLoader.LoadAssetAsync(bundle, assetPath, type); }
        public async ResourceOperationBase<RawFileLocation> ResolveRawFileAsync(BundleInfo container, AssetInfo asset) { return RawFileLocation.From(await FileSystem.ResolveAsync(container, m_downloadPriority), container, asset, m_decryptionServices); }
        public RawFileLocation ResolveRawFile(BundleInfo container, AssetInfo asset)
        {
            return FileSystem is not ISynchronousResourceFileSystem sync
                ? throw new NotSupportedException("文件系统不支持同步原始文件加载。")
                : RawFileLocation.From(sync.Resolve(container), container, asset, m_decryptionServices);
        }
        private static void RequireUnityBundle(BundleInfo info)
        { if (info == null) { throw new ArgumentNullException(nameof(info)); } if (info.FileType != ResourceFileType.AssetBundle) { throw new InvalidOperationException("原始文件不能作为 Unity Bundle 加载。"); } }

        public object LoadBundle(BundleInfo info)
        {
            RequireUnityBundle(info);
            if (!SupportsSynchronousLoading) {
                throw new NotSupportedException("当前文件系统或 Unity 加载器不支持同步加载。");
            }
            BundleInfo snapshot = ResourceFileSystem.Snapshot(info);
            ResourceFileLocation file = ((ISynchronousResourceFileSystem)FileSystem).Resolve(snapshot);
            return ((ISynchronousUnityResourceLoader)m_objectLoader).LoadBundle(file, snapshot);
        }
        public Object LoadAsset(object bundle, string assetPath, Type type)
        {
            return !(m_objectLoader is ISynchronousAssetLoader loader)
                ? throw new NotSupportedException("加载器不支持同步资源加载。")
                : loader.LoadAsset(bundle, assetPath, type);
        }
        public ResourceOperationBase<Object[]> LoadSubAssetsAsync(object bundle, string assetPath, Type type) { return CollectionLoader().LoadSubAssetsAsync(bundle, assetPath, type); }
        public ResourceOperationBase<Object[]> LoadAllAssetsAsync(object bundle, Type type) { return CollectionLoader().LoadAllAssetsAsync(bundle, type); }
        public Object[] LoadSubAssets(object bundle, string assetPath, Type type) { return SynchronousCollectionLoader().LoadSubAssets(bundle, assetPath, type); }
        public Object[] LoadAllAssets(object bundle, Type type) { return SynchronousCollectionLoader().LoadAllAssets(bundle, type); }
        private IResourceCollectionBackend CollectionLoader()
        {
            return SupportsAssetCollections ? (IResourceCollectionBackend)m_objectLoader :
            throw new NotSupportedException("加载器不支持资源集合加载。");
        }
        private ISynchronousResourceCollectionBackend SynchronousCollectionLoader()
        {
            return SupportsSynchronousCollections ? (ISynchronousResourceCollectionBackend)m_objectLoader :
            throw new NotSupportedException("加载器不支持同步资源集合加载。");
        }
        public void UnloadBundle(object bundle) { m_objectLoader.UnloadBundle(bundle); }
        public BundleDownloadQueue DownloadQueue { get { return (FileSystem as IResourceDownloadSource)?.DownloadQueue; } }
        public ResourceOperationBase<string> PreloadBundleFileAsync(BundleInfo info, DownloadPriority priority = DownloadPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            return !FileSystem.SupportsDownloads
                ? throw new InvalidOperationException("该管理器未启用下载缓存。")
                : ResolvePathAsync(info, priority, cancellationToken);
        }

        private async ResourceOperationBase<string> ResolvePathAsync(BundleInfo info, DownloadPriority priority, CancellationToken cancellationToken) { return (await FileSystem.ResolveAsync(info, priority, cancellationToken)).Location; }
        public static ResourceOperationBase<string> ReadTextAsync(string location) { return ResourceFileReader.ReadTextAsync(location); }
        public static ResourceOperationBase<string> ReadTextAsync(string location, CancellationToken cancellationToken) { return ResourceFileReader.ReadTextAsync(location, cancellationToken); }
        internal static string Combine(string root, string file) { return ResourcePath.Combine(root, file); }
    }
}

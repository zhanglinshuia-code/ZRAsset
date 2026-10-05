using System;
using Object = UnityEngine.Object;

namespace ZRAsset
{
    public enum ResourceAssetLoadKind { MainAsset, SubAssets, AllAssets }

    /// <summary>可选能力：同步定位只能返回已存在且重新校验过的本地文件，不等待网络/异步操作。</summary>
    public interface ISynchronousResourceFileSystem { ResourceFileLocation Resolve(BundleInfo info); }

    public interface ISynchronousAssetLoader { Object LoadAsset(object bundle, string assetPath, Type type); }
    public interface ISynchronousResourceBackend: ISynchronousAssetLoader
    {
        bool SupportsSynchronousLoading { get; }
        object LoadBundle(BundleInfo info);
    }
    public interface ISynchronousUnityResourceLoader: ISynchronousAssetLoader
    {
        object LoadBundle(ResourceFileLocation file, BundleInfo info);
    }

    /// <summary>子资源含指定类型的主对象与可见子对象；全部资源限定为当前 Bundle，不遍历依赖包。</summary>
    public interface IResourceCollectionBackend
    {
        bool SupportsAssetCollections { get; }
        ResourceOperationBase<Object[]> LoadSubAssetsAsync(object bundle, string assetPath, Type type);
        ResourceOperationBase<Object[]> LoadAllAssetsAsync(object bundle, Type type);
    }
    public interface ISynchronousResourceCollectionBackend
    {
        bool SupportsSynchronousCollections { get; }
        Object[] LoadSubAssets(object bundle, string assetPath, Type type);
        Object[] LoadAllAssets(object bundle, Type type);
    }
}

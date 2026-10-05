using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace ZRAsset
{
    /// <summary>只负责 Unity 对象的打开、反序列化与卸载；输入文件由文件系统定位和准备。</summary>
    public interface IUnityResourceLoader
    {
        ResourceOperationBase<object> LoadBundleAsync(ResourceFileLocation file, BundleInfo info);
        ResourceOperationBase<Object> LoadAssetAsync(object bundle, string assetPath, Type type);
        void UnloadBundle(object bundle);
    }

    public sealed class UnityResourceLoader: IUnityResourceLoader, ISynchronousUnityResourceLoader,
        IResourceCollectionBackend, ISynchronousResourceCollectionBackend
    {
        public static int WebBundleMemoryLimit { get; set; } = 128 * 1024 * 1024;
        private static byte[] ReadWebBundle(Stream stream)
        {
            if (stream.Length > WebBundleMemoryLimit || stream.Length > int.MaxValue) {
                throw new IOException("WebGL Bundle 超过内存加载上限，请缩小分包或显式调整 WebBundleMemoryLimit。");
            }
            var bytes = new byte[(int)stream.Length]; var offset = 0;
            while (offset < bytes.Length) { var count = stream.Read(bytes, offset, bytes.Length - offset); if (count == 0) { throw new EndOfStreamException(); } offset += count; }
            return bytes;
        }
        private readonly IResourceDecryptionServices m_decryptionServices;
        private readonly ResourceDownloadPolicy m_networkPolicy;
        private readonly string m_originalUrl;
        private readonly Dictionary<AssetBundle, Stream> m_streams = new();
        public UnityResourceLoader(IResourceDecryptionServices decryptionServices = null,
            ResourceDownloadPolicy networkPolicy = null, string originalUrl = null)
        { m_decryptionServices = decryptionServices; m_networkPolicy = networkPolicy; m_originalUrl = originalUrl; }
        public bool SupportsAssetCollections { get { return true; } }
        public bool SupportsSynchronousCollections { get { return true; } }
        public object LoadBundle(ResourceFileLocation file, BundleInfo info)
        {
            UnitySceneLoadRequest.RequireUnblockedQueue();
            if (file == null) {
                throw new ArgumentNullException(nameof(file));
            }
            if (info == null) {
                throw new ArgumentNullException(nameof(info));
            }
            if (info.FileType != ResourceFileType.AssetBundle) {
                throw new InvalidOperationException("Raw containers cannot be loaded as AssetBundles.");
            }
            if (info.IsEncrypted) {
                Stream stream = ResourceEncryption.OpenRead(file, info, m_decryptionServices);
                try {
                    AssetBundle decoded = Application.platform == RuntimePlatform.WebGLPlayer ? AssetBundle.LoadFromMemory(ReadWebBundle(stream), info.Crc) :
                        AssetBundle.LoadFromStream(stream, info.Crc, 64 * 1024);
                    if (!decoded) {
                        throw new IOException("Cannot open encrypted bundle: " + info.Name);
                    }
                    m_streams.Add(decoded, stream); stream = null; return decoded;
                }
                finally { stream?.Dispose(); }
            }
            if (file.LocalPath == null) {
                throw new NotSupportedException("同步 AssetBundle 加载需要本地文件。");
            }
            var bundle = AssetBundle.LoadFromFile(file.LocalPath, info.Crc);
            return !bundle ? throw new IOException("Cannot open bundle: " + file.Location) : (object)bundle;
        }

        public Object LoadAsset(object bundle, string assetPath, Type type)
        {
            UnitySceneLoadRequest.RequireUnblockedQueue();
            Object asset = ((AssetBundle)bundle).LoadAsset(assetPath, type);
            return !asset ? throw new IOException($"Asset not found or wrong type: {assetPath} ({type.Name})") : asset;
        }

        public Object[] LoadSubAssets(object bundle, string assetPath, Type type)
        { UnitySceneLoadRequest.RequireUnblockedQueue(); return ((AssetBundle)bundle).LoadAssetWithSubAssets(assetPath, type); }
        public Object[] LoadAllAssets(object bundle, Type type)
        { UnitySceneLoadRequest.RequireUnblockedQueue(); return ((AssetBundle)bundle).LoadAllAssets(type); }
        public ResourceOperationBase<Object[]> LoadSubAssetsAsync(object bundle, string assetPath, Type type)
        {
            AssetBundleRequest request = ((AssetBundle)bundle).LoadAssetWithSubAssetsAsync(assetPath, type);
            return OperationSystem.Start(new UnityResourceRequest<Object[]>(request, () => request.allAssets));
        }
        public ResourceOperationBase<Object[]> LoadAllAssetsAsync(object bundle, Type type)
        {
            AssetBundleRequest request = ((AssetBundle)bundle).LoadAllAssetsAsync(type);
            return OperationSystem.Start(new UnityResourceRequest<Object[]>(request, () => request.allAssets));
        }

        public ResourceOperationBase<object> LoadBundleAsync(ResourceFileLocation file, BundleInfo info)
        {
            if (file == null) { throw new ArgumentNullException(nameof(file)); }
            if (info == null) { throw new ArgumentNullException(nameof(info)); }
            if (info.FileType != ResourceFileType.AssetBundle) { throw new InvalidOperationException("Raw containers cannot be loaded as AssetBundles."); }
            if (info.IsEncrypted && Application.platform != RuntimePlatform.WebGLPlayer) { return LoadEncryptedRequest(file, info); }
            if (info.IsEncrypted || file.LocalPath == null) { return LoadSpecialBundleAsync(file, info); }
            if (!File.Exists(file.LocalPath)) { throw new FileNotFoundException("Bundle not found.", file.LocalPath); }
            if (info.Size > 0 && new FileInfo(file.LocalPath).Length != info.Size) { throw new InvalidDataException("Bundle size mismatch: " + file.LocalPath); }
            AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(file.LocalPath, info.Crc);
            return OperationSystem.Start(new UnityResourceRequest<object>(request, () =>
            {
                AssetBundle bundle = request.assetBundle;
                return !bundle ? throw new IOException("Cannot open bundle: " + file.Location) : (object)bundle;
            }));
        }

        private ResourceOperationBase<object> LoadEncryptedRequest(ResourceFileLocation file, BundleInfo info)
        {
            Stream stream = ResourceEncryption.OpenRead(file, info, m_decryptionServices);
            try {
                AssetBundleCreateRequest request = AssetBundle.LoadFromStreamAsync(stream, info.Crc, 64 * 1024);
                return OperationSystem.Start(new UnityResourceRequest<object>(request, () =>
                {
                    AssetBundle bundle = request.assetBundle;
                    if (!bundle) {
                        throw new IOException("Cannot open encrypted bundle: " + info.Name);
                    }
                    m_streams.Add(bundle, stream);
                    stream = null;
                    return bundle;
                }, () => stream?.Dispose()));
            }
            catch { stream?.Dispose(); throw; }
        }

        private async ResourceOperationBase<object> LoadSpecialBundleAsync(ResourceFileLocation file, BundleInfo info)
        {
            if (file == null) {
                throw new ArgumentNullException(nameof(file));
            }
            if (info == null) {
                throw new ArgumentNullException(nameof(info));
            }
            if (info.FileType != ResourceFileType.AssetBundle) {
                throw new InvalidOperationException("Raw containers cannot be loaded as AssetBundles.");
            }
            if (info.IsEncrypted) {
                Stream stream = ResourceEncryption.OpenRead(file, info, m_decryptionServices);
                try {
                    var web = Application.platform == RuntimePlatform.WebGLPlayer;
                    // 浏览器只有主线程；解密/读取按文件服务的时间片推进，避免整包同步解密形成长帧。
                    AssetBundleCreateRequest decoded = web ? AssetBundle.LoadFromMemoryAsync(
                        await ResourceFileIO.Shared.ReadRawBytesAsync(stream, WebBundleMemoryLimit, default), info.Crc) :
                        AssetBundle.LoadFromStreamAsync(stream, info.Crc, 64 * 1024);
                    await UnityOperations.WaitAsync(decoded);
                    if (decoded.assetBundle == null) {
                        throw new IOException("Cannot open encrypted bundle: " + info.Name);
                    }                    // 内存加载完成后 Unity 不再读取解密流；流加载则保持到 Bundle 卸载。
                    if (!web) { m_streams.Add(decoded.assetBundle, stream); stream = null; }
                    return decoded.assetBundle;
                }
                finally { stream?.Dispose(); }
            }
            AssetBundle bundle;
            if (file.LocalPath == null) {
                // HTTP/jar 由 Unity 原生解码。HTTP 保留 Unity 内容版本缓存，不声称具备可寻址的磁盘文件。
                var useUnityCache = file.Kind == ResourceLocationKind.HttpUri &&
                    info.Hash != null && info.Hash.Length == 32 && info.Hash.All(Uri.IsHexDigit);
                using UnityWebRequest request = useUnityCache ?
                    UnityWebRequestAssetBundle.GetAssetBundle(file.Location, Hash128.Parse(info.Hash), info.Crc) :
                    UnityWebRequestAssetBundle.GetAssetBundle(file.Location, info.Crc);
                request.timeout = 30;
                (m_networkPolicy ?? file.NetworkPolicy)?.Configure(request, new ResourceRequestContext(file.Location, m_originalUrl ?? file.Location, ResourceRequestKind.AssetBundle));
                await UnityOperations.WaitAsync(request.SendWebRequest());
                if (request.result != UnityWebRequest.Result.Success) {
                    if (file.Kind == ResourceLocationKind.HttpUri) {
                        throw new ResourceDownloadException($"Bundle load failed: {file.Location}: {request.error}",
                            request.responseCode, request.GetResponseHeader("Retry-After"));
                    }
                    throw new IOException($"Bundle load failed: {file.Location}: {request.error}");
                }
                bundle = DownloadHandlerAssetBundle.GetContent(request);
            }
            else {
                if (!File.Exists(file.LocalPath)) {
                    throw new FileNotFoundException("Bundle not found.", file.LocalPath);
                }
                if (info.Size > 0 && new FileInfo(file.LocalPath).Length != info.Size) {
                    throw new InvalidDataException("Bundle size mismatch: " + file.LocalPath);
                }
                AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(file.LocalPath, info.Crc);
                await UnityOperations.WaitAsync(request);
                bundle = request.assetBundle;
            }
            return bundle == null ? throw new IOException("Cannot open bundle: " + file.Location) : (object)bundle;
        }

        public ResourceOperationBase<Object> LoadAssetAsync(object bundle, string assetPath, Type type)
        {
            AssetBundleRequest request = ((AssetBundle)bundle).LoadAssetAsync(assetPath, type);
            return OperationSystem.Start(new UnityResourceRequest<Object>(request, () =>
            {
                Object asset = request.asset;
                return !asset ? throw new IOException($"Asset not found or wrong type: {assetPath} ({type.Name})") : asset;
            }));
        }

        public void UnloadBundle(object bundle)
        {
            var native = (AssetBundle)bundle;
            native.Unload(true); // Unity 在整个 Bundle 生命周期内可能继续读取流。
            if (m_streams.TryGetValue(native, out Stream stream)) { m_streams.Remove(native); stream.Dispose(); }
        }
    }
}

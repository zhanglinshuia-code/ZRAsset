using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>目录/StreamingAssets 来源；jar 中需要流读取的文件自动校验落盘，普通 Bundle 可直接加载。</summary>
    public sealed class BuiltInResourceFileSystem: ResourceFileSystem, ISynchronousResourceFileSystem, IResourceFileInspector
    {
        private readonly string m_root;
        private readonly Dictionary<string, BundleInfo> m_packagedBundles;
        private readonly ResourceLocationKind m_kind;
        private readonly ResourceUnpacker m_unpacker;
        private readonly ResourceUnpackOptions m_unpackOptions;
        private readonly ResourceDownloadPolicy m_networkPolicy;
        private readonly Dictionary<string, string[]> m_bundleTags = new(StringComparer.Ordinal);
        public override ResourceFileCapabilities FileCapabilities
        {
            get
            {
                return ResourceFileLocation.DescribeCapabilities(
            m_unpackOptions?.AllFiles == true ? ResourceLocationKind.LocalFile : m_kind);
            }
        }

        public override bool SupportsPersistentCache
        {
            get
            {
                return m_unpacker != null;
            }
        }

        public override bool SupportsDownloads
        {
            get
            {
                return m_unpacker != null && m_kind == ResourceLocationKind.HttpUri;
            }
        }

        public BuiltInResourceFileSystem(string root, ResourceManifest builtInManifest = null,
            ResourceUnpackOptions unpackOptions = null, IDownloadTransport unpackTransport = null, ResourceDownloadPolicy networkPolicy = null)
            : this(root, builtInManifest == null ? null : ResourceManifestPreparation.Copy(builtInManifest), unpackOptions, unpackTransport, networkPolicy) { }

        internal BuiltInResourceFileSystem(string root, ResourceManifestPreparation prepared,
            ResourceUnpackOptions unpackOptions = null, IDownloadTransport unpackTransport = null, ResourceDownloadPolicy networkPolicy = null)
        {
            ResourceManifest builtInManifest = prepared?.Manifest;
            m_networkPolicy = networkPolicy ?? unpackOptions?.NetworkPolicy;
            if (root == null) {
                throw new ArgumentNullException(nameof(root));
            }

            if (string.IsNullOrWhiteSpace(root)) {
                throw new ArgumentException("Bundle root is required.", nameof(root));
            }

            m_kind = ResourcePath.Classify(root);
            m_root = m_kind == ResourceLocationKind.LocalFile ? Path.GetFullPath(root) : ResourcePath.NormalizeUri(root);
            if (builtInManifest != null) {
                m_packagedBundles = prepared.Bundles;
                m_bundleTags = prepared.BundleTags;
            }
            if (m_kind == ResourceLocationKind.HttpUri && ResourcePersistence.IsReady && Application.platform == RuntimePlatform.WebGLPlayer && unpackOptions == null) {
                unpackOptions = new ResourceUnpackOptions(allFiles: true, networkPolicy: m_networkPolicy);
            }

            if (m_kind == ResourceLocationKind.JarUri || (m_kind == ResourceLocationKind.HttpUri && unpackOptions != null)) {
                m_unpackOptions = unpackOptions ?? new ResourceUnpackOptions(builtInManifest?.FormatVersion >= 3 ?
                    ResourcePackageIdentity.GetCacheRoot(Path.Combine(Application.persistentDataPath, "ZRAssetUnpacked"), builtInManifest.PackageName) : null, networkPolicy: m_networkPolicy);
                m_unpacker = new ResourceUnpacker(m_root, builtInManifest?.BuildTarget, m_unpackOptions, unpackTransport);
            }
        }

        private bool RequiresUnpack(BundleInfo info)
        {
            return m_unpacker != null &&
            (m_unpackOptions.AllFiles || info.IsEncrypted || info.FileType != ResourceFileType.AssetBundle ||
             m_unpackOptions.Policy?.ShouldUnpack(Snapshot(info), Array.AsReadOnly(m_bundleTags.TryGetValue(info.Name, out var tags) ? tags : Array.Empty<string>())) == true);
        }

        protected override IDisposable AcquireReadLeaseCore()
        {
            return m_unpacker?.AcquireReadLease();
        }

        protected override ResourceOperationBase ShutdownAsync()
        {
            return m_unpacker?.DisposeAsync() ?? ResourceOperationBase.CompletedOperation;
        }

        protected override async ResourceOperationBase<ResourceFileLocation> ResolveCoreAsync(BundleInfo info, DownloadPriority priority, CancellationToken token)
        {
            if (RequiresUnpack(info)) {
                return await m_unpacker.ResolveAsync(info, token);
            }

            var file = new ResourceFileLocation(ResourcePath.Combine(m_root, info.Name), m_networkPolicy);
            if (file.LocalPath != null) {
                if (!File.Exists(file.LocalPath)) {
                    throw new FileNotFoundException("Bundle not found.", file.LocalPath);
                }

                if ((info.Size > 0 || info.FileType != ResourceFileType.AssetBundle) && new FileInfo(file.LocalPath).Length != info.Size) {
                    throw new InvalidDataException("Bundle size mismatch: " + file.LocalPath);
                }

                if (!string.IsNullOrEmpty(info.Sha256)) {
                    var hash = await ResourceFileIO.Shared.ComputeSha256Async(file.LocalPath, token);
                    if (!string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase)) {
                        throw new InvalidDataException("Bundle SHA-256 mismatch: " + file.LocalPath);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            return file;
        }

        public ResourceFileLocation Resolve(BundleInfo info)
        {
            CheckAvailable();
            info = Snapshot(info);
            if (RequiresUnpack(info)) {
                return m_unpacker.Resolve(info);
            }

            var file = new ResourceFileLocation(ResourcePath.Combine(m_root, info.Name), m_networkPolicy);
            ValidateLocalFile(file, info);
            return file;
        }

        public ResourceOperationBase<ResourcePreparationBundle> InspectAsync(BundleInfo info, CancellationToken cancellationToken = default)
        {
            CheckAvailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (m_kind == ResourceLocationKind.HttpUri) {
                throw new NotSupportedException("HTTP 只读来源无法在不请求网络的情况下证明文件存在。");
            }

            BundleInfo snapshot = Snapshot(info);
            return RunTrackedAsync(async token =>
            {
                ResourceFileLocation file = await TryInspectVerifiedAsync(snapshot, token);
                return new ResourcePreparationBundle(snapshot.Name, snapshot.Size,
                    file == null ? ResourcePreparationSource.Download : ResourcePreparationSource.BuiltIn, file?.Location);
            }, cancellationToken);
        }

        internal static void ValidateLocalFile(ResourceFileLocation file, BundleInfo info)
        {
            if (file.LocalPath == null) {
                throw new NotSupportedException("同步加载需要本地文件，请先异步准备资源：" + file.Location);
            }

            if (!File.Exists(file.LocalPath)) {
                throw new FileNotFoundException("Bundle not found.", file.LocalPath);
            }

            if ((info.Size > 0 || info.FileType != ResourceFileType.AssetBundle) && new FileInfo(file.LocalPath).Length != info.Size) {
                throw new InvalidDataException("Bundle size mismatch: " + file.LocalPath);
            }

            if (!string.IsNullOrEmpty(info.Sha256)) {
                using FileStream input = ResourceFileReader.OpenRead(file);
                using var sha = System.Security.Cryptography.SHA256.Create();
                var hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                if (!string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("Bundle SHA-256 mismatch: " + file.LocalPath);
                }
            }
        }

        internal ResourceFileLocation TryResolveLocal(BundleInfo info)
        {
            CheckAvailable();
            if (m_kind == ResourceLocationKind.JarUri && !HasPackagedProof(info)) {
                return null;
            }

            if (m_kind != ResourceLocationKind.LocalFile && m_kind != ResourceLocationKind.FileUri && !RequiresUnpack(info)) {
                return null;
            }

            try {
                if (!RequiresUnpack(info)) {
                    DownloadStorage.RejectLinks(new ResourceFileLocation(ResourcePath.Combine(m_root, info.Name), m_networkPolicy).LocalPath);
                }

                return Resolve(info);
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException) { return null; }
        }

        private bool HasPackagedProof(BundleInfo info)
        {
            return m_packagedBundles != null &&
            m_packagedBundles.TryGetValue(info.Name, out BundleInfo packaged) && DownloadStorage.IsSha256(info.Sha256) &&
            string.Equals(info.Sha256, packaged.Sha256, StringComparison.OrdinalIgnoreCase) && info.Size == packaged.Size && info.FileType == packaged.FileType;
        }

        // 规划只相信安装清单，不触发 URI 传输或发布。返回 URI 仅描述来源。
        internal async ResourceOperationBase<ResourceFileLocation> TryInspectVerifiedAsync(BundleInfo info, CancellationToken token)
        {
            CheckAvailable(); token.ThrowIfCancellationRequested();
            if (m_kind == ResourceLocationKind.JarUri) {
                if (!HasPackagedProof(info)) {
                    return null;
                }

                ResourceFileLocation cached = RequiresUnpack(info) ? await m_unpacker.TryResolveAsync(info, token) : null;
                return cached ?? new ResourceFileLocation(ResourcePath.Combine(m_root, info.Name), m_networkPolicy);
            }
            return await TryResolveVerifiedAsync(info, token);
        }

        /// <summary>用于缓存回退；HTTP 不能假定已安装，APK 内容必须由随 Player 分发的清单证明相同。</summary>
        internal async ResourceOperationBase<ResourceFileLocation> TryResolveVerifiedAsync(BundleInfo info, CancellationToken token)
        {
            CheckAvailable();
            token.ThrowIfCancellationRequested();
            if (m_kind == ResourceLocationKind.HttpUri) {
                return null;
            }

            if (m_kind == ResourceLocationKind.JarUri && !HasPackagedProof(info)) {
                return null;
            }

            try {
                if (m_kind == ResourceLocationKind.LocalFile || m_kind == ResourceLocationKind.FileUri) {
                    DownloadStorage.RejectLinks(new ResourceFileLocation(ResourcePath.Combine(m_root, info.Name), m_networkPolicy).LocalPath);
                }

                return await ResolveAsync(info, cancellationToken: token);
            }
            catch (Exception error) when (!(error is ResourceInsufficientSpaceException) &&
                !(error is IOException io && ResourceDiskPolicy.IsDiskFull(io)) &&
                (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException)) { return null; }
        }
    }
}

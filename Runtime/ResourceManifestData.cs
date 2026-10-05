#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZRAsset
{
    /// <summary>资源与场景使用不同的 Unity 加载入口，不能把场景当普通资源反序列化。</summary>
    public enum ResourceKind { Asset, Scene, RawFile }

    // V4 的文件格式；原始文件与归档不能交给 Unity AssetBundle 加载器。
    public enum ResourceFileType { AssetBundle = 0, RawFile = 1, Archive = 2 }

    [Serializable]
    public sealed class AssetInfo
    {
        // Address 面向业务，AssetPath 面向 Unity，BundleName 指向所属资源包。
        public string Address;
        public string AssetPath;
        public string BundleName;
        public ResourceKind Kind;
        public string Guid;
        public string[] Tags = Array.Empty<string>();
        // V6: complete resource-specific dependency set, excluding the owning bundle.
        public string[] DependencyBundles;
        public ResourceFileType FileType;
        public long FileOffset, FileSize;
        public string FileSha256;
        [NonSerialized] public bool IsDependencyOnly;
    }

    [Serializable]
    public sealed class BundleInfo
    {
        public string Name;
        public string Hash;
        // SHA-256 负责远端包的强完整性校验，Unity Hash 仍用于差分与构建追踪。
        public string Sha256;
        public uint Crc;
        public long Size;
        public string[] Dependencies = Array.Empty<string>();
        public ResourceFileType FileType;
        // Size/Sha256 描述传输密文；内容字段描述解密后的容器。空 Encryption 表示旧明文格式。
        public string Encryption, EncryptionKeyId;
        public long UnencryptedSize;
        public string UnencryptedSha256;
        public bool IsEncrypted
        {
            get
            {
                return !string.IsNullOrEmpty(Encryption);
            }
        }

        public long ContentSize
        {
            get
            {
                return IsEncrypted ? UnencryptedSize : Size;
            }
        }

        public string ContentSha256
        {
            get
            {
                return IsEncrypted ? UnencryptedSha256 : Sha256;
            }
        }
    }

    [Serializable]
    public sealed partial class ResourceManifest
    {
        // V2 增加资源类型；读取时继续兼容 V1 的纯资源清单。
        public int FormatVersion = 2;
        public string PackageName;
        public string PackageVersion;
        public string BuildTarget;
        public AssetInfo[] Assets = Array.Empty<AssetInfo>();
        public BundleInfo[] Bundles = Array.Empty<BundleInfo>();

        /// <summary>复制可变 DTO 和数组，同时复用不可变字符串，保留二进制清单的字符串表收益。</summary>
        public ResourceManifest Clone()
        {
            ResourceManifest copy = CopyUnchecked();
            copy.Validate();
            return copy;
        }

        // Only private, already validated snapshots may skip validation. Never cache validity on mutable DTOs.
        internal ResourceManifest CopyUnchecked()
        {
            ResourceManifest copy = null;
            foreach (var step in CopySteps(value => copy = value)) { }
            return copy;
        }

        internal IEnumerable<int> CopySteps(Action<ResourceManifest> publish)
        {
            var copy = new ResourceManifest
            {
                FormatVersion = FormatVersion,
                PackageName = PackageName,
                PackageVersion = PackageVersion,
                BuildTarget = BuildTarget,
                Bundles = Bundles == null ? null : new BundleInfo[Bundles.Length],
                Assets = Assets == null ? null : new AssetInfo[Assets.Length]
            };
            for (var i = 0; i < (Bundles?.Length ?? 0); i++) {
                copy.Bundles[i] = CopyBundle(Bundles[i]);
                yield return 0;
            }
            for (var i = 0; i < (Assets?.Length ?? 0); i++) {
                copy.Assets[i] = CopyAsset(Assets[i]);
                yield return 0;
            }
            publish(copy);
        }

        internal static BundleInfo CopyBundle(BundleInfo b)
        {
            return b == null ? null : new BundleInfo
            {
                Name = b.Name,
                Hash = b.Hash,
                Sha256 = b.Sha256,
                Crc = b.Crc,
                Size = b.Size,
                Dependencies = CopyStrings(b.Dependencies),
                FileType = b.FileType,
                Encryption = b.Encryption,
                EncryptionKeyId = b.EncryptionKeyId,
                UnencryptedSize = b.UnencryptedSize,
                UnencryptedSha256 = b.UnencryptedSha256
            };
        }

        internal static AssetInfo CopyAsset(AssetInfo a)
        {
            return a == null ? null : new AssetInfo
            {
                Address = a.Address,
                AssetPath = a.AssetPath,
                BundleName = a.BundleName,
                Kind = a.Kind,
                Guid = a.Guid,
                Tags = CopyStrings(a.Tags),
                DependencyBundles = CopyStrings(a.DependencyBundles),
                FileType = a.FileType,
                FileOffset = a.FileOffset,
                FileSize = a.FileSize,
                FileSha256 = a.FileSha256
            };
        }

        private static string[] CopyStrings(string[] values)
        {
            return values == null ? null : values.Length == 0 ? Array.Empty<string>() : (string[])values.Clone();
        }

        /// <summary>在加载前拒绝非法路径、地址冲突、缺失依赖和循环依赖，避免运行时出现半初始化状态。</summary>
        public void Validate()
        {
            foreach (var step in ValidateSteps()) { }
        }

        // Shared semantic checks: synchronous callers drain the iterator, WebGL yields between records/edges.
        internal IEnumerable<int> ValidateSteps()
        {
            if (FormatVersion < 1 || FormatVersion > 6 || Assets == null || Bundles == null) {
                throw new InvalidDataException("Unsupported or incomplete resource manifest.");
            }

            if (FormatVersion >= 3) {
                if (!ResourceManifestRules.IsValidName(PackageName) || !ResourceManifestRules.IsValidVersion(PackageVersion)) {
                    throw new InvalidDataException("Invalid Package identity or version.");
                }
            }
            else if (!string.IsNullOrEmpty(PackageName) || !string.IsNullOrEmpty(PackageVersion)) {
                throw new InvalidDataException("Package identity requires manifest V3.");
            }

            var bundles = new Dictionary<string, BundleInfo>(StringComparer.Ordinal);
            foreach (BundleInfo bundle in Bundles) {
                if (bundle == null || !IsSafeBundleName(bundle.Name) || bundle.Dependencies == null || bundle.Size < 0) {
                    throw new InvalidDataException("Invalid bundle description.");
                }

                ResourceManifestRules.ValidateEncryption(bundle);
                yield return bundle.IsEncrypted && FormatVersion < 5
                    ? throw new InvalidDataException("Encryption requires manifest V5.")
                    : (uint)bundle.FileType > (uint)ResourceFileType.Archive ||
                    (FormatVersion < 4 && bundle.FileType != ResourceFileType.AssetBundle)
                    ? throw new InvalidDataException("This runtime does not support the declared bundle file type.")
                    : FormatVersion >= 3 && !bundle.Name.StartsWith(ResourceManifestRules.BundlePrefix(PackageName), StringComparison.Ordinal)
                    ? throw new InvalidDataException("Bundle is outside the Package namespace: " + bundle.Name)
                    : !string.IsNullOrEmpty(bundle.Sha256) && !ResourceManifestRules.IsSha256(bundle.Sha256)
                    ? throw new InvalidDataException($"Invalid bundle SHA-256: {bundle.Name}")
                    : !bundles.TryAdd(bundle.Name, bundle) ? throw new InvalidDataException($"Duplicate bundle: {bundle.Name}") : 0;
            }
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            var assetBundles = new Dictionary<string, string>(StringComparer.Ordinal);
            var bundleKinds = new Dictionary<string, ResourceKind>(StringComparer.Ordinal);
            var guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pathGuids = new Dictionary<string, string>(StringComparer.Ordinal);
            var pathDependencies = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var rawEntries = new Dictionary<string, Dictionary<string, AssetInfo>>(StringComparer.Ordinal);
            foreach (AssetInfo asset in Assets) {
                if (asset == null || string.IsNullOrWhiteSpace(asset.Address) ||
                    string.IsNullOrWhiteSpace(asset.AssetPath) || string.IsNullOrEmpty(asset.BundleName) ||
                    !bundles.ContainsKey(asset.BundleName)) {
                    throw new InvalidDataException("Invalid asset or missing asset bundle.");
                }

                if (!addresses.Add(asset.Address)) {
                    throw new InvalidDataException($"Duplicate address: {asset.Address}");
                }

                if (FormatVersion >= 3) {
                    if (!ResourceManifestRules.IsHex(asset.Guid, 32) || asset.Tags == null) {
                        throw new InvalidDataException("Invalid asset GUID or tags: " + asset.Address);
                    }

                    var tags = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var tag in asset.Tags) {
                        yield return string.IsNullOrWhiteSpace(tag) || tag != tag.Trim() || !tags.Add(tag)
                            ? throw new InvalidDataException("Invalid asset GUID or tags: " + asset.Address)
                            : 0;
                    }
                    if (guids.TryGetValue(asset.Guid, out var guidPath) && guidPath != asset.AssetPath) {
                        throw new InvalidDataException("Asset GUID maps to different paths: " + asset.Guid);
                    }

                    if (pathGuids.TryGetValue(asset.AssetPath, out var pathGuid) && !string.Equals(pathGuid, asset.Guid, StringComparison.OrdinalIgnoreCase)) {
                        throw new InvalidDataException("Asset path maps to different GUIDs: " + asset.AssetPath);
                    }

                    guids[asset.Guid] = asset.AssetPath;
                    pathGuids[asset.AssetPath] = asset.Guid;
                }
                if (assetBundles.TryGetValue(asset.AssetPath, out var previous) && previous != asset.BundleName) {
                    throw new InvalidDataException($"Asset assigned to multiple bundles: {asset.AssetPath}");
                }

                assetBundles[asset.AssetPath] = asset.BundleName;
                if ((uint)asset.Kind > (uint)ResourceKind.RawFile || (FormatVersion == 1 && asset.Kind != ResourceKind.Asset)) {
                    throw new InvalidDataException($"Unsupported resource kind: {asset.Address}");
                }

                BundleInfo file = bundles[asset.BundleName];
                if (FormatVersion >= 6) {
                    if (asset.DependencyBundles == null) {
                        throw new InvalidDataException("V6 requires asset dependencies: " + asset.Address);
                    }

                    var uniqueDependencies = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var dependency in asset.DependencyBundles) {
                        yield return string.IsNullOrEmpty(dependency) || dependency == asset.BundleName ||
                            !bundles.TryGetValue(dependency, out BundleInfo dependencyBundle) ||
                            dependencyBundle.FileType != ResourceFileType.AssetBundle || !uniqueDependencies.Add(dependency)
                            ? throw new InvalidDataException("Invalid asset dependency: " + asset.Address)
                            : 0;
                    }
                    if (asset.Kind == ResourceKind.RawFile && uniqueDependencies.Count != 0) {
                        throw new InvalidDataException("Raw files cannot declare asset dependencies.");
                    }

                    if (pathDependencies.TryGetValue(asset.AssetPath, out var previousDependencies)) {
                        if (uniqueDependencies.Count != previousDependencies.Length) {
                            throw new InvalidDataException("Aliases disagree on asset dependencies: " + asset.AssetPath);
                        }

                        foreach (var dependency in previousDependencies) {
                            yield return !uniqueDependencies.Contains(dependency)
                                ? throw new InvalidDataException("Aliases disagree on asset dependencies: " + asset.AssetPath)
                                : 0;
                        }
                    }
                    pathDependencies[asset.AssetPath] = asset.DependencyBundles;
                }
                else if (asset.DependencyBundles != null && asset.DependencyBundles.Length != 0) {
                    throw new InvalidDataException("Asset dependencies require manifest V6.");
                }

                if (asset.FileType != file.FileType || asset.Kind == ResourceKind.RawFile != (file.FileType != ResourceFileType.AssetBundle)) {
                    throw new InvalidDataException("Asset kind/file type mismatch: " + asset.Address);
                }

                if (asset.Kind == ResourceKind.RawFile) {
                    if (!rawEntries.TryGetValue(asset.BundleName, out Dictionary<string, AssetInfo> entries)) {
                        rawEntries.Add(asset.BundleName, entries = new Dictionary<string, AssetInfo>(StringComparer.Ordinal));
                    }

                    if (entries.TryGetValue(asset.AssetPath, out AssetInfo previousRaw)) {
                        if (asset.FileOffset != previousRaw.FileOffset || asset.FileSize != previousRaw.FileSize ||
                            !string.Equals(asset.FileSha256, previousRaw.FileSha256, StringComparison.OrdinalIgnoreCase)) {
                            throw new InvalidDataException("Aliases disagree on raw file metadata.");
                        }
                    }
                    else {
                        entries.Add(asset.AssetPath, asset);
                    }

                    if (FormatVersion < 4 || asset.FileOffset < 0 || asset.FileSize < 0 || asset.FileOffset > file.ContentSize ||
                        asset.FileSize > file.ContentSize - asset.FileOffset || !ResourceManifestRules.IsSha256(asset.FileSha256) ||
                        (file.FileType == ResourceFileType.RawFile && (asset.FileOffset != 0 || asset.FileSize != file.ContentSize)) ||
                        (file.FileType == ResourceFileType.Archive && asset.FileOffset < ResourceManifestRules.ArchiveHeaderSize)) {
                        throw new InvalidDataException("Invalid raw file range or hash: " + asset.Address);
                    }
                }
                else if (asset.FileOffset != 0 || asset.FileSize != 0 || !string.IsNullOrEmpty(asset.FileSha256)) {
                    throw new InvalidDataException("Unity assets cannot declare raw file ranges.");
                }

                if (asset.Kind != ResourceKind.RawFile && asset.Kind == ResourceKind.Scene != asset.AssetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException($"Scene kind/path mismatch: {asset.Address}");
                }

                if (bundleKinds.TryGetValue(asset.BundleName, out ResourceKind kind) && kind != asset.Kind) {
                    throw new InvalidDataException($"Scenes and normal assets cannot share a bundle: {asset.BundleName}");
                }

                bundleKinds[asset.BundleName] = asset.Kind;
                yield return 0;
            }
            foreach (BundleInfo file in Bundles) {
                if (file.FileType == ResourceFileType.AssetBundle) {
                    foreach (var name in file.Dependencies) {
                        yield return bundles.TryGetValue(name, out BundleInfo dependency) && dependency.FileType != ResourceFileType.AssetBundle
                            ? throw new InvalidDataException("Unity Bundle cannot depend on raw files: " + file.Name)
                            : 0;
                    }
                    yield return 0;
                    continue;
                }
                if (file.Dependencies.Length != 0) {
                    throw new InvalidDataException("Raw containers cannot declare Unity dependencies.");
                }

                if (!rawEntries.TryGetValue(file.Name, out Dictionary<string, AssetInfo> entries) || entries.Count == 0 ||
                    (file.FileType == ResourceFileType.RawFile && entries.Count != 1)) {
                    throw new InvalidDataException("RawFile requires one source file; Archive requires at least one entry.");
                }

                var ordered = new SortedSet<AssetInfo>(Comparer<AssetInfo>.Create((left, right) =>
                {
                    var order = left.FileOffset.CompareTo(right.FileOffset);
                    if (order == 0) {
                        order = left.FileSize.CompareTo(right.FileSize);
                    }

                    return order == 0 ? StringComparer.Ordinal.Compare(left.AssetPath, right.AssetPath) : order;
                }));
                foreach (AssetInfo entry in entries.Values) { ordered.Add(entry); yield return 0; }
                long end = file.FileType == ResourceFileType.Archive ? ResourceManifestRules.ArchiveHeaderSize : 0;
                foreach (AssetInfo entry in ordered) {
                    if (entry.FileOffset != end) {
                        throw new InvalidDataException("Raw entries must form a contiguous, non-overlapping file.");
                    }

                    end = checked(entry.FileOffset + entry.FileSize);
                    yield return file.FileType == ResourceFileType.RawFile && !string.IsNullOrEmpty(file.ContentSha256) &&
                        !string.Equals(file.ContentSha256, entry.FileSha256, StringComparison.OrdinalIgnoreCase)
                        ? throw new InvalidDataException("RawFile entry hash differs from its container hash.")
                        : 0;
                }
                if (end != file.ContentSize) {
                    throw new InvalidDataException("Raw container has unaccounted bytes.");
                }
            }
            foreach (var step in ResourceDependencyGraph.ValidateSteps(bundles, FormatVersion >= 6)) {
                yield return step;
            }
        }

        public static bool IsSafeBundleName(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == "..") {
                return false;
            }

            foreach (var c in name) {
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_' && c != '-' && c != '.') {
                    return false;
                }
            }

            return true;
        }
    }
}

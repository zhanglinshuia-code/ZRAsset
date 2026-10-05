using System;
using System.Collections.Generic;
using System.Linq;

namespace ZRAsset.Editor
{
    /// <summary>质量检查后的只读快照；字符串和数组在构建冻结点复制一次，不暴露可修改 DTO。</summary>
    public sealed class ResourceBuildManifestView
    {
        private readonly ResourceManifest m_snapshot;
        public int FormatVersion { get { return m_snapshot.FormatVersion; } }
        public string PackageName { get { return m_snapshot.PackageName; } }
        public string PackageVersion { get { return m_snapshot.PackageVersion; } }
        public string BuildTarget { get { return m_snapshot.BuildTarget; } }
        public IReadOnlyList<ResourceBuildBundleView> Bundles { get; }
        public IReadOnlyList<ResourceBuildAssetView> Assets { get; }
        internal ResourceBuildManifestView(ResourceManifest manifest)
        {
            m_snapshot = manifest.CopyUnchecked();
            Bundles = Array.AsReadOnly(m_snapshot.Bundles.Select(bundle => new ResourceBuildBundleView(bundle)).ToArray());
            Assets = Array.AsReadOnly(m_snapshot.Assets.Select(asset => new ResourceBuildAssetView(asset)).ToArray());
        }
        internal bool Matches(ResourceManifest value)
        {
            if (value == null || value.FormatVersion != FormatVersion || value.PackageName != PackageName ||
                value.PackageVersion != PackageVersion || value.BuildTarget != BuildTarget ||
                value.Bundles == null || value.Assets == null || value.Bundles.Length != Bundles.Count || value.Assets.Length != Assets.Count) { return false; }
            for (var i = 0; i < value.Bundles.Length; i++) {
                BundleInfo left = m_snapshot.Bundles[i], right = value.Bundles[i];
                if (right == null || left.Name != right.Name || left.Hash != right.Hash || left.Sha256 != right.Sha256 || left.Encryption != right.Encryption || left.EncryptionKeyId != right.EncryptionKeyId || left.UnencryptedSha256 != right.UnencryptedSha256 || left.Crc != right.Crc || left.Size != right.Size || left.UnencryptedSize != right.UnencryptedSize || left.FileType != right.FileType || !Same(left.Dependencies, right.Dependencies)) { return false; }
            }
            for (var i = 0; i < value.Assets.Length; i++) {
                AssetInfo left = m_snapshot.Assets[i], right = value.Assets[i];
                if (right == null || left.Address != right.Address || left.AssetPath != right.AssetPath || left.BundleName != right.BundleName || left.Guid != right.Guid || left.FileSha256 != right.FileSha256 || left.Kind != right.Kind || left.FileType != right.FileType || left.FileOffset != right.FileOffset || left.FileSize != right.FileSize || left.IsDependencyOnly != right.IsDependencyOnly || !Same(left.Tags, right.Tags) || !Same(left.DependencyBundles, right.DependencyBundles)) { return false; }
            }
            return true;
        }
        private static bool Same(string[] left, string[] right)
        {
            if (left == null || right == null) { return left == right; }
            if (left.Length != right.Length) { return false; }
            for (var i = 0; i < left.Length; i++) { if (left[i] != right[i]) { return false; } }
            return true;
        }
    }
    public readonly struct ResourceBuildBundleView
    {
        private readonly BundleInfo m_value;
        public string Name { get { return m_value.Name; } }
        public string Hash { get { return m_value.Hash; } }
        public string Sha256 { get { return m_value.Sha256; } }
        public string Encryption { get { return m_value.Encryption; } }
        public string EncryptionKeyId { get { return m_value.EncryptionKeyId; } }
        public string UnencryptedSha256 { get { return m_value.UnencryptedSha256; } }
        public uint Crc { get { return m_value.Crc; } }
        public long Size { get { return m_value.Size; } }
        public long UnencryptedSize { get { return m_value.UnencryptedSize; } }
        public ResourceFileType FileType { get { return m_value.FileType; } }
        public IReadOnlyList<string> Dependencies { get; }
        internal ResourceBuildBundleView(BundleInfo value)
        {
            m_value = value;
            Dependencies = Array.AsReadOnly(value.Dependencies ?? Array.Empty<string>());
        }
    }
    public readonly struct ResourceBuildAssetView
    {
        private readonly AssetInfo m_value;
        public string Address { get { return m_value.Address; } }
        public string AssetPath { get { return m_value.AssetPath; } }
        public string BundleName { get { return m_value.BundleName; } }
        public string Guid { get { return m_value.Guid; } }
        public string FileSha256 { get { return m_value.FileSha256; } }
        public ResourceKind Kind { get { return m_value.Kind; } }
        public ResourceFileType FileType { get { return m_value.FileType; } }
        public long FileOffset { get { return m_value.FileOffset; } }
        public long FileSize { get { return m_value.FileSize; } }
        public bool IsDependencyOnly { get { return m_value.IsDependencyOnly; } }
        public IReadOnlyList<string> Tags { get; }
        public IReadOnlyList<string> DependencyBundles { get; }
        internal ResourceBuildAssetView(AssetInfo value)
        {
            m_value = value;
            Tags = Array.AsReadOnly(value.Tags ?? Array.Empty<string>());
            DependencyBundles = Array.AsReadOnly(value.DependencyBundles ?? Array.Empty<string>());
        }
    }
}

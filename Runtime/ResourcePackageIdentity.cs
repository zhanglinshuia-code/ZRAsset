using System;
using System.IO;

namespace ZRAsset
{
    /// <summary>包名也是磁盘目录和构建命名空间；只允许规范化的小写安全名称。</summary>
    public static class ResourcePackageIdentity
    {
        public static bool IsValidName(string name)
        {
            return ResourceManifestRules.IsValidName(name);
        }

        public static void ValidateName(string name)
        {
            if (!IsValidName(name)) {
                throw new ArgumentException("Package 名必须以小写字母开头，仅含小写字母、数字、下划线和连字符，最多 48 字符，不能使用设备保留名。", nameof(name));
            }
        }

        public static bool IsValidVersion(string version)
        {
            return ResourceManifestRules.IsValidVersion(version);
        }

        // 长度编码避免 a + b_c 与 a_b + c 产生相同命名空间。此名称直接交给 Unity 构建。
        public static string BundlePrefix(string name) { ValidateName(name); return "p" + name.Length + "_" + name + "_"; }
        public static string QualifyBundle(string name, string bundleName)
        {
            return !ResourceManifest.IsSafeBundleName(bundleName)
                ? throw new ArgumentException("无效 Bundle 名。", nameof(bundleName))
                : BundlePrefix(name) + bundleName;
        }

        public static string GetCacheRoot(string parentRoot, string name)
        {
            ValidateName(name);
            return string.IsNullOrWhiteSpace(parentRoot)
                ? throw new ArgumentException("缓存父目录不能为空。", nameof(parentRoot))
                : DownloadStorage.ValidatePath(parentRoot, Path.Combine(parentRoot, "ZRAssetPackages", name));
        }

        public static void ValidateManifest(ResourceManifest manifest, string name, string version = null)
        {
            ValidateName(name);
            if (manifest == null) {
                throw new ArgumentNullException(nameof(manifest));
            }

            manifest.Validate();
            ValidateIdentity(manifest, name, version);
        }

        internal static void ValidateIdentity(ResourceManifest manifest, string name, string version = null)
        {
            ValidateName(name);
            if (manifest == null) {
                throw new ArgumentNullException(nameof(manifest));
            }

            if (manifest.FormatVersion < 3 || manifest.PackageName != name) {
                throw new InvalidDataException($"Package 清单归属不匹配：期望 {name}，实际 {manifest.PackageName} (V{manifest.FormatVersion})。");
            }

            if (version != null && manifest.PackageVersion != version) {
                throw new InvalidDataException($"Package 版本不匹配：{manifest.PackageVersion} != {version}。");
            }
        }
    }
}

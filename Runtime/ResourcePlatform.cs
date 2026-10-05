using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>报告本实现已提供的后端能力；设备是否通过发布验证由项目的验证记录说明。</summary>
    public sealed class ResourcePlatformCapabilities
    {
        public RuntimePlatform Platform { get; }
        public string BuildTarget { get; }
        public bool SupportsBundleLoading
        {
            get
            {
                return BuildTarget != null;
            }
        }

        public bool SupportsDiskDownloads { get; }
        public bool SupportsVersionTransactions
        {
            get
            {
                return SupportsDiskDownloads;
            }
        }

        public bool StreamingAssetsRequiresWebRequest { get; }

        internal ResourcePlatformCapabilities(RuntimePlatform platform, string target, bool disk, bool streamingUri)
        { Platform = platform; BuildTarget = target; SupportsDiskDownloads = disk; StreamingAssetsRequiresWebRequest = streamingUri; }
    }

    /// <summary>使用运行进程的平台判断加载能力。Editor 切换构建目标不代表能加载该目标的 Bundle。</summary>
    public static class ResourcePlatform
    {
        public static ResourcePlatformCapabilities Current
        {
            get
            {
                return Describe(Application.platform);
            }
        }

        public static ResourcePlatformCapabilities Describe(RuntimePlatform platform)
        {
            switch (platform) {
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsServer:
                    return new ResourcePlatformCapabilities(platform, IntPtr.Size == 8 ? "StandaloneWindows64" : "StandaloneWindows", true, false);
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXServer:
                    return new ResourcePlatformCapabilities(platform, "StandaloneOSX", true, false);
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxServer:
                    return new ResourcePlatformCapabilities(platform, "StandaloneLinux64", true, false);
                case RuntimePlatform.Android:
                    return new ResourcePlatformCapabilities(platform, "Android", true, true);
                case RuntimePlatform.IPhonePlayer:
                    return new ResourcePlatformCapabilities(platform, "iOS", true, false);
                case RuntimePlatform.WebGLPlayer:
                    return new ResourcePlatformCapabilities(platform, "WebGL", ResourcePersistence.IsReady && Application.platform == RuntimePlatform.WebGLPlayer, true);
                default:
                    return new ResourcePlatformCapabilities(platform, null, false, false);
            }
        }

        public static void ValidateBuildTarget(string buildTarget, RuntimePlatform? platform = null)
        {
            ResourcePlatformCapabilities capability = Describe(platform ?? Application.platform);
            if (!capability.SupportsBundleLoading) {
                throw new PlatformNotSupportedException($"ZRAsset 尚未实现平台 {capability.Platform} 的 Bundle 加载适配。");
            }

            if (string.Equals(buildTarget, capability.BuildTarget, StringComparison.Ordinal)) {
                return;
            }

            throw new InvalidDataException($"Bundle 构建平台不匹配：清单为 {buildTarget ?? "<空>"}，当前进程 {capability.Platform} 需要 {capability.BuildTarget}。");
        }

        public static void RequireDiskDownloads(string operation = "磁盘下载缓存", RuntimePlatform? platform = null)
        {
            ResourcePlatformCapabilities capability = Describe(platform ?? Application.platform);
            if (!capability.SupportsDiskDownloads) {
                throw new PlatformNotSupportedException($"{operation} 暂不支持 {capability.Platform}。WebGL 可通过 HTTP(S) 直接加载 Bundle。");
            }
        }

        public static void RequireVersionTransactions(string operation = "资源版本事务", RuntimePlatform? platform = null)
        {
            ResourcePlatformCapabilities capability = Describe(platform ?? Application.platform);
            if (!capability.SupportsVersionTransactions) {
                throw new PlatformNotSupportedException($"{operation} 暂不支持 {capability.Platform}；当前实现需要可持久化的磁盘文件系统。");
            }
        }
    }

    public enum ResourceLocationKind { LocalFile, FileUri, JarUri, HttpUri }

    /// <summary>本地路径保留文件名原文，URI 分别编码路径段；禁止子路径覆盖根目录或向上跳转。</summary>
    public static class ResourcePath
    {
        public static ResourceLocationKind Classify(string location)
        {
            if (location == null) {
                throw new ArgumentNullException(nameof(location));
            }

            if (location.StartsWith("jar:", StringComparison.OrdinalIgnoreCase)) {
                return ResourceLocationKind.JarUri;
            }

            if (location.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) {
                return ResourceLocationKind.FileUri;
            }

            if (location.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || location.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) {
                return ResourceLocationKind.HttpUri;
            }
            // 盘符与 UNC 路径不能被 System.Uri 当成网络 scheme。
            var colon = location.IndexOf(':');
            return location.Contains("://") || (colon > 1 && Uri.CheckSchemeName(location.Substring(0, colon)))
                ? throw new NotSupportedException($"不支持的资源 URI：{location}")
                : ResourceLocationKind.LocalFile;
        }

        public static bool IsUri(string location)
        {
            return Classify(location) != ResourceLocationKind.LocalFile;
        }

        /// <param name="relativePath">未进行 URI 编码的相对路径；百分号作为文件名字符处理。</param>
        public static string Combine(string root, string relativePath)
        {
            if (root == null) {
                throw new ArgumentNullException(nameof(root));
            }

            if (string.IsNullOrEmpty(relativePath)) {
                throw new ArgumentException("资源相对路径不能为空。", nameof(relativePath));
            }

            var parts = relativePath.Replace('\\', '/').Split('/');
            if (parts.Any(part => part.Length == 0 || part == "." || part == ".." || part.Contains(':') || part.Contains('\0'))) {
                throw new ArgumentException("资源路径必须是不包含父目录跳转的相对路径。", nameof(relativePath));
            }

            if (Classify(root) == ResourceLocationKind.LocalFile) {
                return Path.Combine(root, Path.Combine(parts));
            }

            var encoded = string.Join("/", parts.Select(Uri.EscapeDataString));
            var normalized = NormalizeUri(root);
            if (Classify(root) == ResourceLocationKind.JarUri) {
                return normalized.TrimEnd('/') + "/" + encoded;
            }
            // Query 与 Fragment 属于根 URI 的参数，追加目录时保持它们的位置和内容。
            var uri = new Uri(normalized, UriKind.Absolute);
            var suffix = uri.Query + uri.Fragment;
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + encoded + suffix;
        }

        public static string NormalizeUri(string location)
        {
            ResourceLocationKind kind = Classify(location);
            if (kind == ResourceLocationKind.LocalFile) {
                throw new ArgumentException("此位置不是 URI。", nameof(location));
            }

            if (kind == ResourceLocationKind.JarUri) {
                var separator = location.IndexOf("!/", StringComparison.Ordinal);
                if (separator < 0) {
                    throw new ArgumentException("Android jar URI 缺少 !/ 分隔符。", nameof(location));
                }

                var archive = location.Substring(4, separator - 4);
                if (Classify(archive) != ResourceLocationKind.FileUri) {
                    throw new ArgumentException("jar URI 必须指向 file URI。", nameof(location));
                }
                // 已编码的路径段只解码一次再编码，防止空格/中文与已有 %xx 被重复转义。
                var entry = location.Substring(separator + 2);
                var encodedEntry = string.Join("/", entry.Split('/').Select(part => Uri.EscapeDataString(Uri.UnescapeDataString(part))));
                return "jar:" + NormalizeUri(archive) + "!/" + encodedEntry;
            }
            return !Uri.TryCreate(location, UriKind.Absolute, out Uri uri) ||
                (kind == ResourceLocationKind.FileUri && !uri.IsFile) ||
                (kind == ResourceLocationKind.HttpUri && string.IsNullOrEmpty(uri.Host))
                ? throw new ArgumentException("资源 URI 格式无效。", nameof(location))
                : uri.AbsoluteUri;
        }
    }
}

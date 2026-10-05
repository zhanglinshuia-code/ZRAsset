using System;
using System.IO;
using System.Threading;

namespace ZRAsset
{
    /// <summary>缓存边界只接受普通目录与文件；拒绝穿越、Windows 设备名及符号链接。</summary>
    internal static class DownloadStorage
    {
        internal static readonly StringComparer PathComparer = Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        internal static bool IsSafeSegment(string value)
        {
            return ResourceManifestRules.IsSafeSegment(value);
        }

        internal static bool IsSha256(string value)
        {
            return ResourceManifestRules.IsSha256(value);
        }

        internal static string ValidatePath(string cacheRoot, string destination)
        {
            if (string.IsNullOrWhiteSpace(destination)) {
                throw new ArgumentException("缓存文件路径不能为空。", nameof(destination));
            }
            // 先检查原始路径，不能让 GetFullPath 把含 .. 的输入悄悄归一化成可写路径。
            foreach (var part in destination.Replace('\\', '/').Split('/')) {
                if (part == "." || part == "..") {
                    throw new ArgumentException("缓存路径不能包含相对跳转。", nameof(destination));
                }
            }

            var root = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(destination);
            var prefix = root + Path.DirectorySeparatorChar;
            StringComparison comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(prefix, comparison)) {
                throw new ArgumentException("缓存文件必须位于配置的 CacheRoot 内。", nameof(destination));
            }

            foreach (var part in full.Substring(prefix.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) {
                if (!IsSafeSegment(part)) {
                    throw new ArgumentException("缓存路径包含不安全的目录或文件名。", nameof(destination));
                }
            }

            RejectLinks(full);
            return full;
        }

        internal static void RejectLinks(string path)
        {
            // 检查每一级已有目录，包括 CacheRoot 本身，避免通过链接写到根目录之外。
            for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) {
                    throw new IOException($"缓存路径不能包含符号链接或重解析点：{current}");
                }
            }
        }

        internal static void DeleteFile(string path)
        {
            RejectLinks(path);
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }

        internal static ResourceOperationBase AppendAsync(string source, string destination, CancellationToken cancellationToken = default)
        {
            RejectLinks(source);
            RejectLinks(destination);
            return ResourceFileIO.Shared.AppendAsync(source, destination, cancellationToken);
        }
    }

    internal static class BundleFileIntegrity
    {
        /// <summary>使用共享的有界文件服务计算 SHA-256，避免大 Bundle 每 64 KiB 等待下一帧。</summary>
        public static ResourceOperationBase<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
        {
            return ResourceFileIO.Shared.ComputeSha256Async(path, cancellationToken);
        }
    }
}

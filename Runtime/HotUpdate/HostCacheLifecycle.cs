using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ZRAsset.HotUpdate
{
    public sealed class HotUpdateHostMismatchException: InvalidOperationException
    {
        public string CurrentPlayerBuildId { get; }
        public string RequiredPlayerBuildId { get; }
        public HotUpdateHostMismatchException(string current, string required)
            : base($"发布目标宿主不匹配：当前 {current}，发布 {required ?? "未声明"}。请获取适配主包的发布或升级安装包。")
        { CurrentPlayerBuildId = current; RequiredPlayerBuildId = required; }
    }

    public sealed class HostCacheCleanupResult
    {
        public long DeletedBytes { get; internal set; }
        public int DeletedFiles { get; internal set; }
        public int BusyHosts { get; internal set; }
        public string[] Warnings { get; internal set; } = Array.Empty<string>();
    }

    // Only this layout and an exact ownership marker authorize whole-host cleanup.
    // Keep the root, marker and usage lock: deleting a lock after closing it races new readers.
    internal static class HostCacheLifecycle
    {
        private const string Marker = "zrasset-host.json";
        [Serializable] private sealed class Owner { public int Format = 1; public string PlayerBuildId; }

        internal static string PathFor(string parent, string player)
        {
            _ = new HotUpdateOptions(player);
            return DownloadStorage.ValidatePath(parent, Path.Combine(Path.GetFullPath(parent), "hosts",
                ResourceVersionManager.ComputeTextSha256(player).Substring(0, 24)));
        }

        internal static CacheUsageLease Register(string parent, string root, string player)
        {
            if (!DownloadStorage.PathComparer.Equals(PathFor(parent, player), Path.GetFullPath(root))) {
                throw new ArgumentException("宿主缓存路径与主包标识不一致。");
            }

            var json = JsonUtility.ToJson(new Owner { PlayerBuildId = player });
            var expected = new UTF8Encoding(false).GetBytes(json);
            if (expected.Length > 4096) {
                throw new ArgumentException("主包缓存归属标记超出长度上限。", nameof(player));
            }

            var lease = CacheUsageLease.Acquire(root);
            try {
                if (IsRegistered(parent, root, player)) {
                    return lease;
                }
                // 初次登记/修复必须独占目录；不能改写正在被其他读者使用的归属。
                lease.Dispose(); lease = null;
                if (!CacheUsageLease.TryAcquireMaintenance(root, out CacheUsageLease registration)) {
                    throw new IOException("主包缓存正在使用，暂时无法登记或恢复归属标记。");
                }

                using (registration) {
                    // 在取得独占锁后重读，另一登记者可能已经提交。
                    if (!IsRegistered(parent, root, player)) {
                        ValidateInitialRegistration(root, expected);
                        var path = DownloadStorage.ValidatePath(root, Path.Combine(root, Marker));
                        ResourceVersionManager.WriteAtomic(path, json);
                    }
                }
                // 正式标记已原子发布；清理器保留标记与租约文件，不会再产生半写入归属。
                return CacheUsageLease.Acquire(root);
            }
            catch { lease?.Dispose(); throw; }
        }

        private static bool IsRegistered(string parent, string root, string player)
        {
            try { return ReadOwner(parent, root)?.PlayerBuildId == player; }
            catch (ArgumentException) { return false; } // 半截 JSON 还须通过下面的精确前缀校验。
        }

        private static void ValidateInitialRegistration(string root, byte[] expected)
        {
            foreach (var item in Directory.GetFileSystemEntries(root)) {
                DownloadStorage.ValidatePath(root, item);
                var name = Path.GetFileName(item);
                if (name == CacheUsageLease.FileName) {
                    continue;
                }
                // 只恢复尚无数据的初次写入；不认领未知文件树或不同宿主的完整标记。
                if ((name != Marker && name != Marker + ".incoming") || Directory.Exists(item)) {
                    throw new IOException("未标记的非空目录不能自动登记为主包缓存。");
                }

                using var stream = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > expected.Length) {
                    throw new InvalidDataException("主包缓存归属标记不匹配。");
                }

                for (int i = 0, value; (value = stream.ReadByte()) != -1; i++) {
                    if (i >= expected.Length || value != expected[i]) {
                        throw new InvalidDataException("主包缓存归属标记不匹配。");
                    }
                }
            }
        }

        private static Owner ReadOwner(string parent, string root)
        {
            var path = DownloadStorage.ValidatePath(parent, Path.Combine(root, Marker));
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) {
                return null;
            }

            Owner owner = JsonUtility.FromJson<Owner>(File.ReadAllText(path));
            return owner?.Format == 1 && !string.IsNullOrWhiteSpace(owner.PlayerBuildId) &&
                DownloadStorage.PathComparer.Equals(PathFor(parent, owner.PlayerBuildId), Path.GetFullPath(root)) ? owner : null;
        }

        internal static string[] OtherRoots(string current)
        {
            var hosts = Path.GetDirectoryName(Path.GetFullPath(current));
            if (Path.GetFileName(hosts) != "hosts") {
                return Array.Empty<string>();
            }

            var parent = Path.GetDirectoryName(hosts);
            if (ReadOwner(parent, current) == null) {
                return Array.Empty<string>();
            }

            var result = new List<string>();
            foreach (var root in Directory.GetDirectories(hosts)) {
                if (DownloadStorage.PathComparer.Equals(root, Path.GetFullPath(current))) {
                    continue;
                }

                try {
                    if (ReadOwner(parent, root) != null) {
                        result.Add(root);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { }
            }
            return result.ToArray();
        }

        internal static CacheUsageLease AcquireSourceLease(string current, string source)
        {
            var ownPrefix = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (source.StartsWith(ownPrefix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) { DownloadStorage.ValidatePath(current, source); return null; }
            foreach (var root in OtherRoots(current)) {
                var prefix = root + Path.DirectorySeparatorChar;
                if (!source.StartsWith(prefix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
                    continue;
                }

                DownloadStorage.ValidatePath(root, source);
                return CacheUsageLease.Acquire(root);
            }
            // Same-host copies already hold a lease on current. Unknown sibling sources are rejected.
            throw new IOException("复用来源已失去主包缓存归属，需重新选择来源。");
        }

        internal static HostCacheCleanupResult CleanOldHosts(string parent, string current)
        {
            if (ReadOwner(parent, current) == null) {
                throw new InvalidDataException("当前主包没有有效归属记录。");
            }

            var result = new HostCacheCleanupResult();
            var warnings = new List<string>();
            foreach (var root in OtherRoots(current)) {
                try {
                    if (!CacheUsageLease.TryAcquireMaintenance(root, out CacheUsageLease lease)) { result.BusyHosts++; continue; }
                    using (lease) {
                        if (ReadOwner(parent, root) == null) {
                            continue;
                        }

                        var files = new List<string>(); var dirs = new List<string>();
                        var pending = new Stack<string>(); pending.Push(root);
                        // Validate the entire tree before the first deletion; never traverse a link.
                        while (pending.Count != 0) {
                            var dir = pending.Pop();
                            foreach (var item in Directory.GetFileSystemEntries(dir)) {
                                DownloadStorage.ValidatePath(root, item);
                                if (Directory.Exists(item)) { dirs.Add(item); pending.Push(item); }
                                else if (dir != root || (Path.GetFileName(item) != Marker && Path.GetFileName(item) != CacheUsageLease.FileName)) {
                                    files.Add(item);
                                }
                            }
                        }
                        foreach (var file in files) {
                            DownloadStorage.ValidatePath(root, file);
                            var bytes = new FileInfo(file).Length;
                            File.Delete(file); result.DeletedFiles++; result.DeletedBytes += bytes;
                        }
                        foreach (var dir in dirs.OrderByDescending(p => p.Length)) { DownloadStorage.ValidatePath(root, dir); Directory.Delete(dir, false); }
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { warnings.Add(e.Message); }
            }
            result.Warnings = warnings.ToArray();
            return result;
        }
    }
}

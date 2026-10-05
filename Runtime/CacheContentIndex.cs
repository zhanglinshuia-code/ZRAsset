using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>
    /// 每个缓存实例按需扫描登记内容；进程内发布增量更新活跃索引，避免反复扫描缓存根。
    /// 索引只缩小候选范围：文件读取与复制前仍必须校验字节，不能把目录名或标记当成完整性证据。
    /// </summary>
    internal sealed class CacheContentIndex
    {
        private sealed class Candidate { internal string Version, Path; }
        private readonly Dictionary<(string, long), List<Candidate>> m_entries = new();
        private readonly HashSet<string> m_paths = new(DownloadStorage.PathComparer);
        private static readonly List<WeakReference<CacheContentIndex>> s_liveIndexes = new();
        private readonly HashSet<string> m_roots = new(DownloadStorage.PathComparer);
        private string m_root, m_target;

        internal static void Published(string root, string path, BundleInfo info)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = root + Path.DirectorySeparatorChar;
            path = Path.GetFullPath(path);
            if (!path.StartsWith(prefix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) { return; }
            var parts = path.Substring(prefix.Length).Replace('\\', '/').Split('/');
            if (parts.Length != 4 || !DownloadStorage.IsSafeSegment(parts[0]) ||
                !string.Equals(parts[2], info.Sha256, StringComparison.OrdinalIgnoreCase) || !PathEquals(parts[3], info.Name)) { return; }
            for (var i = s_liveIndexes.Count - 1; i >= 0; i--) {
                if (!s_liveIndexes[i].TryGetTarget(out CacheContentIndex index)) { s_liveIndexes.RemoveAt(i); continue; }
                if (index.m_roots.Contains(root) && PathEquals(parts[1], index.m_target)) {
                    index.Add(PathEquals(root, index.m_root) ? parts[0] : "host/" + parts[0], path, info.Sha256, info.Size);
                }
            }
        }

        internal static async ResourceOperationBase<CacheContentIndex> BuildAsync(string root, string target, CancellationToken token, bool includeHosts = true)
        {
            var index = new CacheContentIndex
            {
                m_root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                m_target = target
            };
            index.m_roots.Add(index.m_root);
            if (includeHosts) {
                foreach (var otherRoot in HotUpdate.HostCacheLifecycle.OtherRoots(root)) {
                    index.m_roots.Add(Path.GetFullPath(otherRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
            }
            // 先登记再异步扫描，扫描期间发布的文件也能进入索引；弱引用不延长缓存生命周期。
            s_liveIndexes.RemoveAll(reference => !reference.TryGetTarget(out _));
            s_liveIndexes.Add(new WeakReference<CacheContentIndex>(index));
            DownloadStorage.RejectLinks(root);
            if (!Directory.Exists(root)) {
                return index;
            }

            var directories = new Stack<string>(); directories.Push(root);
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var slice = System.Diagnostics.Stopwatch.StartNew();
            while (directories.Count != 0) {
                token.ThrowIfCancellationRequested();
                var directory = directories.Pop();
                string[] children;
                try {
                    DownloadStorage.RejectLinks(directory);
                    children = await ResourceOperationBase.Run(() => Directory.GetFileSystemEntries(directory));
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException) { continue; }
                foreach (var path in children) {
                    token.ThrowIfCancellationRequested();
                    if (slice.Elapsed.TotalMilliseconds >= 2) { await ResourceOperationBase.Yield(); slice.Restart(); }
                    try {
                        FileAttributes attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) {
                            continue;
                        }

                        var parts = path.Substring(prefix.Length).Replace('\\', '/').Split('/');
                        if ((attributes & FileAttributes.Directory) != 0) {
                            // 只访问登记布局，不递归扫描无关目录、锁目录或任意用户文件树。
                            var versions = PathEquals(parts[0], "ZRAssetVersions");
                            var relevant = versions
                                ? parts.Length <= 4 && (parts.Length < 2 || PathEquals(parts[1], target)) &&
                                    (parts.Length < 3 || PathEquals(parts[2], "versions"))
                                : parts.Length <= 3 && !PathEquals(parts[0], CacheDestinationLock.InfrastructureDirectory) &&
                                    (parts.Length < 2 || PathEquals(parts[1], target)) &&
                                    (parts.Length < 3 || DownloadStorage.IsSha256(parts[2]));
                            if (relevant) {
                                directories.Push(path);
                            }

                            continue;
                        }
                        if (parts.Length == 5 && PathEquals(parts[0], "ZRAssetVersions") && PathEquals(parts[1], target) &&
                            PathEquals(parts[2], "versions") && PathEquals(parts[4], "manifest.json") && DownloadStorage.IsSafeSegment(parts[3])) {
                            if (new FileInfo(path).Length > 16 * 1024 * 1024) {
                                continue;
                            }

                            DownloadStorage.ValidatePath(root, path);
                            ResourceManifest manifest = await ResourceManifest.FromJsonAsync(
                                await ResourceFileReader.ReadTextAsync(path, 16 * 1024 * 1024, token), cancellationToken: token);
                            if (!string.Equals(manifest.BuildTarget, target, StringComparison.Ordinal)) {
                                continue;
                            }

                            foreach (BundleInfo bundle in manifest.Bundles) {
                                token.ThrowIfCancellationRequested();
                                if (slice.Elapsed.TotalMilliseconds >= 2) { await ResourceOperationBase.Yield(); slice.Restart(); }
                                BundleDownloadQueue.ValidateInfo(bundle);
                            }

                            foreach (BundleInfo bundle in manifest.Bundles) {
                                token.ThrowIfCancellationRequested();
                                if (slice.Elapsed.TotalMilliseconds >= 2) { await ResourceOperationBase.Yield(); slice.Restart(); }
                                var source = DownloadStorage.ValidatePath(root, System.IO.Path.Combine(root, parts[3], target, bundle.Sha256.ToLowerInvariant(), bundle.Name));
                                index.Add(parts[3], source, bundle.Sha256, bundle.Size);
                            }
                        }
                        else if (parts.Length == 4 && PathEquals(parts[1], target) && DownloadStorage.IsSafeSegment(parts[0]) &&
                            DownloadStorage.IsSha256(parts[2]) && parts[3].EndsWith(CacheOwnership.Suffix,
                                Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
                            DownloadStorage.ValidatePath(root, path);
                            CacheOwnership.Record owner = CacheOwnership.Read(path);
                            if (owner == null || !string.Equals(owner.Sha256, parts[2], StringComparison.OrdinalIgnoreCase)) {
                                continue;
                            }

                            var source = path.Substring(0, path.Length - CacheOwnership.Suffix.Length);
                            if (!PathEquals(System.IO.Path.GetFileName(source), owner.Name)) {
                                continue;
                            }

                            index.Add(parts[0], source, owner.Sha256, owner.Size);
                        }
                    }
                    catch (Exception exception) when (exception is IOException || exception is InvalidDataException ||
                        exception is UnauthorizedAccessException || exception is ArgumentException || exception is InvalidOperationException) { /* 不能识别或读取的登记不进入索引；目标仍可走正式下载。 */ }
                }
            }
            if (includeHosts) {
                foreach (var sourceRoot in HotUpdate.HostCacheLifecycle.OtherRoots(root)) {
                    token.ThrowIfCancellationRequested();
                    try {
                        using var sourceLease = CacheUsageLease.Acquire(sourceRoot);
                        CacheContentIndex other = await BuildAsync(sourceRoot, target, token, false);
                        foreach (KeyValuePair<(string, long), List<Candidate>> entry in other.m_entries.ToArray()) {
                            foreach (Candidate candidate in entry.Value.ToArray()) {
                                token.ThrowIfCancellationRequested();
                                if (slice.Elapsed.TotalMilliseconds >= 2) { await ResourceOperationBase.Yield(); slice.Restart(); }
                                index.Add("host/" + candidate.Version, candidate.Path, entry.Key.Item1, entry.Key.Item2);
                            }
                        }
                    }
                    catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException) { /* 旧宿主只是可选复用来源；无法访问时继续查找或正常下载，不吞掉取消。 */ }
                }
            }

            return index;
        }

        internal async ResourceOperationBase<string> FindVerifiedAsync(string currentVersion, string destination, BundleInfo info, CancellationToken token)
        {
            if (!m_entries.TryGetValue((info.Sha256.ToLowerInvariant(), info.Size), out List<Candidate> candidates)) {
                return null;
            }

            // 校验会让出主线程；同时发布可以追加候选，但不能使在途枚举器失效。
            var count = candidates.Count;
            for (var i = 0; i < count; i++) {
                Candidate candidate = candidates[i];
                token.ThrowIfCancellationRequested();
                if (PathEquals(candidate.Version, currentVersion) || PathEquals(candidate.Path, destination)) {
                    continue;
                }

                try {
                    DownloadStorage.RejectLinks(candidate.Path);
                    if (await BundleDownloadQueue.IsVerifiedAsync(candidate.Path, info, token)) {
                        return candidate.Path;
                    }
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException) { /* 源被删除、占用或损坏时继续找其他源，不修改源文件。 */ }
            }
            return null;
        }

        private void Add(string version, string path, string hash, long size)
        {
            if (!m_paths.Add(path)) {
                return;
            }

            (string, long size) key = (hash.ToLowerInvariant(), size);
            if (!m_entries.TryGetValue(key, out List<Candidate> candidates)) {
                m_entries[key] = candidates = new List<Candidate>();
            }

            candidates.Add(new Candidate { Version = version, Path = path });
        }
        private static bool PathEquals(string left, string right)
        {
            return DownloadStorage.PathComparer.Equals(left, right);
        }
    }

    /// <summary>下载与复制使用同一个目标锁；不同目标仍可并发，进程退出后由系统释放锁。</summary>
    internal static class CacheDestinationLock
    {
        internal const string InfrastructureDirectory = "ZRAssetWriteLocks";

        internal static async ResourceOperationBase<ResourceFileLock> AcquireAsync(string root, string destination, CancellationToken token)
        {
            var full = DownloadStorage.ValidatePath(root, destination);
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var relative = full.Substring(prefix.Length).Replace('\\', '/');
            StringComparison comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (relative.Equals(InfrastructureDirectory, comparison) || relative.StartsWith(InfrastructureDirectory + "/", comparison)) {
                throw new ArgumentException("下载目标不能位于内部写入锁目录。", nameof(destination));
            }
            // 以缓存根内的规范相对路径计算键；同一 Windows 路径的大小写别名必须得到同一把锁。
            // 锁放入独立命名空间，合法包名即使以旧锁后缀结尾也不会覆盖另一个包的锁文件。
            if (Path.DirectorySeparatorChar == '\\') {
                relative = relative.ToUpperInvariant();
            }

            string key;
            using (var sha = SHA256.Create()) {
                key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(relative))).Replace("-", "").ToLowerInvariant();
            }

            var path = DownloadStorage.ValidatePath(root, Path.Combine(root, InfrastructureDirectory, key + ".lock"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            while (true) {
                token.ThrowIfCancellationRequested();
                DownloadStorage.RejectLinks(path);
                try { return ResourceFileLock.Open(path); }
                catch (IOException exception) when ((exception.HResult & 0xffff) == 32 || (exception.HResult & 0xffff) == 33) { await ResourceOperationBase.Delay(25, token); }
            }
        }
    }
}

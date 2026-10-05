using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>
    /// 同一缓存根的读者租约。下载队列、资源管理器和版本事务持共享读锁；维护必须取得独占锁。
    /// 不依赖 PID、时间或心跳判断存活：进程退出后操作系统自动释放文件句柄。
    /// </summary>
    internal sealed class CacheUsageLease: IDisposable
    {
        internal const string FileName = "zrasset-usage.lock";
        private ResourceFileLock m_stream;
        private CacheUsageLease(ResourceFileLock stream) { m_stream = stream; }

        internal static CacheUsageLease Acquire(string cacheRoot)
        {
            var path = PreparePath(cacheRoot);
            return new CacheUsageLease(ResourceFileLock.Open(path, true));
        }

        internal static bool TryAcquireMaintenance(string cacheRoot, out CacheUsageLease lease)
        {
            var path = PreparePath(cacheRoot);
            try {
                lease = new CacheUsageLease(ResourceFileLock.Open(path));
                return true;
            }
            catch (IOException) { lease = null; return false; }
        }

        private static string PreparePath(string cacheRoot)
        {
            if (string.IsNullOrWhiteSpace(cacheRoot)) {
                throw new ArgumentException("缓存根不能为空。", nameof(cacheRoot));
            }

            var root = Path.GetFullPath(cacheRoot);
            DownloadStorage.RejectLinks(root);
            var path = DownloadStorage.ValidatePath(root, Path.Combine(root, FileName));
            Directory.CreateDirectory(root);
            DownloadStorage.RejectLinks(path);
            return path;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref m_stream, null)?.Dispose();
        }
    }

    /// <summary>V8 下载写入标记；只记录已明确交给下载队列管理的单个目标，不声明整个目录的所有权。</summary>
    internal static class CacheOwnership
    {
        internal const string Suffix = ".zrasset-owner.json";
        [Serializable]
        internal sealed class Record
        {
            public int FormatVersion = 1;
            public string Name;
            public string Sha256;
            public long Size;
        }

        internal static void Register(string root, string destination, BundleInfo info)
        {
            var path = DownloadStorage.ValidatePath(root, destination + Suffix);
            var record = new Record { Name = info.Name, Sha256 = info.Sha256.ToLowerInvariant(), Size = info.Size };
            if (File.Exists(path)) {
                Record old = Read(path) ?? throw new IOException("缓存所有权标记被其他内容占用，不能覆盖：" + path);
                if (old.Name == record.Name && old.Sha256 == record.Sha256 && old.Size == record.Size) {
                    return;
                }
            }
            // 标记不是提交凭证：写到一半的标记会被维护当作未知文件保留，不会扩大删除范围。
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(JsonUtility.ToJson(record));
        }

        internal static Record Read(string path)
        {
            try {
                DownloadStorage.RejectLinks(path);
                if (new FileInfo(path).Length > 64 * 1024) {
                    return null;
                }

                Record record = JsonUtility.FromJson<Record>(File.ReadAllText(path));
                return record != null && record.FormatVersion == 1 && ResourceManifest.IsSafeBundleName(record.Name) &&
                    DownloadStorage.IsSafeSegment(record.Name) && DownloadStorage.IsSha256(record.Sha256) && record.Size >= 0 ? record : null;
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is ArgumentException) { return null; }
        }
    }

    public enum ResourceCacheMaintenanceStatus { Ready, Busy, Blocked, Completed }
    public enum ResourceCacheFileKind { Bundle, ExpiredPartial, OwnershipMarker, Manifest }

    /// <summary>配额统计包含缓存根下全部可计量文件；未知文件与版本元数据也占用配额，但不会被强制删除。</summary>
    public sealed class ResourceCacheMaintenanceOptions
    {
        public long MaxCacheBytes { get; }
        public TimeSpan PartialMaxAge { get; }
        internal HashSet<string> SelectedNames { get; }
        public bool ClearUnusedManifests { get; }
        public ResourceCacheMaintenanceOptions(long maxCacheBytes, TimeSpan? partialMaxAge = null,
            ResourceManifest selectionManifest = null, ResourceSelection selection = null, bool clearUnusedManifests = false)
        {
            if (maxCacheBytes < 0) {
                throw new ArgumentOutOfRangeException(nameof(maxCacheBytes));
            }

            MaxCacheBytes = maxCacheBytes;
            PartialMaxAge = partialMaxAge ?? TimeSpan.FromDays(1);
            if (PartialMaxAge < TimeSpan.Zero) {
                throw new ArgumentOutOfRangeException(nameof(partialMaxAge));
            }

            if (selectionManifest == null != (selection == null)) {
                throw new ArgumentException("按资源清理必须同时提供清单和选择条件。");
            }

            if (selection != null) {
                SelectedNames = new HashSet<string>(new ResourceCatalog(selectionManifest).Select(selection).BundleNames, StringComparer.Ordinal);
            }

            if (selection != null && clearUnusedManifests) {
                throw new ArgumentException("清单清理不能与资源选择组合。");
            }

            ClearUnusedManifests = clearUnusedManifests;
        }
    }

    public sealed class ResourceCacheFile
    {
        public string Path { get; internal set; }
        public long Bytes { get; internal set; }
        public DateTime LastWriteUtc { get; internal set; }
        public ResourceCacheFileKind Kind { get; internal set; }
    }

    /// <summary>只读的清理预览；Apply 会重新扫描保护关系，预览不是绕过保护检查的删除授权清单。</summary>
    public sealed class ResourceCacheMaintenancePlan
    {
        public string CacheRoot { get; internal set; }
        public ResourceCacheMaintenanceOptions Options { get; internal set; }
        public ResourceCacheMaintenanceStatus Status { get; internal set; }
        public IReadOnlyList<ResourceCacheFile> Files { get; internal set; } = Array.Empty<ResourceCacheFile>();
        public IReadOnlyList<string> Messages { get; internal set; } = Array.Empty<string>();
        public long ExistingBytes { get; internal set; }
        public long ProtectedBytes { get; internal set; }
        public long UnknownBytes { get; internal set; }
        public int UnknownFiles { get; internal set; }
        public int UnmeasuredEntries { get; internal set; }
        public long PlannedReclaimedBytes { get; internal set; }
        public long RemainingBytes
        {
            get
            {
                return Math.Max(0, ExistingBytes - PlannedReclaimedBytes);
            }
        }

        public long UnmetQuotaBytes
        {
            get
            {
                return Math.Max(0, RemainingBytes - Options.MaxCacheBytes);
            }
        }
    }

    public sealed class ResourceCacheMaintenanceResult
    {
        public ResourceCacheMaintenanceStatus Status { get; internal set; }
        public long ReclaimedBytes { get; internal set; }
        public int DeletedFiles { get; internal set; }
        public long RemainingBytes { get; internal set; }
        public long ProtectedBytes { get; internal set; }
        public long UnknownBytes { get; internal set; }
        public int UnknownFiles { get; internal set; }
        public int UnmeasuredEntries { get; internal set; }
        public long UnmetQuotaBytes { get; internal set; }
        public IReadOnlyList<string> Messages { get; internal set; } = Array.Empty<string>();
    }

    /// <summary>
    /// 显式执行的保守磁盘维护。先 Dispose 所有使用此缓存的管理器和下载队列，再 Plan/Apply。
    /// 仅逐个删除有效版本清单或 V8 所有权标记识别的文件；从不递归删除目录，也不清理 Unity 原生缓存。
    /// </summary>
    public static class ResourceCacheMaintenance
    {
        public static ResourceOperationBase<ResourceCacheMaintenanceResult> ApplyAsync(ResourceCacheMaintenancePlan plan)
        { return ApplyAsync(plan, default); }

        public static ResourceOperationBase<ResourceCacheMaintenanceResult> ApplyAsync(ResourceCacheMaintenancePlan plan, CancellationToken cancellationToken)
        { return ApplyCoreAsync(plan, new MaintenanceWork(true, cancellationToken)); }

        public static async ResourceOperationBase<ResourceCacheMaintenancePlan> PlanAsync(string cacheRoot,
            ResourceCacheMaintenanceOptions options, CancellationToken cancellationToken = default)
        {
            ResourcePlatform.RequireDiskDownloads("磁盘缓存维护");
            if (options == null) { throw new ArgumentNullException(nameof(options)); }
            cancellationToken.ThrowIfCancellationRequested();
            var root = NormalizeRoot(cacheRoot);
            ResourcePersistence.RegisterCacheRoot(root);
            if (!CacheUsageLease.TryAcquireMaintenance(root, out CacheUsageLease lease)) { return BusyPlan(root, options); }
            using (lease) {
                await ResourceOperationBase.Yield();
                return await ScanCoreAsync(root, options, new MaintenanceWork(true, cancellationToken));
            }
        }

        [Serializable] private sealed class Pointer { public string Active, Previous, ManifestSha256; }
        private sealed class FileState
        {
            internal string Path, Relative;
            internal long Bytes;
            internal DateTime Modified;
        }
        private sealed class Owner
        {
            internal string Destination, Marker;
            internal bool Protected;
            internal List<string> ReuseTemporaries;
        }

        public static ResourceCacheMaintenancePlan Plan(string cacheRoot, ResourceCacheMaintenanceOptions options)
        {
            ResourcePlatform.RequireDiskDownloads("磁盘缓存维护");
            if (options == null) {
                throw new ArgumentNullException(nameof(options));
            }

            var root = NormalizeRoot(cacheRoot);
            ResourcePersistence.RegisterCacheRoot(root);
            if (!CacheUsageLease.TryAcquireMaintenance(root, out CacheUsageLease lease)) {
                return BusyPlan(root, options);
            }

            using (lease) {
                return ScanCoreAsync(root, options, new MaintenanceWork(false, default)).GetAwaiter().GetResult();
            }
        }

        public static ResourceCacheMaintenanceResult Apply(ResourceCacheMaintenancePlan plan)
        { return ApplyCoreAsync(plan, new MaintenanceWork(false, default)).GetAwaiter().GetResult(); }

        private static async ResourceOperationBase<ResourceCacheMaintenanceResult> ApplyCoreAsync(ResourceCacheMaintenancePlan plan, MaintenanceWork work)
        {
            work.Token.ThrowIfCancellationRequested();
            if (plan == null) {
                throw new ArgumentNullException(nameof(plan));
            }

            if (plan.Status != ResourceCacheMaintenanceStatus.Ready) {
                return Result(plan, plan.Status, 0, 0, plan.Messages);
            }

            if (!CacheUsageLease.TryAcquireMaintenance(plan.CacheRoot, out CacheUsageLease lease)) {
                return Result(BusyPlan(plan.CacheRoot, plan.Options), ResourceCacheMaintenanceStatus.Busy, 0, 0,
                    new[] { "缓存正在使用或另一次维护正在执行；没有删除任何文件。" });
            }

            using (lease) {
                if (work.Asynchronous) { await ResourceOperationBase.Yield(); }
                ResourceCacheMaintenancePlan current = await ScanCoreAsync(plan.CacheRoot, plan.Options, work);
                if (current.Status != ResourceCacheMaintenanceStatus.Ready) {
                    return Result(current, current.Status, 0, 0, current.Messages);
                }

                var eligible = new Dictionary<string, ResourceCacheFile>(DownloadStorage.PathComparer);
                foreach (ResourceCacheFile file in current.Files) { await work.Checkpoint(); eligible.Add(file.Path, file); }
                long reclaimed = 0;
                var deleted = 0;
                var messages = new List<string>(current.Messages);
                try {
                    foreach (ResourceCacheFile proposed in plan.Files) {
                        await work.Checkpoint();
                        if (!eligible.TryGetValue(proposed.Path, out ResourceCacheFile now) || now.Bytes != proposed.Bytes || now.LastWriteUtc != proposed.LastWriteUtc) { messages.Add("保护关系或文件快照已变化，保留：" + proposed.Path); continue; }
                        try {
                            var path = DownloadStorage.ValidatePath(plan.CacheRoot, proposed.Path);
                            DownloadStorage.RejectLinks(path);
                            if (proposed.Kind == ResourceCacheFileKind.OwnershipMarker) {
                                var destination = path.Substring(0, path.Length - CacheOwnership.Suffix.Length);
                                if (new[] { destination, destination + ".part", destination + ".part.json", destination + ".part.incoming",
                                destination + ".part.incoming.json", destination + ".part.json.incoming", destination + ".part.incoming.json.incoming" }.Any(File.Exists) || HasReuseTemporary(destination)) { messages.Add("目标文件仍被保留，继续保留所有权标记：" + path); continue; }
                            }
                            var file = new FileInfo(path);
                            if (!file.Exists || file.Length != proposed.Bytes || file.LastWriteTimeUtc != proposed.LastWriteUtc) { messages.Add("文件在预检后变化，保留：" + path); continue; }
                            File.Delete(path);
                            reclaimed += proposed.Bytes;
                            deleted++;
                        }
                        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException) { messages.Add("未能删除 " + proposed.Path + "：" + exception.Message); }
                    }
                    ResourceCacheMaintenancePlan after = await ScanCoreAsync(plan.CacheRoot, plan.Options, work);
                    messages.AddRange(after.Messages);
                    return Result(after, after.Status == ResourceCacheMaintenanceStatus.Ready ? ResourceCacheMaintenanceStatus.Completed : after.Status,
                        reclaimed, deleted, messages.AsReadOnly());
                }
                finally { if (work.Asynchronous && deleted > 0) { await ResourcePersistence.FlushAsync(); } }
            }
        }

        private static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) {
                throw new ArgumentException("缓存根不能为空。", nameof(root));
            }

            foreach (var segment in root.Replace('\\', '/').Split('/')) {
                if (segment == "." || segment == "..") {
                    throw new ArgumentException("缓存根不能包含相对跳转。", nameof(root));
                }
            }

            var full = Path.GetFullPath(root);
            DownloadStorage.RejectLinks(full);
            return full;
        }

        private static ResourceCacheMaintenancePlan BusyPlan(string root, ResourceCacheMaintenanceOptions options)
        {
            return new()
            {
                CacheRoot = root,
                Options = options,
                Status = ResourceCacheMaintenanceStatus.Busy,
                UnmeasuredEntries = 1,
                Messages = new[] { "缓存租约仍被资源管理器、下载队列或版本事务持有，请先完成并 Dispose 后再维护。" }
            };
        }

        private static ResourceCacheMaintenanceResult Result(ResourceCacheMaintenancePlan plan, ResourceCacheMaintenanceStatus status,
            long reclaimed, int deleted, IReadOnlyList<string> messages)
        {
            return new()
            {
                Status = status,
                ReclaimedBytes = reclaimed,
                DeletedFiles = deleted,
                RemainingBytes = plan.ExistingBytes,
                ProtectedBytes = plan.ProtectedBytes,
                UnknownBytes = plan.UnknownBytes,
                UnknownFiles = plan.UnknownFiles,
                UnmeasuredEntries = plan.UnmeasuredEntries,
                UnmetQuotaBytes = Math.Max(0, plan.ExistingBytes - plan.Options.MaxCacheBytes),
                Messages = messages
            };
        }

        private static async ResourceOperationBase<ResourceCacheMaintenancePlan> ScanCoreAsync(string root, ResourceCacheMaintenanceOptions options, MaintenanceWork work)
        {
            var plan = new ResourceCacheMaintenancePlan { CacheRoot = root, Options = options, Status = ResourceCacheMaintenanceStatus.Ready };
            var messages = new List<string>();
            var files = new Dictionary<string, FileState>(DownloadStorage.PathComparer);
            try {
                var pending = new Stack<string>(); pending.Push(root);
                while (pending.Count != 0) {
                    await work.Checkpoint();
                    var directory = pending.Pop();
                    DownloadStorage.RejectLinks(directory);
                    // 每个缓存根有独立租约和相对路径版本保护。父目录的锁不能授权维护子缓存。
                    if (!DownloadStorage.PathComparer.Equals(directory, root) &&
                        (File.Exists(Path.Combine(directory, CacheUsageLease.FileName)) ||
                         File.Exists(Path.Combine(directory, "zrasset-host.json")))) {
                        plan.Status = ResourceCacheMaintenanceStatus.Blocked; plan.UnmeasuredEntries++;
                        messages.Add("发现独立子缓存，请对其实际 CacheRoot 单独维护：" + directory);
                        continue;
                    }
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory)) {
                        await work.Checkpoint();
                        FileAttributes attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) {
                            plan.UnmeasuredEntries++; plan.Status = ResourceCacheMaintenanceStatus.Blocked;
                            messages.Add("发现链接或重解析点，未遍历且停止清理：" + path); continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                        var info = new FileInfo(path);
                        var file = new FileState { Path = path, Relative = Relative(root, path), Bytes = info.Length, Modified = info.LastWriteTimeUtc };
                        files.Add(path, file); plan.ExistingBytes = checked(plan.ExistingBytes + file.Bytes);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is OverflowException) {
                plan.Status = ResourceCacheMaintenanceStatus.Blocked; plan.UnmeasuredEntries++;
                messages.Add("无法完整读取缓存目录，停止清理：" + exception.Message);
            }

            // 目录身份遵循宿主文件系统规则；Windows 的 V1 与 v1 必须保护同一物理版本。
            var protectedVersions = new HashSet<string>(DownloadStorage.PathComparer);
            var owners = new Dictionary<string, Owner>(DownloadStorage.PathComparer);
            var unusedManifests = new List<FileState>();
            var known = new HashSet<string>(DownloadStorage.PathComparer);
            var protectedFiles = new HashSet<string>(DownloadStorage.PathComparer);
            foreach (FileState file in files.Values) {
                await work.Checkpoint();
                if (DownloadStorage.PathComparer.Equals(file.Relative, CacheUsageLease.FileName)) { known.Add(file.Path); protectedFiles.Add(file.Path); }
                var parts = file.Relative.Split('/');
                if (parts.Length >= 3 && DownloadStorage.PathComparer.Equals(parts[0], "ZRAssetVersions")) {
                    // 版本事务元数据始终保留，避免破坏后续恢复、回滚和“版本号不可复用”的约束。
                    if (DownloadStorage.PathComparer.Equals(parts[parts.Length - 1], "manifest.json") || DownloadStorage.PathComparer.Equals(parts[parts.Length - 1], "active.json") ||
                        DownloadStorage.PathComparer.Equals(parts[parts.Length - 1], "active.backup.json") || DownloadStorage.PathComparer.Equals(parts[parts.Length - 1], "update.lock")) { known.Add(file.Path); protectedFiles.Add(file.Path); }
                    if (parts.Length == 3 && (DownloadStorage.PathComparer.Equals(parts[2], "active.json") || DownloadStorage.PathComparer.Equals(parts[2], "active.backup.json"))) {
                        try {
                            await ValidatePointerAsync(file.Path, root, parts[1], protectedVersions, work);
                        }
                        catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is ArgumentException || exception is InvalidOperationException) { plan.Status = ResourceCacheMaintenanceStatus.Blocked; messages.Add("版本指针无法验证，停止清理：" + exception.Message); }
                    }
                }
            }

            foreach (FileState file in files.Values) {
                await work.Checkpoint();
                var parts = file.Relative.Split('/');
                if (parts.Length == 5 && DownloadStorage.PathComparer.Equals(parts[0], "ZRAssetVersions") && DownloadStorage.PathComparer.Equals(parts[2], "versions") && DownloadStorage.PathComparer.Equals(parts[4], "manifest.json")) {
                    try {
                        if (!DownloadStorage.IsSafeSegment(parts[1]) || !DownloadStorage.IsSafeSegment(parts[3])) {
                            continue;
                        }

                        if (file.Bytes > 16 * 1024 * 1024) {
                            throw new InvalidDataException("版本清单过大。");
                        }

                        ResourceManifest manifest = await work.ReadManifest(file.Path);
                        if (!DownloadStorage.PathComparer.Equals(manifest.BuildTarget, parts[1])) {
                            throw new InvalidDataException("版本清单平台不匹配。");
                        }

                        if (options.ClearUnusedManifests && !protectedVersions.Contains(parts[1] + "/" + parts[3])) {
                            unusedManifests.Add(file);
                        }

                        foreach (BundleInfo bundle in manifest.Bundles) {
                            await work.Checkpoint();
                            if (!DownloadStorage.IsSha256(bundle.Sha256) || bundle.Size < 0 || (bundle.Size == 0 && bundle.FileType != ResourceFileType.RawFile)) {
                                continue;
                            }

                            var destination = DownloadStorage.ValidatePath(root, Path.Combine(root, parts[3], parts[1], bundle.Sha256.ToLowerInvariant(), bundle.Name));
                            AddOwner(destination, null, protectedVersions.Contains(parts[1] + "/" + parts[3]));
                        }
                    }
                    catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is ArgumentException || exception is InvalidOperationException) { messages.Add("无法识别旧版本清单，相关未登记文件会保留：" + file.Path); }
                }
                if (file.Relative.EndsWith(CacheOwnership.Suffix, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
                    CacheOwnership.Record record = CacheOwnership.Read(file.Path);
                    if (record == null) {
                        continue;
                    }

                    var destination = file.Path.Substring(0, file.Path.Length - CacheOwnership.Suffix.Length);
                    try { DownloadStorage.ValidatePath(root, destination); }
                    catch (ArgumentException) { continue; }
                    if (!DownloadStorage.PathComparer.Equals(Path.GetFileName(destination), record.Name)) {
                        continue;
                    }

                    var destinationParts = Relative(root, destination).Split('/');
                    var protect = destinationParts.Length >= 2 && protectedVersions.Contains(destinationParts[1] + "/" + destinationParts[0]);
                    AddOwner(destination, file.Path, protect);
                }
            }

            // 只有已登记目标的严格 GUID 临时名属于复制事务。先识别全部正式目标，避免合法包名碰巧长成临时名时被误归类。
            foreach (FileState file in files.Values) {
                await work.Checkpoint();
                if (owners.ContainsKey(file.Path) || !TryGetReuseDestination(file.Path, out var destination) ||
                    !owners.TryGetValue(destination, out Owner owner)) {
                    continue;
                }

                owner.ReuseTemporaries ??= new List<string>();
                owner.ReuseTemporaries.Add(file.Path);
            }

            var candidates = new Dictionary<string, ResourceCacheFile>(DownloadStorage.PathComparer);
            foreach (FileState unused in unusedManifests) { await work.Checkpoint(); protectedFiles.Remove(unused.Path); candidates[unused.Path] = Describe(unused, ResourceCacheFileKind.Manifest); }
            var bundleCandidates = new List<ResourceCacheFile>();
            DateTime now = DateTime.UtcNow;
            DateTime expiredBefore = options.PartialMaxAge >= now - DateTime.MinValue ? DateTime.MinValue : now - options.PartialMaxAge;
            foreach (Owner owner in owners.Values) {
                await work.Checkpoint();
                // 通用下载队列允许自定义目标；即使意外给事务元数据写过标记，也不能用标记覆盖元数据保护。
                owner.Protected |= protectedFiles.Contains(owner.Destination);
                owner.Protected |= options.SelectedNames != null && !options.SelectedNames.Contains(Path.GetFileName(owner.Destination));
                var payloadPaths = PayloadPaths(owner).ToArray();
                var partialGroupExpired = payloadPaths.Skip(1).Where(files.ContainsKey).All(path => files[path].Modified <= expiredBefore);
                foreach (var path in payloadPaths) {
                    if (!files.TryGetValue(path, out FileState file)) {
                        continue;
                    }

                    known.Add(path);
                    if (owner.Protected) { protectedFiles.Add(path); continue; }
                    if (path == owner.Destination) {
                        bundleCandidates.Add(Describe(file, ResourceCacheFileKind.Bundle));
                    }
                    else if (partialGroupExpired) {
                        candidates[path] = Describe(file, ResourceCacheFileKind.ExpiredPartial);
                    }
                    else {
                        protectedFiles.Add(path); // 未过期断点保留，避免频繁启动重新下载。
                    }
                }
                if (owner.Marker != null && files.TryGetValue(owner.Marker, out FileState marker)) {
                    known.Add(owner.Marker);
                    if (owner.Protected) {
                        protectedFiles.Add(owner.Marker);
                    }
                    else if (!payloadPaths.Any(files.ContainsKey)) {
                        candidates[owner.Marker] = Describe(marker, ResourceCacheFileKind.OwnershipMarker);
                    }
                }
            }
            long selectedBytes = 0;
            foreach (ResourceCacheFile candidate in candidates.Values) { await work.Checkpoint(); selectedBytes = checked(selectedBytes + candidate.Bytes); }
            ResourceCacheFile[] orderedBundles = await work.Sort(bundleCandidates, CompareFiles);
            foreach (ResourceCacheFile bundle in orderedBundles) {
                await work.Checkpoint();
                if (plan.ExistingBytes - selectedBytes <= options.MaxCacheBytes) {
                    break;
                }

                candidates[bundle.Path] = bundle; selectedBytes += bundle.Bytes;
            }
            // 标记最后删除；若该目标还有保留的有效包或断点，则继续保留标记。
            foreach (Owner owner in owners.Values) {
                await work.Checkpoint();
                if (owner.Protected || owner.Marker == null || !files.TryGetValue(owner.Marker, out FileState marker)) {
                    continue;
                }

                var hasKeptPayload = PayloadPaths(owner).Any(path => files.ContainsKey(path) && !candidates.ContainsKey(path));
                if (!hasKeptPayload) {
                    candidates[owner.Marker] = Describe(marker, ResourceCacheFileKind.OwnershipMarker);
                }
            }
            foreach (var path in protectedFiles) { await work.Checkpoint(); if (files.TryGetValue(path, out FileState file)) { plan.ProtectedBytes = checked(plan.ProtectedBytes + file.Bytes); } }
            foreach (FileState file in files.Values) {
                await work.Checkpoint();
                if (!known.Contains(file.Path)) { plan.UnknownFiles++; plan.UnknownBytes += file.Bytes; }
            }
            if (plan.Status == ResourceCacheMaintenanceStatus.Ready) {
                ResourceCacheFile[] ordered = await work.Sort(candidates.Values, (left, right) =>
                {
                    var kind = (left.Kind == ResourceCacheFileKind.OwnershipMarker).CompareTo(right.Kind == ResourceCacheFileKind.OwnershipMarker);
                    return kind == 0 ? CompareFiles(left, right) : kind;
                });
                plan.Files = Array.AsReadOnly(ordered);
                foreach (ResourceCacheFile file in ordered) { await work.Checkpoint(); plan.PlannedReclaimedBytes = checked(plan.PlannedReclaimedBytes + file.Bytes); }
            }
            plan.Messages = messages.AsReadOnly();
            return plan;

            void AddOwner(string destination, string marker, bool protect)
            {
                if (!owners.TryGetValue(destination, out Owner owner)) {
                    owners[destination] = owner = new Owner { Destination = destination };
                }

                owner.Protected |= protect;
                if (marker != null) {
                    owner.Marker = marker;
                }
            }
        }

        private static async ResourceOperationBase ValidatePointerAsync(string path, string root, string target, HashSet<string> versions, MaintenanceWork work)
        {
            if (new FileInfo(path).Length > 64 * 1024) {
                throw new InvalidDataException("版本指针过大。");
            }

            Pointer pointer = JsonUtility.FromJson<Pointer>(await work.ReadText(path, 64 * 1024));
            if (pointer == null || !DownloadStorage.IsSafeSegment(target) || !DownloadStorage.IsSafeSegment(pointer.Active) ||
                !DownloadStorage.IsSha256(pointer.ManifestSha256) || (!string.IsNullOrEmpty(pointer.Previous) && !DownloadStorage.IsSafeSegment(pointer.Previous))) {
                throw new InvalidDataException("活动或备份指针结构无效。");
            }

            var manifestPath = DownloadStorage.ValidatePath(root, Path.Combine(root, "ZRAssetVersions", target, "versions", pointer.Active, "manifest.json"));
            if (new FileInfo(manifestPath).Length > 16 * 1024 * 1024) {
                throw new InvalidDataException("活动版本清单过大。");
            }

            var json = await work.ReadText(manifestPath, 16 * 1024 * 1024);
            var hash = work.Asynchronous ? await ResourceVersionManager.HashAsync(json, work.Token) : ResourceVersionManager.ComputeTextSha256(json);
            ResourceManifest manifest = work.Asynchronous ? await ResourceManifest.FromJsonAsync(json, cancellationToken: work.Token) : ResourceManifest.FromJson(json);
            if (!string.Equals(hash, pointer.ManifestSha256, StringComparison.OrdinalIgnoreCase) ||
                !DownloadStorage.PathComparer.Equals(manifest.BuildTarget, target)) {
                throw new InvalidDataException("活动或备份指针对应的清单校验失败。");
            }

            versions.Add(target + "/" + pointer.Active);
            if (!string.IsNullOrEmpty(pointer.Previous)) {
                versions.Add(target + "/" + pointer.Previous);
            }
        }

        private static int CompareFiles(ResourceCacheFile left, ResourceCacheFile right)
        {
            var modified = left.LastWriteUtc.CompareTo(right.LastWriteUtc);
            return modified == 0 ? StringComparer.Ordinal.Compare(left.Path, right.Path) : modified;
        }

        private sealed class MaintenanceWork
        {
            private readonly System.Diagnostics.Stopwatch m_slice = System.Diagnostics.Stopwatch.StartNew();
            internal bool Asynchronous { get; }
            internal CancellationToken Token { get; }
            internal MaintenanceWork(bool asynchronous, CancellationToken token) { Asynchronous = asynchronous; Token = token; }
            internal ResourceOperationBase Checkpoint()
            {
                Token.ThrowIfCancellationRequested();
                return Asynchronous && m_slice.Elapsed.TotalMilliseconds >= 2 ? YieldAsync() : ResourceOperationBase.CompletedOperation;
            }
            private async ResourceOperationBase YieldAsync()
            { await ResourceOperationBase.Yield(); Token.ThrowIfCancellationRequested(); m_slice.Restart(); }
            internal ResourceOperationBase<string> ReadText(string path, int maximum)
            { return Asynchronous ? ResourceFileReader.ReadTextAsync(path, maximum, Token) : ResourceOperationBase.FromResult(File.ReadAllText(path)); }
            internal async ResourceOperationBase<ResourceManifest> ReadManifest(string path)
            {
                var json = await ReadText(path, 16 * 1024 * 1024);
                return Asynchronous ? await ResourceManifest.FromJsonAsync(json, cancellationToken: Token) : ResourceManifest.FromJson(json);
            }
            internal async ResourceOperationBase<ResourceCacheFile[]> Sort(ICollection<ResourceCacheFile> source, Comparison<ResourceCacheFile> comparison)
            {
                var result = new ResourceCacheFile[source.Count];
                var index = 0;
                foreach (ResourceCacheFile file in source) { await Checkpoint(); result[index++] = file; }
                if (Asynchronous) { await ResourceManifestWork.RunAsync(ResourceManifestWork.SortSteps(result, comparison), Token); }
                else { Array.Sort(result, comparison); }
                return result;
            }
        }

        private static string Relative(string root, string path)
        {
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.Substring(prefix.Length).Replace('\\', '/');
        }

        private static ResourceCacheFile Describe(FileState file, ResourceCacheFileKind kind)
        {
            return new()
            { Path = file.Path, Bytes = file.Bytes, LastWriteUtc = file.Modified, Kind = kind };
        }

        private static IEnumerable<string> PayloadPaths(Owner owner)
        {
            yield return owner.Destination;
            yield return owner.Destination + ".part";
            yield return owner.Destination + ".part.json";
            yield return owner.Destination + ".part.incoming";
            yield return owner.Destination + ".part.incoming.json";
            yield return owner.Destination + ".part.json.incoming";
            yield return owner.Destination + ".part.incoming.json.incoming";
            if (owner.ReuseTemporaries != null) {
                foreach (var temporary in owner.ReuseTemporaries) {
                    yield return temporary;
                }
            }
        }

        private static bool TryGetReuseDestination(string path, out string destination)
        {
            destination = null;
            const int suffixLength = 7 + 32 + 4; // .reuse. + GUID 的 N 格式 + .tmp
            if (path.Length <= suffixLength) {
                return false;
            }

            var start = path.Length - suffixLength;
            StringComparison comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.EndsWith(".tmp", comparison) || !path.Substring(start, 7).Equals(".reuse.", comparison)) {
                return false;
            }

            for (var index = start + 7; index < start + 7 + 32; index++) {
                if (!Uri.IsHexDigit(path[index])) {
                    return false;
                }
            }

            destination = path.Substring(0, start);
            return true;
        }

        private static bool HasReuseTemporary(string destination)
        {
            var directory = Path.GetDirectoryName(destination);
            DownloadStorage.RejectLinks(directory);
            if (!Directory.Exists(directory)) {
                return false;
            }
            // 仅在删除某个标记前检查同目录；不使用宽泛 glob，更不会递归到其他目录。
            foreach (var path in Directory.EnumerateFiles(directory)) {
                if (TryGetReuseDestination(path, out var owner) && DownloadStorage.PathComparer.Equals(owner, destination)) {
                    return true;
                }
            }

            return false;
        }
    }
}

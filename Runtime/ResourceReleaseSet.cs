using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>一次发布的完整 Package 集合。外层签名绑定所有成员的精确清单内容。</summary>
    [Serializable]
    public sealed class ResourceReleaseSet
    {
        public int FormatVersion = 1;
        public string Name;
        public string Version;
        public string BuildTarget;
        public ResourceManifest[] Packages;

        public string Encode(IResourceKeyProvider keys = null, string keyId = null, IResourceManifestCodec codec = null)
        {
            Validate();
            return ResourceManifestEnvelope.EncodeBytes(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(this)), codec, keyId, keys);
        }

        public void Validate()
        {
            if (FormatVersion != 1 || !DownloadStorage.IsSafeSegment(Name) || !DownloadStorage.IsSafeSegment(Version) ||
                !DownloadStorage.IsSafeSegment(BuildTarget) || Packages == null || Packages.Length == 0 || Packages.Length > 128) {
                throw new InvalidDataException("联合发布描述无效。");
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ResourceManifest manifest in Packages) {
                if (manifest == null || manifest.BuildTarget != BuildTarget || !names.Add(manifest.PackageName ?? "")) {
                    throw new InvalidDataException("联合发布的平台或 Package 身份冲突。");
                }
                ResourcePackageIdentity.ValidateManifest(manifest, manifest.PackageName);
                if (manifest.FormatVersion < 3) {
                    throw new InvalidDataException("联合发布必须使用命名 Package 清单。");
                }
            }
        }
    }

    /// <summary>
    /// 独占集合缓存目录。所有成员先准备和校验，再提交唯一集合指针；不写成员 active.json。
    /// 切换期间资源入口关闭，调用方必须先交回所有旧资源凭证。须由 Unity 主线程使用。
    /// </summary>
    public sealed partial class ResourceReleaseSetManager
    {
        [Serializable]
        private sealed class Pointer
        {
            public int Format = 1;
            public string ActiveJson;
            public string ActiveSha256;
            public string PreviousJson;
            public string PreviousSha256;
            public long AcceptedSequence = -1;
            public string AcceptedSha256;
        }

        [Serializable]
        private sealed class Accepted
        {
            public long Sequence;
            public string Sha256;
        }

        private readonly string m_name;
        private readonly string m_buildTarget;
        private readonly Func<string, string, BundleDownloadOptions> m_downloads;
        private readonly Func<string, string> m_builtInRoots;
        private readonly IResourceDecryptionServices m_decryption;
        private readonly IResourceKeyProvider m_manifestKeys;
        private readonly IResourceManifestCodec m_manifestCodec;
        private readonly bool m_encodedBuiltInManifest;
        private readonly ResourceDownloadPolicy m_networkPolicy;
        private readonly ResourceFileLock m_lock;
        private readonly Dictionary<string, ResourceVersionManager> m_versions = new(StringComparer.Ordinal);
        private Dictionary<string, ResourceManager> m_resources = new(StringComparer.Ordinal);
        private Pointer m_pointer;
        private Accepted m_accepted;
        private string m_activeVersion;
        private bool m_disposed;

        public bool IsBusy { get; private set; }
        public string ActiveVersion { get { Check(); return m_activeVersion; } }
        public string CacheRoot { get; }

        public ResourceReleaseSetManager(string name, string buildTarget, string cacheRoot,
            Func<string, string, BundleDownloadOptions> downloads, Func<string, string> builtInRoots = null,
            IResourceDecryptionServices decryptionServices = null, IResourceKeyProvider manifestKeys = null, IResourceManifestCodec manifestCodec = null,
            bool encodedBuiltInManifest = false, ResourceDownloadPolicy networkPolicy = null)
            : this(name, buildTarget, cacheRoot, downloads, builtInRoots, decryptionServices, manifestKeys, manifestCodec,
                encodedBuiltInManifest, networkPolicy, true)
        { }

        private ResourceReleaseSetManager(string name, string buildTarget, string cacheRoot,
            Func<string, string, BundleDownloadOptions> downloads, Func<string, string> builtInRoots,
            IResourceDecryptionServices decryptionServices, IResourceKeyProvider manifestKeys, IResourceManifestCodec manifestCodec,
            bool encodedBuiltInManifest, ResourceDownloadPolicy networkPolicy, bool recover)
        {
            ResourcePackages.CheckThread();
            ResourcePlatform.RequireVersionTransactions();
            if (!DownloadStorage.IsSafeSegment(name) || !DownloadStorage.IsSafeSegment(buildTarget)) {
                throw new ArgumentException("集合名称和平台必须是安全的单层目录名。");
            }
            m_downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
            m_name = name;
            m_buildTarget = buildTarget;
            m_builtInRoots = builtInRoots;
            m_decryption = decryptionServices;
            m_manifestKeys = manifestKeys;
            m_manifestCodec = manifestCodec;
            m_encodedBuiltInManifest = encodedBuiltInManifest;
            m_networkPolicy = networkPolicy;
            CacheRoot = DownloadStorage.ValidatePath(cacheRoot, Path.Combine(cacheRoot, "ZRAssetSets", name, buildTarget));
            Directory.CreateDirectory(CacheRoot);
            ResourcePersistence.RegisterCacheRoot(CacheRoot);
            m_lock = ResourceFileLock.Open(Path.Combine(CacheRoot, "set.lock"));
            if (!recover) { return; }
            try {
                m_pointer = Recover();
                m_activeVersion = m_pointer == null ? null : ReadSet(m_pointer.ActiveJson, m_pointer.ActiveSha256).Version;
                var acceptedPath = Path.Combine(CacheRoot, "accepted.json");
                if (File.Exists(acceptedPath)) {
                    if (new FileInfo(acceptedPath).Length > 4096) {
                        throw new InvalidDataException("联合发布序号记录过大。");
                    }

                    m_accepted = JsonUtility.FromJson<Accepted>(File.ReadAllText(acceptedPath));
                    if (m_accepted == null || m_accepted.Sequence < 0 || !DownloadStorage.IsSha256(m_accepted.Sha256) ||
                        m_accepted.Sequence < (m_pointer?.AcceptedSequence ?? -1)) {
                        throw new InvalidDataException("联合发布序号记录损坏。");
                    }
                }
                else if (m_pointer?.AcceptedSequence >= 0) {
                    throw new InvalidDataException("联合发布序号记录缺失。");
                }
            }
            catch {
                m_lock.Dispose();
                throw;
            }
        }

        public ResourceManager GetPackage(string name)
        {
            Check();
            return m_resources.TryGetValue(name, out ResourceManager manager) ? manager :
                throw new InvalidOperationException("Package 尚未加载，先调用 LoadActiveAsync 或 ActivateAsync。");
        }

        /// <summary>仅适用于本地受信任清单。网络更新使用 ActivateSignedAsync。</summary>
        public async ResourceOperationBase ActivateAsync(ResourceReleaseSet release, CancellationToken token = default)
        {
            Check();
            token.ThrowIfCancellationRequested();
            IsBusy = true;
            try { await ChangeAsync(await SnapshotAsync(release, token), null, null, false, token, alreadyBusy: true); }
            finally { IsBusy = false; }
        }

        public async ResourceOperationBase ActivateSignedAsync(string releaseUrl, ResourceReleaseTrustOptions trust,
            string playerBuildId = null, CancellationToken token = default)
        {
            Check();
            if (trust == null) {
                throw new ArgumentNullException(nameof(trust));
            }

            token.ThrowIfCancellationRequested();
            IsBusy = true;
            try {
                Uri source = trust.ValidateSource(releaseUrl);
                var envelope = await ResourceVersionManager.ReadBoundedReleaseTextAsync(source, 64 * 1024, 30, token, m_networkPolicy);
                ResourceSignedReleasePayload payload = ResourceReleaseAuthentication.Verify(envelope, trust, DateTimeOffset.UtcNow);
                if (payload.Release.BuildTarget != m_buildTarget || (playerBuildId != null && payload.PlayerBuildId != playerBuildId)) {
                    throw new InvalidDataException("联合发布平台或宿主身份不匹配。");
                }
                CheckSequence(payload);
                if (!Uri.TryCreate(source, payload.Release.ManifestUrl, out Uri catalog) || catalog.Scheme != source.Scheme ||
                    catalog.Host != source.Host || catalog.Port != source.Port || !string.IsNullOrEmpty(catalog.UserInfo)) {
                    throw new InvalidDataException("联合发布清单必须同源。");
                }
                var json = await ResourceVersionManager.ReadBoundedReleaseTextAsync(catalog, trust.MaximumManifestBytes, 30, token, m_networkPolicy);
                ResourceReleaseSet set = await ReadSetAsync(json, payload.Release.ManifestSha256, token);
                if (set.Version != payload.Release.Version) {
                    throw new InvalidDataException("集合版本与签名不一致。");
                }

                await ChangeAsync(set, envelope, trust, false, token, alreadyBusy: true);
            }
            finally {
                IsBusy = false;
            }
        }

        public async ResourceOperationBase RollbackAsync(CancellationToken token = default)
        {
            Check();
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(m_pointer?.PreviousJson)) { throw new InvalidOperationException("没有可回滚的联合发布。"); }
            IsBusy = true;
            try { await ChangeAsync(await ReadSetAsync(m_pointer.PreviousJson, m_pointer.PreviousSha256, token), null, null, false, token, true); }
            finally { IsBusy = false; }
        }

        public async ResourceOperationBase LoadActiveAsync(CancellationToken token = default)
        {
            Check();
            token.ThrowIfCancellationRequested();
            if (m_pointer == null) { throw new InvalidOperationException("尚无已提交的联合发布。"); }
            IsBusy = true;
            try { await ChangeAsync(await ReadSetAsync(m_pointer.ActiveJson, m_pointer.ActiveSha256, token), null, null, true, token, true); }
            finally { IsBusy = false; }
        }

        private async ResourceOperationBase ChangeAsync(ResourceReleaseSet set, string envelope, ResourceReleaseTrustOptions trust,
            bool loadOnly, CancellationToken token, bool alreadyBusy = false)
        {
            if (!alreadyBusy) {
                Check();
            }

            IsBusy = true;
            var nextResources = new Dictionary<string, ResourceManager>(StringComparer.Ordinal);
            var oldClosed = false;
            var committed = false;
            try {
                // 先下载，不阻塞旧版 Handle 使用。集合入口在整个操作期间禁止取得新的管理器。
                foreach (ResourceManifest manifest in set.Packages) {
                    token.ThrowIfCancellationRequested();
                    ResourceVersionManager versions = await VersionsAsync(manifest.PackageName, manifest.PackageVersion, token);
                    await versions.PrepareAsync(manifest.PackageVersion, manifest, cancellationToken: token);
                    nextResources.Add(manifest.PackageName, await versions.OpenPreparedAsync(manifest, token));
                }
                foreach (ResourceManager manager in m_resources.Values) {
                    manager.EnsureCanDispose();
                }

                token.ThrowIfCancellationRequested();
                var setJson = await ResourceManifestWork.ValueAsync(() => JsonUtility.ToJson(set), token);
                if (await ResourceManifestWork.ValueAsync(() => System.Text.Encoding.UTF8.GetByteCount(setJson), token) > 8 * 1024 * 1024) {
                    throw new InvalidDataException("联合发布清单过大。");
                }

                var savedSet = DownloadStorage.ValidatePath(CacheRoot, Path.Combine(CacheRoot, "sets", set.Version + ".json"));
                if (File.Exists(savedSet) && await ResourceFileReader.ReadTextAsync(savedSet, 8 * 1024 * 1024, token) != setJson) {
                    throw new InvalidDataException("联合发布版本不可变，请使用新版本号。");
                }
                if (!System.IO.File.Exists(savedSet)) {
                    await ResourceFileIO.Shared.WriteAtomicAsync(savedSet, setJson);
                }

                ResourceSignedReleasePayload payload = envelope == null ? null : ResourceReleaseAuthentication.Verify(envelope, trust, DateTimeOffset.UtcNow);
                if (payload != null) {
                    CheckSequence(payload);
                }
                // 最终检查与全部入口封锁同步完成；任一成员拒绝时整组仍可使用。
                ResourceOperationBase closing = ResourceManager.DisposeTogether(m_resources.Values);
                oldClosed = true;
                await closing;
                token.ThrowIfCancellationRequested();
                if (payload != null) {
                    payload = ResourceReleaseAuthentication.Verify(envelope, trust, DateTimeOffset.UtcNow);
                    var accepted = new Accepted { Sequence = payload.Sequence, Sha256 = payload.Release.ManifestSha256 };
                    await ResourceFileIO.Shared.WriteAtomicAsync(Path.Combine(CacheRoot, "accepted.json"), JsonUtility.ToJson(accepted));
                    m_accepted = accepted;
                    await ResourcePersistence.FlushAsync();
                }
                if (!loadOnly) {
                    var json = setJson;
                    var next = new Pointer
                    {
                        ActiveJson = json,
                        ActiveSha256 = await ResourceVersionManager.HashAsync(json, token),
                        PreviousJson = m_pointer?.ActiveJson,
                        PreviousSha256 = m_pointer?.ActiveSha256,
                        AcceptedSequence = payload?.Sequence ?? m_pointer?.AcceptedSequence ?? -1,
                        AcceptedSha256 = payload?.Release.ManifestSha256 ?? m_pointer?.AcceptedSha256
                    };
                    await CommitAsync(next);
                    m_pointer = next;
                    m_activeVersion = set.Version;
                }
                m_resources = nextResources;
                committed = true;
            }
            catch {
                if (oldClosed) {
                    m_resources.Clear();
                    // 持久化失败时恢复整组旧版本；重开失败则保持入口关闭，允许显式 LoadActiveAsync 重试。
                    if (m_pointer != null) {
                        try {
                            ResourceReleaseSet old = await ReadSetAsync(m_pointer.ActiveJson, m_pointer.ActiveSha256, default);
                            foreach (ResourceManifest manifest in old.Packages) {
                                ResourceVersionManager versions = await VersionsAsync(manifest.PackageName, manifest.PackageVersion, default);
                                m_resources.Add(manifest.PackageName, await versions.OpenPreparedAsync(manifest, default));
                            }
                        }
                        catch {
                            await CloseAllAsync(m_resources);
                            m_resources.Clear();
                        }
                    }
                }
                throw;
            }
            finally {
                try {
                    if (!committed) {
                        await CloseAllAsync(nextResources);
                    }
                }
                finally { IsBusy = false; }
            }
        }

        private async ResourceOperationBase<ResourceVersionManager> VersionsAsync(string name, string version, CancellationToken token)
        {
            var key = name + "/" + version;
            if (m_versions.TryGetValue(key, out ResourceVersionManager result)) {
                return result;
            }

            BundleDownloadOptions source = m_downloads(name, version) ?? throw new InvalidOperationException("缺少 Package 下载配置。");
            var scoped = new BundleDownloadOptions(source.RemoteBaseUrl, source.CacheVersion,
                DownloadStorage.ValidatePath(CacheRoot, Path.Combine(CacheRoot, "p", name)), source.MaxConcurrentDownloads, source.RequestTimeoutSeconds,
                source.RetryPolicy, source.DiskPolicy, source.NetworkPolicy, source.SourceOptions);
            result = await ResourceVersionManager.CreateAsync(m_buildTarget, scoped, m_builtInRoots?.Invoke(name) ?? "", name,
                m_decryption, m_manifestKeys, m_manifestCodec, m_encodedBuiltInManifest, token);
            m_versions.Add(key, result);
            return result;
        }

        private async ResourceOperationBase CommitAsync(Pointer next)
        {
            var path = Path.Combine(CacheRoot, "active.json");
            var backup = Path.Combine(CacheRoot, "active.backup.json");
            var old = File.Exists(path) ? await ResourceFileReader.ReadTextAsync(path, 32 * 1024 * 1024) : null;
            var oldBackup = File.Exists(backup) ? await ResourceFileReader.ReadTextAsync(backup, 32 * 1024 * 1024) : null;
            var json = await ResourceManifestWork.ValueAsync(() => JsonUtility.ToJson(next), default);
            if (old != null) { await ResourceFileIO.Shared.WriteAtomicAsync(backup, old); }
            await ResourceFileIO.Shared.WriteAtomicAsync(path, json);
            try {
                await ResourcePersistence.FlushAsync();
            }
            catch {
                Restore(path, old);
                Restore(backup, oldBackup);
                try { await ResourcePersistence.FlushAsync(); } catch { }
                throw;
            }
        }

        private Pointer Recover()
        {
            var path = Path.Combine(CacheRoot, "active.json");
            var backup = Path.Combine(CacheRoot, "active.backup.json");
            if (!File.Exists(path) && !File.Exists(backup)) {
                return null;
            }

            foreach (var candidate in new[] { path, backup }) {
                try {
                    if (!File.Exists(candidate) || new FileInfo(candidate).Length > 32 * 1024 * 1024) {
                        continue;
                    }

                    Pointer value = JsonUtility.FromJson<Pointer>(File.ReadAllText(candidate));
                    if (value == null || value.Format != 1 || value.AcceptedSequence < -1 ||
                        (value.AcceptedSequence >= 0 && !DownloadStorage.IsSha256(value.AcceptedSha256))) {
                        continue;
                    }

                    ReadSet(value.ActiveJson, value.ActiveSha256);
                    if (!string.IsNullOrEmpty(value.PreviousJson)) {
                        ReadSet(value.PreviousJson, value.PreviousSha256);
                    }

                    if (candidate != path) {
                        ResourceVersionManager.WriteAtomic(path, JsonUtility.ToJson(value));
                    }

                    return value;
                }
                catch (ArgumentException) { }
                catch (InvalidDataException) { }
            }
            throw new InvalidDataException("联合发布指针及备份损坏。");
        }

        private ResourceReleaseSet ReadSet(string json, string hash)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 8 * 1024 * 1024 || !DownloadStorage.IsSha256(hash) ||
                !string.Equals(ResourceVersionManager.ComputeTextSha256(json), hash, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("联合发布清单校验失败。");
            }
            if (ResourceManifestEnvelope.IsEnvelope(json)) {
                json = new System.Text.UTF8Encoding(false, true).GetString(ResourceManifestEnvelope.DecodeBytes(json, m_manifestKeys, m_manifestCodec));
                if (System.Text.Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024) {
                    throw new InvalidDataException("联合发布解码清单过大。");
                }
            }
            return Snapshot(JsonUtility.FromJson<ResourceReleaseSet>(json));
        }

        private ResourceReleaseSet Snapshot(ResourceReleaseSet set)
        {
            if (set == null) {
                throw new ArgumentNullException(nameof(set));
            }

            set.Validate();
            return set.Name != m_name || set.BuildTarget != m_buildTarget
                ? throw new InvalidDataException("联合发布身份不匹配。")
                : new ResourceReleaseSet
                {
                    Name = set.Name,
                    Version = set.Version,
                    BuildTarget = set.BuildTarget,
                    Packages = set.Packages.Select(manifest => manifest.Clone()).ToArray()
                };
        }

        private void CheckSequence(ResourceSignedReleasePayload payload)
        {
            if (payload.Sequence < (m_accepted?.Sequence ?? -1) || (payload.Sequence == m_accepted?.Sequence &&
                !string.Equals(payload.Release.ManifestSha256, m_accepted.Sha256, StringComparison.OrdinalIgnoreCase))) {
                throw new InvalidDataException("联合发布序号回退或同序号内容冲突。");
            }
        }

        private static void Restore(string path, string value)
        {
            if (value == null) {
                DownloadStorage.DeleteFile(path);
            }
            else {
                ResourceVersionManager.WriteAtomic(path, value);
            }
        }

        private static async ResourceOperationBase CloseAllAsync(Dictionary<string, ResourceManager> resources)
        {
            await ResourceManager.DisposeTogether(resources.Values);
        }

        public async ResourceOperationBase DisposeAsync()
        {
            if (m_disposed) {
                return;
            }

            Check();
            foreach (ResourceManager manager in m_resources.Values) {
                manager.EnsureCanDispose();
            }

            IsBusy = true;
            try {
                await CloseAllAsync(m_resources);
                m_resources.Clear();
                m_disposed = true;
                m_lock.Dispose();
            }
            finally {
                IsBusy = false;
            }
        }

        private void Check()
        {
            ResourcePackages.CheckThread();
            if (m_disposed) {
                throw new ObjectDisposedException(nameof(ResourceReleaseSetManager));
            }

            if (IsBusy) {
                throw new InvalidOperationException("联合发布操作正在进行。");
            }
        }
    }
}

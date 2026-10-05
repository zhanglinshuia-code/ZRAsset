using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    public sealed partial class ResourceReleaseSetManager
    {
        /// <summary>异步恢复联合发布。原构造函数保留同步兼容；大型集合应使用此入口。</summary>
        public static async ResourceOperationBase<ResourceReleaseSetManager> CreateAsync(string name, string buildTarget, string cacheRoot,
            Func<string, string, BundleDownloadOptions> downloads, Func<string, string> builtInRoots = null,
            IResourceDecryptionServices decryptionServices = null, IResourceKeyProvider manifestKeys = null,
            IResourceManifestCodec manifestCodec = null, bool encodedBuiltInManifest = false,
            ResourceDownloadPolicy networkPolicy = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manager = new ResourceReleaseSetManager(name, buildTarget, cacheRoot, downloads, builtInRoots,
                decryptionServices, manifestKeys, manifestCodec, encodedBuiltInManifest, networkPolicy, false);
            try {
                await manager.RecoverAsync(cancellationToken);
                return manager;
            }
            catch { manager.m_lock.Dispose(); throw; }
        }

        private async ResourceOperationBase<ResourceReleaseSet> SnapshotAsync(ResourceReleaseSet source, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (source == null) { throw new ArgumentNullException(nameof(source)); }
            var result = new ResourceReleaseSet();
            await ResourceManifestWork.RunAsync(CopySteps(), token);
            return result;

            IEnumerable<int> CopySteps()
            {
                if (source.FormatVersion != 1 || source.Name != m_name || source.BuildTarget != m_buildTarget ||
                    !DownloadStorage.IsSafeSegment(source.Version) || source.Packages == null ||
                    source.Packages.Length == 0 || source.Packages.Length > 128) {
                    throw new InvalidDataException("联合发布身份或格式无效。");
                }
                result.Name = source.Name; result.BuildTarget = source.BuildTarget; result.Version = source.Version;
                result.Packages = new ResourceManifest[source.Packages.Length];
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < result.Packages.Length; i++) {
                    ResourceManifest member = source.Packages[i] ?? throw new InvalidDataException("联合发布成员为空。");
                    foreach (var step in member.CopySteps(value => result.Packages[i] = value)) { yield return step; }
                    member = result.Packages[i];
                    ResourcePackageIdentity.ValidateIdentity(member, member.PackageName);
                    if (member.BuildTarget != m_buildTarget || !names.Add(member.PackageName)) {
                        throw new InvalidDataException("联合发布成员的平台或身份冲突。");
                    }
                    foreach (var step in member.ValidateSteps()) { yield return step; }
                }
            }
        }

        private async ResourceOperationBase<ResourceReleaseSet> ReadSetAsync(string json, string hash, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(json) || json.Length > 8 * 1024 * 1024 || !DownloadStorage.IsSha256(hash) ||
                !string.Equals(await ResourceVersionManager.HashAsync(json, token), hash, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("联合发布清单校验失败。");
            }
            if (ResourceManifestEnvelope.IsEnvelope(json)) {
                var bytes = await ResourceManifestEnvelope.DecodeBytesAsync(json, m_manifestKeys, m_manifestCodec, token);
                if (bytes.Length > 8 * 1024 * 1024) { throw new InvalidDataException("联合发布解码清单过大。"); }
                json = await ResourceManifestWork.ValueAsync(() => new System.Text.UTF8Encoding(false, true).GetString(bytes), token);
            }
            return await SnapshotAsync(await ResourceManifestWork.ValueAsync(() => JsonUtility.FromJson<ResourceReleaseSet>(json), token), token);
        }

        private async ResourceOperationBase RecoverAsync(CancellationToken token)
        {
            var path = Path.Combine(CacheRoot, "active.json");
            var backup = Path.Combine(CacheRoot, "active.backup.json");
            if (File.Exists(path) || File.Exists(backup)) {
                foreach (var candidate in new[] { path, backup }) {
                    try {
                        if (!File.Exists(candidate)) { continue; }
                        var json = await ResourceFileReader.ReadTextAsync(candidate, 32 * 1024 * 1024, token);
                        Pointer value = await ResourceManifestWork.ValueAsync(() => JsonUtility.FromJson<Pointer>(json), token);
                        if (value == null || value.Format != 1 || value.AcceptedSequence < -1 ||
                            (value.AcceptedSequence >= 0 && !DownloadStorage.IsSha256(value.AcceptedSha256))) { continue; }
                        ResourceReleaseSet active = await ReadSetAsync(value.ActiveJson, value.ActiveSha256, token);
                        if (!string.IsNullOrEmpty(value.PreviousJson)) { await ReadSetAsync(value.PreviousJson, value.PreviousSha256, token); }
                        token.ThrowIfCancellationRequested();
                        if (candidate != path) { await ResourceFileIO.Shared.WriteAtomicAsync(path, json); }
                        m_pointer = value; m_activeVersion = active.Version;
                        break;
                    }
                    catch (ArgumentException) { }
                    catch (InvalidDataException) { }
                }
                if (m_pointer == null) { throw new InvalidDataException("联合发布指针及备份损坏。"); }
            }
            var acceptedPath = Path.Combine(CacheRoot, "accepted.json");
            if (File.Exists(acceptedPath)) {
                var json = await ResourceFileReader.ReadTextAsync(acceptedPath, 4096, token);
                m_accepted = JsonUtility.FromJson<Accepted>(json);
                if (m_accepted == null || m_accepted.Sequence < 0 || !DownloadStorage.IsSha256(m_accepted.Sha256) ||
                    m_accepted.Sequence < (m_pointer?.AcceptedSequence ?? -1)) {
                    throw new InvalidDataException("联合发布序号记录损坏。");
                }
            }
            else if (m_pointer?.AcceptedSequence >= 0) { throw new InvalidDataException("联合发布序号记录缺失。"); }
            token.ThrowIfCancellationRequested();
        }
    }
}

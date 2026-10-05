using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    public sealed partial class ResourceVersionManager
    {
        /// <summary>Compares an independent target snapshot against the current on-disk active version.</summary>
        public async ResourceOperationBase<ResourceVersionDiff> CompareAsync(ResourceManifest target, CancellationToken cancellationToken = default)
        {
            CheckThread();
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            await EnterAsync(cancellationToken);
            try { return await CompareOwnedAsync(await ValidateTargetAsync(target, null, cancellationToken), cancellationToken); }
            finally { Exit(); }
        }

        private async ResourceOperationBase<ResourceVersionDiff> CompareOwnedAsync(ResourceManifest target, CancellationToken token)
        {
            ResourceVersionDiff result = null;
            await ResourceManifestWork.RunAsync(DiffSteps(m_activeManifest, target, value => result = value), token);
            return result;
        }

        private static IEnumerable<int> DiffSteps(ResourceManifest previous, ResourceManifest target, Action<ResourceVersionDiff> publish)
        {
            var oldBundles = new Dictionary<string, BundleInfo>(previous?.Bundles.Length ?? 0, StringComparer.Ordinal);
            var oldAssets = new Dictionary<string, AssetInfo>(previous?.Assets.Length ?? 0, StringComparer.Ordinal);
            foreach (BundleInfo bundle in previous?.Bundles ?? Array.Empty<BundleInfo>()) { oldBundles.Add(bundle.Name, bundle); yield return 0; }
            foreach (AssetInfo asset in previous?.Assets ?? Array.Empty<AssetInfo>()) { oldAssets.Add(asset.Address, asset); yield return 0; }
            var download = new List<string>(); var reused = new List<string>(); var removedBundles = new List<string>();
            var added = new List<string>(); var changed = new List<string>(); var removed = new List<string>();
            long bytes = 0;
            foreach (BundleInfo bundle in target.Bundles) {
                if (oldBundles.TryGetValue(bundle.Name, out BundleInfo old) && SameContent(old, bundle)) {
                    reused.Add(bundle.Name);
                }
                else { download.Add(bundle.Name); bytes = checked(bytes + bundle.Size); }
                oldBundles.Remove(bundle.Name);
                yield return 0;
            }
            foreach (var name in oldBundles.Keys) { removedBundles.Add(name); yield return 0; }
            foreach (AssetInfo asset in target.Assets) {
                if (!oldAssets.TryGetValue(asset.Address, out AssetInfo old)) {
                    added.Add(asset.Address);
                }
                else if (!SameAsset(old, asset, target.FormatVersion)) {
                    changed.Add(asset.Address);
                }

                oldAssets.Remove(asset.Address);
                yield return 0;
            }
            foreach (var address in oldAssets.Keys) { removed.Add(address); yield return 0; }
            List<string>[] lists = new[] { download, reused, removedBundles, added, changed, removed };
            var arrays = new string[lists.Length][];
            for (var i = 0; i < lists.Length; i++) {
                arrays[i] = lists[i].ToArray();
                foreach (var step in ResourceManifestWork.SortSteps(arrays[i], StringComparer.Ordinal.Compare)) {
                    yield return step;
                }
            }
            publish(new ResourceVersionDiff
            {
                DownloadBundles = arrays[0],
                ReusedBundles = arrays[1],
                RemovedBundles = arrays[2],
                AddedAddresses = arrays[3],
                ChangedAddresses = arrays[4],
                RemovedAddresses = arrays[5],
                EstimatedDownloadBytes = bytes
            });
        }

        private static bool SameAsset(AssetInfo a, AssetInfo b, int format)
        {
            return a.AssetPath == b.AssetPath && a.BundleName == b.BundleName &&
            a.Kind == b.Kind && a.FileType == b.FileType && a.FileOffset == b.FileOffset && a.FileSize == b.FileSize &&
            string.Equals(a.FileSha256 ?? "", b.FileSha256 ?? "", StringComparison.OrdinalIgnoreCase) && SameValues(a.DependencyBundles, b.DependencyBundles) &&
            (format < 3 || (string.Equals(a.Guid, b.Guid, StringComparison.OrdinalIgnoreCase) && SameValues(a.Tags, b.Tags)));
        }

        private static bool SameValues(string[] left, string[] right)
        {
            left ??= Array.Empty<string>(); right ??= Array.Empty<string>();
            if (left.Length != right.Length) {
                return false;
            }

            var equal = true;
            for (var i = 0; i < left.Length; i++) {
                if (left[i] != right[i]) { equal = false; break; }
            }

            if (equal) {
                return true;
            }
            // Preserve multiset semantics for old formats; never sort private active arrays in place.
            var a = (string[])left.Clone(); var b = (string[])right.Clone();
            Array.Sort(a, StringComparer.Ordinal); Array.Sort(b, StringComparer.Ordinal);
            for (var i = 0; i < a.Length; i++) {
                if (a[i] != b[i]) {
                    return false;
                }
            }

            return true;
        }

        private async ResourceOperationBase<ResourceManifest> ValidateTargetAsync(ResourceManifest target, string version, CancellationToken token)
        {
            return target == null
                ? throw new ArgumentNullException(nameof(target))
                : await ValidateOwnedTargetAsync(await target.CloneAsync(token), version, token);
        }

        private async ResourceOperationBase<ResourceManifest> ParseTargetAsync(string json, string version, CancellationToken token,
            bool encoded = false)
        {
            ResourceManifest manifest = await ResourceManifest.FromJsonAsync(json, encoded ? m_manifestKeys : null,
                encoded ? m_manifestCodec : null, token);
            return await ValidateOwnedTargetAsync(manifest, version, token);
        }

        private async ResourceOperationBase<ResourceManifest> ValidateOwnedTargetAsync(ResourceManifest manifest, string version, CancellationToken token)
        {
            await ResourceManifestWork.RunAsync(ValidateIdentitySteps(), token);
            return manifest;
            IEnumerable<int> ValidateIdentitySteps()
            {
                if (PackageName != null) {
                    ResourcePackageIdentity.ValidateIdentity(manifest, PackageName, version);
                }
                else if (manifest.FormatVersion >= 3) {
                    throw new InvalidDataException("V3 清单需要绑定 Package 身份的版本管理器。");
                }

                if (!string.Equals(manifest.BuildTarget, m_buildTarget, StringComparison.Ordinal)) {
                    throw new InvalidDataException($"目标清单平台与版本目录不一致：{manifest.BuildTarget} != {m_buildTarget}");
                }

                foreach (BundleInfo bundle in manifest.Bundles) { BundleDownloadQueue.ValidateInfo(bundle); yield return 0; }
            }
        }

        private static ResourceOperationBase<string> SerializeAsync(ResourceManifest manifest, CancellationToken token)
        {
            return ResourceManifestWork.ValueAsync(() => JsonUtility.ToJson(manifest), token);
        }

        internal static async ResourceOperationBase<string> HashAsync(string text, CancellationToken token)
        {
            string result = null;
            await ResourceManifestWork.RunAsync(HashSteps(), token);
            return result;
            IEnumerable<int> HashSteps()
            {
                using var sha = SHA256.Create();
                Encoder encoder = Encoding.UTF8.GetEncoder();
                var chars = new char[4096];
                var bytes = new byte[Encoding.UTF8.GetMaxByteCount(chars.Length)];
                for (var offset = 0; offset < text.Length; offset += chars.Length) {
                    var count = Math.Min(chars.Length, text.Length - offset);
                    text.CopyTo(offset, chars, 0, count);
                    var length = encoder.GetBytes(chars, 0, count, bytes, 0, offset + count == text.Length);
                    sha.TransformBlock(bytes, 0, length, bytes, 0);
                    yield return 0;
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                result = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static async ResourceOperationBase<bool> SameManifestAsync(string left, string right, CancellationToken token)
        {
            if (string.Equals(left, right, StringComparison.Ordinal)) {
                return true;
            }

            var a = await ComparableManifestAsync(left, token);
            var b = await ComparableManifestAsync(right, token);
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        private static async ResourceOperationBase<string> ComparableManifestAsync(string json, CancellationToken token)
        {
            ResourceManifest value = await ResourceManifest.FromJsonAsync(json, cancellationToken: token);
            await ResourceManifestWork.RunAsync(Normalize(), token);
            return await SerializeAsync(value, token);
            IEnumerable<int> Normalize()
            {
                if (value.FormatVersion < 3) {
                    value.PackageName = value.PackageVersion = "";
                }

                foreach (AssetInfo asset in value.Assets) {
                    if (value.FormatVersion < 6) {
                        asset.DependencyBundles = Array.Empty<string>();
                    }

                    if (value.FormatVersion < 3) { asset.Guid = ""; asset.Tags = Array.Empty<string>(); }
                    yield return 0;
                }
            }
        }

        private static async ResourceOperationBase<BundleInfo[]> SelectBundlesAsync(ResourceManifest manifest, ResourceSelection selection, CancellationToken token)
        {
            // Full preparation only needs bundles: avoid building asset metadata at all.
            if (selection == null || selection.Kind == ResourceSelection.SelectionKind.All) {
                BundleInfo[] bundles = null;
                await ResourceManifestWork.RunAsync(CopyAndSort(), token);
                return bundles;
                IEnumerable<int> CopyAndSort()
                {
                    bundles = new BundleInfo[manifest.Bundles.Length];
                    for (var i = 0; i < bundles.Length; i++) { bundles[i] = manifest.Bundles[i]; yield return 0; }
                    foreach (var step in ResourceManifestWork.SortSteps(bundles, (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name))) {
                        yield return step;
                    }
                }
            }
            ResourceCatalog catalog = await ResourceCatalog.CreateOwnedAsync(manifest, token: token);
            ResourceSelectionResult selected = await catalog.SelectAsync(selection, token);
            return selected.OwnedBundles;
        }

        private async ResourceOperationBase<CachedResourceFileSystem> CreateVersionCacheAsync(string version, CancellationToken token, bool offline = false)
        {
            ResourceManifest manifest = await ReadBuiltInManifestAsync(token);
            ResourceManifestPreparation prepared = manifest == null ? null : await ResourceManifestPreparation.TakeOwnershipAsync(manifest, token: token);
            token.ThrowIfCancellationRequested();
            return new CachedResourceFileSystem(new BundleDownloadCache(m_builtInRoot, OptionsFor(version), m_buildTarget,
                prepared, offline ? new HotUpdate.HotUpdateBootstrap.OfflineTransport() : null, offline: offline));
        }

        private static async ResourceOperationBase<BundleInfo[]> ValidateRequiredBundlesAsync(ResourceManifest manifest, string[] names, CancellationToken token)
        {
            BundleInfo[] result = null;
            await ResourceManifestWork.RunAsync(RequiredBundleSteps(manifest, names, value => result = value), token);
            return result;
        }

        private static IEnumerable<int> RequiredBundleSteps(ResourceManifest manifest, string[] names, Action<BundleInfo[]> publish)
        {
            if (names == null) {
                throw new InvalidDataException("选择性版本指针缺少 Bundle 列表。");
            }

            var bundles = new Dictionary<string, BundleInfo>(manifest.Bundles.Length, StringComparer.Ordinal);
            foreach (BundleInfo bundle in manifest.Bundles) { bundles.Add(bundle.Name, bundle); yield return 0; }
            var required = new HashSet<string>(StringComparer.Ordinal);
            var result = new BundleInfo[names.Length];
            for (var i = 0; i < names.Length; i++) {
                yield return names[i] == null || !required.Add(names[i]) || !bundles.TryGetValue(names[i], out result[i])
                    ? throw new InvalidDataException("选择性版本指针包含重复或未知 Bundle。")
                    : 0;
            }
            foreach (BundleInfo bundle in result) {
                foreach (var dependency in bundle.Dependencies) {
                    yield return !required.Contains(dependency) ? throw new InvalidDataException("选择性版本指针缺少依赖 Bundle。") : 0;
                }
            }

            foreach (var step in ResourceManifestWork.SortSteps(result, (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name))) {
                yield return step;
            }

            publish(result);
        }

        private async ResourceOperationBase EnterAsync(CancellationToken token, bool recover = true)
        {
            token.ThrowIfCancellationRequested();
            Enter(false);
            try {
                if (recover) {
                    await RecoverPointerAsync(token);
                }
            }
            catch { Exit(); throw; }
        }

        /// <summary>Refreshes the active pointer and validates its manifests without synchronous large-manifest decoding.</summary>
        public async ResourceOperationBase RefreshAsync(CancellationToken cancellationToken = default)
        {
            CheckThread();
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            await EnterAsync(cancellationToken);
            Exit();
        }

        private async ResourceOperationBase RecoverPointerAsync(CancellationToken token)
        {
            var current = PointerPath();
            if (!File.Exists(current) && !File.Exists(BackupPath())) { m_pointer = null; m_activeManifest = null; return; }
            (VersionPointer Pointer, ResourceManifest Manifest) state = await ReadPointerAsync(current, token);
            if (state.Pointer == null) {
                state = await ReadPointerAsync(BackupPath(), token);
                if (state.Pointer == null) {
                    throw new InvalidDataException("活动版本指针及备份都无法通过清单校验。");
                }

                token.ThrowIfCancellationRequested();
                WriteAtomic(current, JsonUtility.ToJson(state.Pointer));
            }
            token.ThrowIfCancellationRequested();
            m_pointer = state.Pointer;
            m_activeManifest = state.Manifest;
        }

        private async ResourceOperationBase<(VersionPointer Pointer, ResourceManifest Manifest)> ReadPointerAsync(string path, CancellationToken token)
        {
            if (!File.Exists(path)) {
                return default;
            }

            try {
                VersionPointer candidate = JsonUtility.FromJson<VersionPointer>(await ResourceFileReader.ReadTextAsync(path, token));
                ValidateVersion(candidate?.Active);
                if (!string.IsNullOrEmpty(candidate.Previous)) {
                    ValidateVersion(candidate.Previous);
                }

                var json = await ResourceFileReader.ReadTextAsync(ManifestPath(candidate.Active), token);
                if (!string.Equals(await HashAsync(json, token), candidate.ManifestSha256, StringComparison.OrdinalIgnoreCase)) {
                    return default;
                }

                ResourceManifest manifest = await ParseTargetAsync(json, candidate.Active, token);
                if (candidate.HasSelection) {
                    await ValidateRequiredBundlesAsync(manifest, candidate.RequiredBundles, token);
                }

                if (candidate.PreviousHasSelection) {
                    if (string.IsNullOrEmpty(candidate.Previous)) {
                        return default;
                    }

                    ResourceManifest previous = await ParseTargetAsync(await ResourceFileReader.ReadTextAsync(ManifestPath(candidate.Previous), token), candidate.Previous, token);
                    await ValidateRequiredBundlesAsync(previous, candidate.PreviousRequiredBundles, token);
                }
                return (candidate, manifest);
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException || error is InvalidOperationException) { return default; }
        }
    }
}

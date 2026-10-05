using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    public enum ResourceTagMatch { Any, All }

    /// <summary>不可变的选择条件。空列表选择零项；只有 All 选择整个清单。</summary>
    public sealed class ResourceSelection
    {
        internal enum SelectionKind { All, Addresses, Guids, Tags, Untagged }
        internal SelectionKind Kind { get; }
        internal readonly string[] Values;
        internal ResourceTagMatch Match { get; }
        public static ResourceSelection All { get; } = new(SelectionKind.All, Array.Empty<string>());
        public static ResourceSelection Untagged { get; } = new(SelectionKind.Untagged, Array.Empty<string>());

        private ResourceSelection(SelectionKind kind, IEnumerable<string> values, ResourceTagMatch match = ResourceTagMatch.Any)
        {
            if (values == null) {
                throw new ArgumentNullException(nameof(values));
            }

            if (!Enum.IsDefined(typeof(ResourceTagMatch), match)) {
                throw new ArgumentOutOfRangeException(nameof(match));
            }

            Kind = kind; Match = match;
            Values = values.Select(value =>
            {
                if (string.IsNullOrWhiteSpace(value) || value != value.Trim()) {
                    throw new ArgumentException("选择条件不能为空或包含首尾空白。", nameof(values));
                }

                if (kind == SelectionKind.Guids) {
                    ValidateGuid(value);
                }

                return kind == SelectionKind.Guids ? value.ToLowerInvariant() : value;
            }).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }
        public static ResourceSelection ByAddresses(IEnumerable<string> addresses)
        {
            return new(SelectionKind.Addresses, addresses);
        }

        public static ResourceSelection ByGuids(IEnumerable<string> guids)
        {
            return new(SelectionKind.Guids, guids);
        }

        public static ResourceSelection ByTags(IEnumerable<string> tags, ResourceTagMatch match = ResourceTagMatch.Any)
        {
            return new(SelectionKind.Tags, tags, match);
        }

        internal static void ValidateGuid(string guid)
        {
            if (guid == null || guid.Length != 32 || guid.Any(c => !Uri.IsHexDigit(c))) {
                throw new ArgumentException("GUID 必须是 32 位十六进制字符串。", nameof(guid));
            }
        }
    }

    /// <summary>只读业务元数据；不会向调用方暴露运行中的清单 DTO。</summary>
    public sealed class ResourceAssetInfo
    {
        public string Address { get; }
        public string AssetPath { get; }
        public string BundleName { get; }
        public ResourceKind Kind { get; }
        public ResourceFileType FileType { get; }
        public long FileSize { get; }
        public string Guid { get; }
        public IReadOnlyList<string> Tags { get; }
        internal int CatalogIndex { get; }
        internal string[] DependencyBundles { get; }
        internal ResourceAssetInfo(AssetInfo info, bool hasMetadata, int catalogIndex)
        {
            CatalogIndex = catalogIndex;
            DependencyBundles = info.DependencyBundles;
            Address = info.Address; AssetPath = info.AssetPath; BundleName = info.BundleName; Kind = info.Kind;
            FileType = info.FileType; FileSize = info.FileSize;
            Guid = hasMetadata ? info.Guid.ToLowerInvariant() : null;
            Tags = Array.AsReadOnly(hasMetadata ? info.Tags : Array.Empty<string>());
        }
    }

    /// <summary>资源条目和去重后的完整依赖闭包；TotalBytes 为物理 Bundle 大小，非缺失下载量。</summary>
    public sealed class ResourceSelectionResult
    {
        public IReadOnlyList<ResourceAssetInfo> Assets { get; }
        public IReadOnlyList<string> BundleNames { get; }
        public long TotalBytes { get; }
        public bool IsWholeManifest { get; }
        internal BundleInfo[] OwnedBundles { get; }
        internal ResourceSelectionResult(ResourceAssetInfo[] assets, BundleInfo[] bundles, string[] bundleNames, long totalBytes, int totalBundles)
        {
            OwnedBundles = bundles;
            Assets = Array.AsReadOnly(assets);
            BundleNames = Array.AsReadOnly(bundleNames);
            TotalBytes = totalBytes;
            IsWholeManifest = bundleNames.Length == totalBundles;
        }
    }

    /// <summary>离线查询索引；构造时复制并校验清单，查询不触发 I/O 或 Unity 对象加载。</summary>
    public sealed class ResourceCatalog
    {
        private Dictionary<string, ResourceAssetInfo> m_addresses;
        private Dictionary<string, ResourceAssetInfo> m_guids;
        private Dictionary<string, ResourceAssetInfo[]> m_guidAliases;
        private Dictionary<string, ResourceAssetInfo[]> m_tagIndex;
        private Dictionary<string, BundleInfo> m_bundles;
        private ResourceAssetInfo[] m_ordered;
        public bool SupportsMetadata { get; private set; }
        public IReadOnlyList<string> Tags { get; private set; }

        public ResourceCatalog(ResourceManifest manifest, ResourceLocationMatch locationMatching = ResourceLocationMatch.ExactAddress)
        {
            if (manifest == null) {
                throw new ArgumentNullException(nameof(manifest));
            }

            ResourceManifestWork.Drain(BuildSteps(manifest.Clone(), locationMatching));
        }

        internal static ResourceCatalog CreateOwned(ResourceManifest snapshot, ResourceLocationMatch matching)
        {
            var result = new ResourceCatalog();
            ResourceManifestWork.Drain(result.BuildSteps(snapshot, matching));
            return result;
        }

        private ResourceCatalog() { }

        /// <summary>Copies and validates mutable input before building an independent immutable index.</summary>
        public static async ResourceOperationBase<ResourceCatalog> CreateAsync(ResourceManifest manifest,
            ResourceLocationMatch locationMatching = ResourceLocationMatch.ExactAddress, CancellationToken cancellationToken = default)
        {
            if (manifest == null) {
                throw new ArgumentNullException(nameof(manifest));
            }

            ResourceManifest snapshot = await manifest.CloneAsync(cancellationToken);
            return await CreateOwnedAsync(snapshot, locationMatching, cancellationToken);
        }

        internal static async ResourceOperationBase<ResourceCatalog> CreateOwnedAsync(ResourceManifest snapshot,
            ResourceLocationMatch matching = ResourceLocationMatch.ExactAddress, CancellationToken token = default, bool cooperative = false)
        {
            var result = new ResourceCatalog();
            await ResourceManifestWork.RunAsync(result.BuildSteps(snapshot, matching), token, cooperative);
            return result;
        }

        private IEnumerable<int> BuildSteps(ResourceManifest snapshot, ResourceLocationMatch matching)
        {
            SupportsMetadata = snapshot.FormatVersion >= 3;
            m_supportsAssetDependencies = snapshot.FormatVersion >= 6;
            m_bundles = new Dictionary<string, BundleInfo>(snapshot.Bundles.Length, StringComparer.Ordinal);
            foreach (BundleInfo bundle in snapshot.Bundles) { m_bundles.Add(bundle.Name, bundle); yield return 0; }
            var entries = new AssetInfo[snapshot.Assets.Length];
            for (var i = 0; i < entries.Length; i++) { entries[i] = snapshot.Assets[i]; yield return 0; }
            foreach (var step in ResourceManifestWork.SortSteps(entries, (a, b) => StringComparer.Ordinal.Compare(a.Address, b.Address))) {
                yield return step;
            }

            m_ordered = new ResourceAssetInfo[entries.Length];
            m_guids = new Dictionary<string, ResourceAssetInfo>(entries.Length, StringComparer.OrdinalIgnoreCase);
            var aliasLists = new Dictionary<string, List<ResourceAssetInfo>>(StringComparer.OrdinalIgnoreCase);
            var tagLists = new Dictionary<string, List<ResourceAssetInfo>>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Length; i++) {
                var info = new ResourceAssetInfo(entries[i], SupportsMetadata, i);
                m_ordered[i] = info;
                if (info.Guid != null && !m_guids.TryAdd(info.Guid, info)) {
                    if (!aliasLists.TryGetValue(info.Guid, out List<ResourceAssetInfo> aliases)) {
                        aliasLists.Add(info.Guid, aliases = new List<ResourceAssetInfo> { m_guids[info.Guid] });
                    }

                    aliases.Add(info);
                }
                foreach (var tag in info.Tags) { Add(tagLists, tag, info); yield return 0; }
                yield return 0;
            }
            foreach (var step in ResourceLocationIndex.CreateSteps(m_ordered, info => info.Address, info => info.AssetPath,
                matching, value => m_addresses = value)) {
                yield return step;
            }

            m_guidAliases = new Dictionary<string, ResourceAssetInfo[]>(aliasLists.Count, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<ResourceAssetInfo>> entry in aliasLists) { m_guidAliases.Add(entry.Key, entry.Value.ToArray()); yield return 0; }
            m_tagIndex = new Dictionary<string, ResourceAssetInfo[]>(tagLists.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<ResourceAssetInfo>> entry in tagLists) { m_tagIndex.Add(entry.Key, entry.Value.ToArray()); yield return 0; }
            var tags = new string[m_tagIndex.Count];
            var index = 0;
            foreach (var tag in m_tagIndex.Keys) { tags[index++] = tag; yield return 0; }
            foreach (var step in ResourceManifestWork.SortSteps(tags, StringComparer.Ordinal.Compare)) {
                yield return step;
            }

            Tags = Array.AsReadOnly(tags);
        }

        private static void Add(Dictionary<string, List<ResourceAssetInfo>> index, string key, ResourceAssetInfo info)
        {
            if (!index.TryGetValue(key, out List<ResourceAssetInfo> list)) {
                index.Add(key, list = new List<ResourceAssetInfo>());
            }

            list.Add(info);
        }

        public ResourceAssetInfo GetAssetInfo(string address)
        {
            return address != null && m_addresses.TryGetValue(address, out ResourceAssetInfo info)
            ? info : throw new KeyNotFoundException("Unknown resource address: " + address);
        }

        /// <summary>同一路径的别名共用 GUID；返回按 Ordinal 排序的第一个地址，可传给所有加载入口。</summary>
        public ResourceAssetInfo GetAssetInfoByGuid(string guid)
        {
            RequireMetadata(); ResourceSelection.ValidateGuid(guid);
            return m_guids.TryGetValue(guid, out ResourceAssetInfo match) ? match : throw new KeyNotFoundException("Unknown resource GUID: " + guid);
        }

        public ResourceSelectionResult Select(ResourceSelection selection)
        {
            if (selection == null) {
                throw new ArgumentNullException(nameof(selection));
            }

            ResourceSelectionResult result = null;
            ResourceManifestWork.Drain(SelectSteps(selection, value => result = value));
            return result;
        }

        /// <summary>Builds the selection and dependency closure without mutating shared catalog caches.</summary>
        public async ResourceOperationBase<ResourceSelectionResult> SelectAsync(ResourceSelection selection,
            CancellationToken cancellationToken = default)
        {
            if (selection == null) {
                throw new ArgumentNullException(nameof(selection));
            }

            ResourceSelectionResult result = null;
            await ResourceManifestWork.RunAsync(SelectSteps(selection, value => result = value), cancellationToken);
            return result;
        }

        private IEnumerable<int> SelectSteps(ResourceSelection selection, Action<ResourceSelectionResult> publish)
        {
            HashSet<ResourceAssetInfo> selected = selection.Kind == ResourceSelection.SelectionKind.All ? null : new HashSet<ResourceAssetInfo>();
            switch (selection.Kind) {
                case ResourceSelection.SelectionKind.All: break;
                case ResourceSelection.SelectionKind.Addresses:
                    foreach (var address in selection.Values) { selected.Add(GetAssetInfo(address)); yield return 0; }
                    break;
                case ResourceSelection.SelectionKind.Guids:
                    RequireMetadata();
                    foreach (var guid in selection.Values) {
                        if (!m_guids.TryGetValue(guid, out ResourceAssetInfo first)) {
                            throw new KeyNotFoundException("Unknown resource GUID: " + guid);
                        }

                        if (m_guidAliases.TryGetValue(guid, out ResourceAssetInfo[] aliases)) {
                            foreach (ResourceAssetInfo info in aliases) { selected.Add(info); yield return 0; }
                        }
                        else { selected.Add(first); yield return 0; }
                    }
                    break;
                case ResourceSelection.SelectionKind.Untagged:
                    RequireMetadata();
                    foreach (ResourceAssetInfo info in m_ordered) { if (info.Tags.Count == 0) { selected.Add(info); } yield return 0; }
                    break;
                default:
                    RequireMetadata();
                    for (var i = 0; i < selection.Values.Length; i++) {
                        ResourceAssetInfo[] matches = m_tagIndex.TryGetValue(selection.Values[i], out ResourceAssetInfo[] found) ? found : Array.Empty<ResourceAssetInfo>();
                        if (selection.Match == ResourceTagMatch.Any || i == 0) {
                            foreach (ResourceAssetInfo info in matches) { selected.Add(info); yield return 0; }
                        }
                        else {
                            var intersect = new HashSet<ResourceAssetInfo>();
                            foreach (ResourceAssetInfo info in matches) { if (selected.Contains(info)) { intersect.Add(info); } yield return 0; }
                            selected = intersect;
                        }
                        if (selection.Match == ResourceTagMatch.All && selected.Count == 0) {
                            break;
                        }
                    }
                    break;
            }
            ResourceAssetInfo[] assets = selected == null ? m_ordered : new ResourceAssetInfo[selected.Count];
            var index = 0;
            if (selected != null) {
                foreach (ResourceAssetInfo info in selected) { assets[index++] = info; yield return 0; }
                foreach (var step in ResourceManifestWork.SortSteps(assets, (a, b) => a.CatalogIndex.CompareTo(b.CatalogIndex))) {
                    yield return step;
                }
            }
            var closure = new HashSet<string>(StringComparer.Ordinal);
            if (selection.Kind == ResourceSelection.SelectionKind.All) {
                foreach (var name in m_bundles.Keys) { closure.Add(name); yield return 0; }
            }
            else {
                // A single traversal of legacy bundle edges across the entire selection, including cycles.
                var pending = new Stack<string>();
                foreach (ResourceAssetInfo info in assets) {
                    if (info.Kind != ResourceKind.RawFile && info.DependencyBundles != null && m_supportsAssetDependencies) {
                        closure.Add(info.BundleName);
                        foreach (var dependency in info.DependencyBundles) { closure.Add(dependency); yield return 0; }
                    }
                    else {
                        pending.Push(info.BundleName);
                    }

                    yield return 0;
                }
                // Keep traversal visitation separate: a V6 asset closure may already contain a raw container.
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (pending.Count > 0) {
                    var name = pending.Pop();
                    if (!visited.Add(name)) { yield return 0; continue; }
                    closure.Add(name);
                    foreach (var dependency in m_bundles[name].Dependencies) { pending.Push(dependency); yield return 0; }
                    yield return 0;
                }
            }
            var files = new BundleInfo[closure.Count];
            index = 0;
            foreach (var name in closure) { files[index++] = m_bundles[name]; yield return 0; }
            foreach (var step in ResourceManifestWork.SortSteps(files, (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name))) {
                yield return step;
            }

            var names = new string[files.Length];
            long totalBytes = 0;
            for (var i = 0; i < files.Length; i++) { names[i] = files[i].Name; totalBytes = checked(totalBytes + files[i].Size); yield return 0; }
            publish(new ResourceSelectionResult(assets, files, names, totalBytes, m_bundles.Count));
        }

        private bool m_supportsAssetDependencies;

        private void RequireMetadata()
        {
            if (!SupportsMetadata) {
                throw new NotSupportedException("标签和 GUID 查询要求 V3 Package 清单；旧清单仍可按地址选择。");
            }
        }
        internal static BundleInfo[] SelectBundles(ResourceManifest manifest, ResourceSelection selection)
        {
            return SelectBundles(manifest, new ResourceCatalog(manifest).Select(selection));
        }

        internal static BundleInfo[] SelectBundles(ResourceManifest manifest, ResourceSelectionResult selection)
        {
            var names = new HashSet<string>(selection.BundleNames, StringComparer.Ordinal);
            return manifest.Bundles.Where(bundle => names.Contains(bundle.Name)).OrderBy(bundle => bundle.Name, StringComparer.Ordinal).ToArray();
        }
    }
}

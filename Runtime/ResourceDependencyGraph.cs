#nullable disable
using System;
using System.Collections.Generic;
using System.IO;

namespace ZRAsset
{
    /// <summary>Immutable-manifest dependency index. Closures are built on demand without recursion.</summary>
    internal sealed class ResourceDependencyGraph
    {
        private readonly Dictionary<string, BundleInfo> m_bundles;
        private readonly Dictionary<string, string[]> m_bundleClosures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> m_assetClosures = new(StringComparer.Ordinal);
        private readonly bool m_hasAssetDependencies;

        internal ResourceDependencyGraph(ResourceManifest manifest)
        {
            m_hasAssetDependencies = manifest.FormatVersion >= 6;
            m_bundles = new Dictionary<string, BundleInfo>(manifest.Bundles.Length, StringComparer.Ordinal);
            foreach (BundleInfo bundle in manifest.Bundles) {
                m_bundles.Add(bundle.Name, bundle);
            }
        }

        internal ResourceDependencyGraph(ResourceManifest manifest, Dictionary<string, BundleInfo> bundles)
        {
            m_hasAssetDependencies = manifest.FormatVersion >= 6;
            m_bundles = bundles;
        }

        internal string[] GetAssetClosure(AssetInfo asset)
        {
            if (!m_hasAssetDependencies || asset.Kind == ResourceKind.RawFile) {
                return GetBundleClosure(asset.BundleName);
            }

            if (m_assetClosures.TryGetValue(asset.AssetPath, out var cached)) {
                return cached;
            }

            var closure = new string[asset.DependencyBundles.Length + 1];
            closure[0] = asset.BundleName;
            Array.Copy(asset.DependencyBundles, 0, closure, 1, asset.DependencyBundles.Length);
            m_assetClosures.Add(asset.AssetPath, closure);
            return closure;
        }

        internal string[] GetBundleClosure(string bundleName)
        {
            if (m_bundleClosures.TryGetValue(bundleName, out var cached)) {
                return cached;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(bundleName);
            while (pending.Count > 0) {
                var current = pending.Pop();
                if (!visited.Add(current)) {
                    continue;
                }

                foreach (var dependency in m_bundles[current].Dependencies) {
                    pending.Push(dependency);
                }
            }

            var closure = new string[visited.Count];
            visited.CopyTo(closure);
            Array.Sort(closure, StringComparer.Ordinal);
            m_bundleClosures.Add(bundleName, closure);
            return closure;
        }

        internal static void Validate(Dictionary<string, BundleInfo> bundles, bool allowCycles)
        {
            foreach (var step in ValidateSteps(bundles, allowCycles)) { }
        }

        internal static IEnumerable<int> ValidateSteps(Dictionary<string, BundleInfo> bundles, bool allowCycles)
        {
            var incomingCounts = new Dictionary<string, int>(bundles.Count, StringComparer.Ordinal);
            foreach (var name in bundles.Keys) { incomingCounts.Add(name, 0); yield return 0; }
            foreach (BundleInfo bundle in bundles.Values) {
                foreach (var dependency in bundle.Dependencies) {
                    if (string.IsNullOrEmpty(dependency) || !incomingCounts.ContainsKey(dependency)) {
                        throw new InvalidDataException("Missing dependency: " + dependency);
                    }

                    incomingCounts[dependency]++;
                    yield return 0;
                }
            }
            if (allowCycles) {
                yield break;
            }

            var ready = new Queue<string>();
            foreach (KeyValuePair<string, int> entry in incomingCounts) {
                if (entry.Value == 0) {
                    ready.Enqueue(entry.Key);
                }

                yield return 0;
            }
            var visitedCount = 0;
            while (ready.Count > 0) {
                var current = ready.Dequeue();
                visitedCount++;
                foreach (var dependency in bundles[current].Dependencies) {
                    if (--incomingCounts[dependency] == 0) {
                        ready.Enqueue(dependency);
                    }

                    yield return 0;
                }
                yield return 0;
            }
            if (visitedCount != bundles.Count) {
                throw new InvalidDataException("Cyclic bundle dependency.");
            }
        }
    }
}

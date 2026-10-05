using System;
using System.Collections.Generic;

namespace ZRAsset
{
    /// <summary>
    /// A loaded Unity bundle can retain objects from expired providers. Keep their dependencies
    /// while that owning bundle remains pinned, and release unrooted cycles as a group.
    /// </summary>
    internal sealed class ResourceProviderRetention
    {
        private readonly Dictionary<BundleProvider, int> m_candidateReferences = new();
        private readonly Dictionary<string, List<AssetProvider>> m_owners = new(StringComparer.Ordinal);
        private readonly Stack<List<AssetProvider>> m_groupPool = new();
        private readonly Queue<string> m_pendingBundles = new();
        private readonly HashSet<string> m_pinnedBundles = new(StringComparer.Ordinal);
        private readonly HashSet<AssetProvider> m_retainedProviders = new();

        internal void FilterCandidates(List<AssetProvider> candidates, Dictionary<string, BundleProvider> bundles)
        {
            if (candidates.Count == 0) {
                return;
            }

            try {
                foreach (AssetProvider provider in candidates) {
                    if (!m_owners.TryGetValue(provider.OwningBundleName, out List<AssetProvider> owners)) {
                        owners = m_groupPool.Count == 0 ? new List<AssetProvider>() : m_groupPool.Pop();
                        m_owners.Add(provider.OwningBundleName, owners);
                    }
                    owners.Add(provider);
                    foreach (BundleProvider bundle in provider.Bundles) {
                        m_candidateReferences.TryGetValue(bundle, out var count);
                        m_candidateReferences[bundle] = count + 1;
                    }
                }
                foreach (var name in m_owners.Keys) {
                    if (!bundles.TryGetValue(name, out BundleProvider bundle)) {
                        continue;
                    }

                    m_candidateReferences.TryGetValue(bundle, out var candidatesCount);
                    if (bundle.References > candidatesCount) {
                        Pin(name);
                    }
                }
                while (m_pendingBundles.Count > 0) {
                    var name = m_pendingBundles.Dequeue();
                    foreach (AssetProvider provider in m_owners[name]) {
                        if (!m_retainedProviders.Add(provider)) {
                            continue;
                        }

                        foreach (BundleProvider bundle in provider.Bundles) {
                            var candidateCount = --m_candidateReferences[bundle];
                            if (bundle.References > candidateCount) {
                                Pin(bundle.Info.Name);
                            }
                        }
                    }
                }
                var retainedCount = 0;
                for (var index = 0; index < candidates.Count; index++) {
                    if (!m_retainedProviders.Contains(candidates[index])) {
                        candidates[retainedCount++] = candidates[index];
                    }
                }

                candidates.RemoveRange(retainedCount, candidates.Count - retainedCount);
            }
            finally {
                foreach (List<AssetProvider> group in m_owners.Values) {
                    group.Clear();
                    m_groupPool.Push(group);
                }
                m_owners.Clear();
                m_candidateReferences.Clear();
                m_pendingBundles.Clear();
                m_pinnedBundles.Clear();
                m_retainedProviders.Clear();
            }
        }

        private void Pin(string bundleName)
        {
            if (m_owners.ContainsKey(bundleName) && m_pinnedBundles.Add(bundleName)) {
                m_pendingBundles.Enqueue(bundleName);
            }
        }
    }
}

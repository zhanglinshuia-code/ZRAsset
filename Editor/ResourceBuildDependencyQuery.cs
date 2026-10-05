using System.Collections.Generic;
using UnityEditor;

namespace ZRAsset.Editor
{
    /// <summary>One analysis snapshot shares dependency queries across collection and bundle planning.</summary>
    internal sealed class ResourceBuildDependencyQuery
    {
        private readonly Dictionary<(string Path, bool Recursive), string[]> m_dependencies = new();
        internal int QueryCount { get; private set; }
        internal int CacheHitCount { get; private set; }

        internal string[] GetDependencies(string assetPath, bool recursive)
        {
            (string assetPath, bool recursive) key = (assetPath, recursive);
            if (m_dependencies.TryGetValue(key, out var dependencies)) {
                CacheHitCount++;
                return dependencies;
            }
            dependencies = AssetDatabase.GetDependencies(assetPath, recursive);
            QueryCount++;
            m_dependencies.Add(key, dependencies);
            return dependencies;
        }
    }
}

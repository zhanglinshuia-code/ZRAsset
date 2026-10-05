using System;
using System.Collections.Generic;
using System.IO;

namespace ZRAsset
{
    [Flags]
    public enum ResourceLocationMatch
    {
        ExactAddress = 0,
        AssetPath = 1,
        Extensionless = 2,
        IgnoreCase = 4
    }

    /// <summary>Precomputes aliases once; lookups allocate no normalized strings.</summary>
    internal static class ResourceLocationIndex
    {
        internal static Dictionary<string, T> Create<T>(IEnumerable<T> entries,
            Func<T, string> getAddress, Func<T, string> getAssetPath, ResourceLocationMatch matching)
        {
            Dictionary<string, T> result = null;
            foreach (var step in CreateSteps(entries, getAddress, getAssetPath, matching, value => result = value)) { }
            return result;
        }

        internal static IEnumerable<int> CreateSteps<T>(IEnumerable<T> entries,
            Func<T, string> getAddress, Func<T, string> getAssetPath, ResourceLocationMatch matching, Action<Dictionary<string, T>> publish)
        {
            const ResourceLocationMatch supported = ResourceLocationMatch.AssetPath |
                ResourceLocationMatch.Extensionless | ResourceLocationMatch.IgnoreCase;
            if ((matching & ~supported) != 0) {
                throw new ArgumentOutOfRangeException(nameof(matching));
            }

            StringComparer comparer = (matching & ResourceLocationMatch.IgnoreCase) != 0
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var locations = new Dictionary<string, T>(comparer);
            foreach (T entry in entries) {
                Add(getAddress(entry), entry);
                if ((matching & ResourceLocationMatch.AssetPath) != 0) {
                    Add(getAssetPath(entry), entry);
                }

                yield return 0;
            }
            publish(locations);

            void Add(string location, T entry)
            {
                AddExact(location, entry);
                if ((matching & ResourceLocationMatch.Extensionless) == 0) {
                    return;
                }

                var extensionIndex = location.LastIndexOf('.');
                var directoryIndex = Math.Max(location.LastIndexOf('/'), location.LastIndexOf('\\'));
                if (extensionIndex > directoryIndex + 1) {
                    AddExact(location.Substring(0, extensionIndex), entry);
                }
            }

            void AddExact(string location, T entry)
            {
                if (locations.TryGetValue(location, out T existing)) {
                    if (!string.Equals(getAssetPath(existing), getAssetPath(entry), StringComparison.Ordinal)) {
                        throw new InvalidDataException("Ambiguous resource location: " + location);
                    }

                    return;
                }
                locations.Add(location, entry);
            }
        }
    }
}

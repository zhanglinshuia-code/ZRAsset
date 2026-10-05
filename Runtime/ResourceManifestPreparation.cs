using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ZRAsset
{
    // One scheduler for pure managed manifest work. No Unity objects or user callbacks run on workers.
    internal static class ResourceManifestWork
    {
        private static readonly OperationSemaphore s_slots = new(2, 2);
        internal static async ResourceOperationBase RunAsync(IEnumerable<int> steps, CancellationToken token, bool cooperative = false)
        {
            await s_slots.WaitAsync(token);
            try {
#if !UNITY_WEBGL || UNITY_EDITOR
                if (!cooperative) {
                    await ResourceOperationBase.Run(() => { Drain(steps, token); return true; });
                    token.ThrowIfCancellationRequested();
                    return;
                }
#endif
                await ResourceOperationBase.Yield();
                using IEnumerator<int> iterator = steps.GetEnumerator();
                var clock = Stopwatch.StartNew();
                var count = 0;
                while (true) {
                    token.ThrowIfCancellationRequested();
                    if (!iterator.MoveNext()) {
                        break;
                    }

                    if (++count >= 16384 || clock.Elapsed.TotalMilliseconds >= 4) { await ResourceOperationBase.Yield(); clock.Restart(); count = 0; }
                }
                token.ThrowIfCancellationRequested();
            }
            finally { s_slots.Release(); }
        }

        internal static void Drain(IEnumerable<int> steps, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            foreach (var step in steps) {
                token.ThrowIfCancellationRequested();
            }
        }

        // Single-call codecs/serializers cannot be preempted on WebGL. Keep that boundary explicit.
        internal static async ResourceOperationBase<T> ValueAsync<T>(Func<T> work, CancellationToken token)
        {
            T result = default;
            await RunAsync(Evaluate(), token);
            return result;
            IEnumerable<int> Evaluate() { result = work(); yield return 0; }
        }

        // Bottom-up merge sort: deterministic ordering with cancellation points even on WebGL.
        internal static IEnumerable<int> SortSteps<T>(T[] values, Comparison<T> compare)
        {
            if (values.Length < 2) {
                yield break;
            }

            var buffer = new T[values.Length];
            T[] source = values, destination = buffer;
            for (long width = 1; width < values.Length; width *= 2) {
                for (long start = 0; start < values.Length; start += width * 2) {
                    int left = (int)start, middle = (int)Math.Min(start + width, values.Length);
                    int right = middle, end = (int)Math.Min(start + (width * 2), values.Length);
                    for (var index = left; index < end; index++) {
                        destination[index] = left < middle && (right >= end || compare(source[left], source[right]) <= 0)
                            ? source[left++] : source[right++];
                        yield return 0;
                    }
                }
                T[] swap = source; source = destination; destination = swap;
            }
            if (!ReferenceEquals(source, values)) {
                for (var i = 0; i < values.Length; i++) { values[i] = source[i]; yield return 0; }
            }
        }
    }

    // Owns a private validated DTO. Callers of TakeOwnershipAsync must not expose or mutate it afterwards.
    internal sealed class ResourceManifestPreparation
    {
        internal ResourceManifest Manifest { get; private set; }
        internal Dictionary<string, AssetInfo> Assets { get; private set; }
        internal Dictionary<string, AssetInfo> Locations { get; private set; }
        internal Dictionary<string, BundleInfo> Bundles { get; private set; }
        internal Dictionary<string, string[]> BundleTags { get; private set; }
        internal ResourceLocationMatch Matching { get; private set; }

        internal static ResourceManifestPreparation Copy(ResourceManifest source, ResourceLocationMatch matching = ResourceLocationMatch.ExactAddress)
        {
            if (source == null) {
                throw new ArgumentNullException(nameof(source));
            }

            var result = new ResourceManifestPreparation();
            ResourceManifestWork.Drain(result.Prepare(source, matching, true));
            return result;
        }

        internal static async ResourceOperationBase<ResourceManifestPreparation> CopyAsync(ResourceManifest source,
            ResourceLocationMatch matching, CancellationToken token = default, bool cooperative = false)
        {
            if (source == null) {
                throw new ArgumentNullException(nameof(source));
            }

            var result = new ResourceManifestPreparation();
            await ResourceManifestWork.RunAsync(result.Prepare(source, matching, true), token, cooperative);
            return result;
        }

        internal static async ResourceOperationBase<ResourceManifestPreparation> TakeOwnershipAsync(ResourceManifest validated,
            ResourceLocationMatch matching = ResourceLocationMatch.ExactAddress, CancellationToken token = default)
        {
            var result = new ResourceManifestPreparation();
            await ResourceManifestWork.RunAsync(result.Prepare(validated, matching, false), token);
            return result;
        }

        private IEnumerable<int> Prepare(ResourceManifest source, ResourceLocationMatch matching, bool copy)
        {
            Matching = matching;
            if (copy) {
                foreach (var step in source.CopySteps(value => Manifest = value)) {
                    yield return step;
                }

                foreach (var step in Manifest.ValidateSteps()) {
                    yield return step;
                }
            }
            else {
                Manifest = source;
            }

            Assets = new Dictionary<string, AssetInfo>(Manifest.Assets.Length, StringComparer.Ordinal);
            Bundles = new Dictionary<string, BundleInfo>(Manifest.Bundles.Length, StringComparer.Ordinal);
            foreach (BundleInfo bundle in Manifest.Bundles) { Bundles.Add(bundle.Name, bundle); yield return 0; }
            var tags = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            SortedDictionary<string, AssetInfo> ordered = matching == ResourceLocationMatch.ExactAddress ? null : new SortedDictionary<string, AssetInfo>(StringComparer.Ordinal);
            foreach (AssetInfo asset in Manifest.Assets) {
                Assets.Add(asset.Address, asset);
                ordered?.Add(asset.Address, asset);
                if (!tags.TryGetValue(asset.BundleName, out HashSet<string> set)) {
                    tags.Add(asset.BundleName, set = new HashSet<string>(StringComparer.Ordinal));
                }

                foreach (var tag in asset.Tags ?? Array.Empty<string>()) { set.Add(tag); yield return 0; }
                yield return 0;
            }
            BundleTags = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, HashSet<string>> entry in tags) {
                var values = new string[entry.Value.Count];
                var i = 0;
                foreach (var value in entry.Value) { values[i++] = value; yield return 0; }
                BundleTags.Add(entry.Key, values);
                yield return 0;
            }
            if (ordered == null) {
                Locations = Assets;
            }
            else {
                foreach (var step in ResourceLocationIndex.CreateSteps(ordered.Values, asset => asset.Address,
                asset => asset.AssetPath, matching, value => Locations = value)) {
                    yield return step;
                }
            }
        }
    }
}

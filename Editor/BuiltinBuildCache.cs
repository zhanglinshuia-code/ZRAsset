using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ZRAsset.Editor
{
    /// <summary>
    /// 原生 Bundle 与 .manifest 保存在稳定目录，由 Unity 判断资产及依赖变化。
    /// 缓存永远保存明文；只有复制到本次 stage 的产物参与加密和发布。
    /// </summary>
    internal static class BuiltinBuildCache
    {
        private const int Schema = 1;
        private const string Payload = "payload";

        [Serializable]
        private sealed class Inventory
        {
            public int SchemaVersion;
            public CachedFile[] Files;
        }

        [Serializable]
        private sealed class CachedFile
        {
            public string Name;
            public long Length;
            public string Sha256;
        }

        internal static BundleInfo[] Build(ResourceBuildPlan plan, string stage, BuildTarget target)
        {
            ResourceBuildReport report = plan.Report;
            BuildAssetBundleOptions options = GetOptions(report);
            var identity = Schema + "\n" + Application.unityVersion + "\n" + target + "\n" +
                (int)options + "\n" + report.PackageName;
            // Scene bundle builds reject Library output in Unity 6. Keep the
            // persistent native output in its own Build subtree for all bundle types.
            var cacheRoot = Path.GetFullPath("Build/ZRAssetBuiltinCache");
            var cache = Path.Combine(cacheRoot, HashText(identity));
            var payload = Path.Combine(cache, Payload);
            var marker = Path.Combine(cache, "building");
            var inventoryPath = Path.Combine(cache, "inventory.json");
            Directory.CreateDirectory(cache);
            // 跨进程也不能同时修改同一原生缓存。锁文件保留，避免关闭/删除锁的竞争。
            using var lease = new FileStream(Path.Combine(cache, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // A forced build replaces all native output, so hashing the old cache
            // would add full-cache IO without providing any reusable work.
            Inventory prior = report.ForceRebuild || File.Exists(marker) ? null : ReadValidInventory(inventoryPath, payload);
            report.BuiltinCacheDirectory = cache;
            report.BuiltinCacheEnabled = !report.ForceRebuild;
            report.BuiltinCacheRestored = prior != null && !report.ForceRebuild;
            if (prior == null) {
                ResetPayload(payload, cacheRoot);
            }
            File.WriteAllText(marker, identity);
            if (report.ForceRebuild) {
                options |= BuildAssetBundleOptions.ForceRebuildAssetBundle;
            }
            AssetBundleManifest native = null;
            try {
                if (plan.Builds.Any(b => b.assetBundleName == Payload || b.assetBundleName == Payload + ".manifest")) {
                    throw new InvalidOperationException("Bundle 名称与 Unity 根清单冲突：" + Payload);
                }
                native = BuildPipeline.BuildAssetBundles(payload, plan.Builds, options, target) ?? throw new InvalidOperationException("Unity 内置管线构建失败，请查看 Console。");
                var names = native.GetAllAssetBundles().OrderBy(n => n, StringComparer.Ordinal).ToArray();
                if (!names.SequenceEqual(plan.Builds.Select(b => b.assetBundleName).OrderBy(n => n, StringComparer.Ordinal))) {
                    throw new InvalidDataException("Unity 内置管线返回的 Bundle 集合与计划不一致。");
                }
                var expected = new HashSet<string>(StringComparer.Ordinal) { Payload, Payload + ".manifest" };
                var bundles = new BundleInfo[names.Length];
                for (var i = 0; i < names.Length; i++) {
                    var name = names[i];
                    expected.Add(name);
                    expected.Add(name + ".manifest");
                    var source = Path.Combine(payload, name);
                    if (!BuildPipeline.GetCRCForAssetBundle(source, out var crc)) {
                        throw new IOException("无法读取 CRC：" + name);
                    }
                    bundles[i] = new BundleInfo
                    {
                        Name = name,
                        Hash = native.GetAssetBundleHash(name).ToString(),
                        Crc = crc,
                        Dependencies = native.GetDirectDependencies(name).OrderBy(n => n, StringComparer.Ordinal).ToArray()
                    };
                }
                var inventory = new Inventory { SchemaVersion = Schema, Files = new CachedFile[expected.Count] };
                Dictionary<string, BundleInfo> byName = bundles.ToDictionary(b => b.Name, StringComparer.Ordinal);
                var index = 0;
                foreach (var name in expected.OrderBy(n => n, StringComparer.Ordinal)) {
                    var path = Path.Combine(payload, name);
                    var length = new FileInfo(path).Length;
                    string hash;
                    if (byName.TryGetValue(name, out BundleInfo bundle)) {
                        hash = CopyAndHash(path, Path.Combine(stage, name));
                        bundle.Size = length;
                        bundle.Sha256 = hash;
                    }
                    else { hash = BundleBuilder.ComputeSha256(path); }
                    inventory.Files[index++] = new CachedFile { Name = name, Length = length, Sha256 = hash };
                }
                // 删除已从当前收集计划移除的文件，避免旧 Bundle 被误认为可发布产物。
                foreach (var path in Directory.GetFiles(payload)) {
                    if (!expected.Contains(Path.GetFileName(path))) {
                        File.Delete(path);
                    }
                }
                // 此指标表示内容未变化，不用耗时或哈希不变冒充原生缓存命中统计。
                if (prior != null) {
                    Dictionary<string, string> oldHashes = prior.Files.ToDictionary(f => f.Name, f => f.Sha256, StringComparer.Ordinal);
                    foreach (CachedFile file in inventory.Files) {
                        if (byName.ContainsKey(file.Name) && oldHashes.TryGetValue(file.Name, out var hash) && hash == file.Sha256) {
                            report.BuiltinUnchangedBundleCount++;
                        }
                    }
                }
                var temporary = inventoryPath + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(inventory));
                if (File.Exists(inventoryPath)) {
                    File.Delete(inventoryPath);
                }
                File.Move(temporary, inventoryPath);
                File.Delete(marker);
                return bundles;
            }
            finally {
                if (native != null) {
                    UnityEngine.Object.DestroyImmediate(native);
                }
            }
        }

        private static string CopyAndHash(string source, string destination)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan);
            using var hash = SHA256.Create();
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(262144);
            try {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) {
                    output.Write(buffer, 0, read);
                    hash.TransformBlock(buffer, 0, read, null, 0);
                }
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        }

        private static BuildAssetBundleOptions GetOptions(ResourceBuildReport report)
        {
            BuildAssetBundleOptions options = report.Compression switch
            {
                ResourceBundleCompression.Lz4 => BuildAssetBundleOptions.ChunkBasedCompression,
                ResourceBundleCompression.Uncompressed => BuildAssetBundleOptions.UncompressedAssetBundle,
                _ => BuildAssetBundleOptions.None
            };
            if (report.StripUnityVersion) {
                options |= BuildAssetBundleOptions.AssetBundleStripUnityVersion;
            }
            if (report.DisableWriteTypeTree) {
                options |= BuildAssetBundleOptions.DisableWriteTypeTree;
            }
            return options | BuildAssetBundleOptions.StrictMode;
        }

        private static Inventory ReadValidInventory(string path, string payload)
        {
            try {
                if (!File.Exists(path)) {
                    return null;
                }
                Inventory inventory = JsonUtility.FromJson<Inventory>(File.ReadAllText(path));
                if (inventory?.SchemaVersion != Schema || inventory.Files == null) {
                    return null;
                }
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (CachedFile file in inventory.Files) {
                    if (file == null || string.IsNullOrEmpty(file.Name) || Path.GetFileName(file.Name) != file.Name ||
                        file.Name.Contains('\\') || file.Name.Contains(':') || !names.Add(file.Name)) {
                        return null;
                    }
                    var source = Path.Combine(payload, file.Name);
                    if (!File.Exists(source) || new FileInfo(source).Length != file.Length || BundleBuilder.ComputeSha256(source) != file.Sha256) {
                        return null;
                    }
                }
                return names.Contains(Payload) && names.Contains(Payload + ".manifest") ? inventory : null;
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is UnauthorizedAccessException) {
                return null;
            }
        }

        private static void ResetPayload(string path, string cacheRoot)
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(full) != Payload) {
                throw new InvalidOperationException("拒绝清理构建缓存根目录之外的路径。");
            }
            if (Directory.Exists(full)) {
                Directory.Delete(full, true);
            }
            Directory.CreateDirectory(full);
        }

        private static string HashText(string value)
        {
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
    }
}

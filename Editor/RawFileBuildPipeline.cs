using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ZRAsset.Editor
{
    internal static class RawFileBuildPipeline
    {
        internal static string Hash(string path)
        {
            using FileStream stream = File.OpenRead(path); using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        internal static void DescribeEntries(AssetInfo[] assets, List<string> errors)
        {
            foreach (IGrouping<string, AssetInfo> container in assets.Where(a => a.Kind == ResourceKind.RawFile).GroupBy(a => a.BundleName)) {
                long offset = container.First().FileType == ResourceFileType.Archive ? ResourceArchive.HeaderSize : 0;
                foreach (IGrouping<string, AssetInfo> entries in container.GroupBy(a => a.AssetPath).OrderBy(g => g.Key, StringComparer.Ordinal)) {
                    try {
                        var size = new FileInfo(entries.Key).Length; var hash = Hash(entries.Key);
                        foreach (AssetInfo entry in entries) { entry.FileOffset = offset; entry.FileSize = size; entry.FileSha256 = hash; }
                        offset = checked(offset + size);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is OverflowException) { errors.Add("无法读取原始文件：" + entries.Key + "；" + error.Message); }
                }
            }
        }
        internal static IEnumerable<BundleInfo> Build(ResourceBuildPlan plan, string stage)
        {
            ILookup<string, AssetInfo> byBundle = plan.Report.Assets.Where(a => a.FileType != ResourceFileType.AssetBundle)
                .ToLookup(a => a.BundleName, StringComparer.Ordinal);
            var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            try {
                foreach (BundleAnalysis container in plan.Report.Bundles.Where(b => b.FileType != ResourceFileType.AssetBundle)) {
                    var path = Path.Combine(stage, container.Name);
                    AssetInfo[] entries = byBundle[container.Name].GroupBy(a => a.AssetPath, StringComparer.Ordinal)
                        .Select(g => g.First()).OrderBy(a => a.AssetPath, StringComparer.Ordinal).ToArray();
                    using var containerHash = SHA256.Create();
                    long size;
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                        using var hashedOutput = new CryptoStream(output, containerHash, CryptoStreamMode.Write, true);
                        if (container.FileType == ResourceFileType.Archive) {
                            ResourceArchive.WriteHeader(hashedOutput);
                        }

                        foreach (AssetInfo entry in entries) {
                            if (output.Position != entry.FileOffset) {
                                throw new InvalidDataException("构建期间原始文件布局发生变化。");
                            }

                            using var input = new FileStream(entry.AssetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            using var sha = SHA256.Create(); long copied = 0; int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) {
                                copied += read;
                                if (copied > entry.FileSize) {
                                    throw new InvalidDataException("构建期间源文件大小增加。");
                                }

                                sha.TransformBlock(buffer, 0, read, null, 0); hashedOutput.Write(buffer, 0, read);
                            }
                            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            if (copied != entry.FileSize || !string.Equals(BitConverter.ToString(sha.Hash).Replace("-", ""), entry.FileSha256, StringComparison.OrdinalIgnoreCase)) {
                                throw new InvalidDataException("原始文件在分析之后发生变化，请重新构建：" + entry.AssetPath);
                            }
                        }
                        hashedOutput.FlushFinalBlock();
                        size = output.Position;
                    }
                    var hash = BitConverter.ToString(containerHash.Hash).Replace("-", "").ToLowerInvariant();
                    yield return new BundleInfo
                    {
                        Name = container.Name,
                        FileType = container.FileType,
                        Size = size,
                        Sha256 = hash,
                        Hash = hash.Substring(0, 32),
                        Dependencies = Array.Empty<string>()
                    };
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
    }
}

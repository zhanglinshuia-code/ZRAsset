using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ZRAsset.Editor
{
    [Serializable]
    public sealed class ResourceBuildQualityResult
    {
        public long TotalBytes;
        public long PatchBytes;
        public long StartupBytes;
        public int BundleCount;
        public int DuplicateAssets;
        public string[] Violations = Array.Empty<string>();
        public bool Passed { get { return Violations.Length == 0; } }

        public void ThrowIfFailed()
        {
            if (!Passed) {
                throw new InvalidOperationException("资源发布质量检查失败：\n" + string.Join("\n", Violations));
            }
        }
    }

    /// <summary>在提交发布目录之前检查真实产物，预算使用传输容器的实际字节数。</summary>
    public static class ResourceBuildQuality
    {
        internal sealed class PolicySnapshot
        {
            public long Total, Bundle, Patch, Startup;
            public int Count, Duplicates;
            public bool RejectRemoved;
            public string[] Tags;
            public ResourceManifest Baseline;
        }

        internal static PolicySnapshot Capture(ResourceBuildQualityPolicy policy)
        {
            if (policy == null) {
                return null;
            }
            if (policy.MaximumTotalBytes < 0 || policy.MaximumBundleBytes < 0 || policy.MaximumPatchBytes < 0 ||
                policy.MaximumStartupBytes < 0 || policy.MaximumBundleCount < 0 || policy.MaximumDuplicateAssets < -1) {
                throw new ArgumentOutOfRangeException(nameof(policy), "质量预算不能为负数。");
            }
            var tags = policy.StartupTags == null ? Array.Empty<string>() : (string[])policy.StartupTags.Clone();
            foreach (var tag in tags) {
                if (string.IsNullOrWhiteSpace(tag)) {
                    throw new ArgumentException("首包标签不能为空。", nameof(policy));
                }
            }
            if (policy.MaximumStartupBytes > 0 && tags.Length == 0) {
                throw new ArgumentException("首包预算必须显式指定 StartupTags。", nameof(policy));
            }
            ResourceManifest baseline = policy.BaselineManifest == null ? null : ResourceManifest.FromJson(policy.BaselineManifest.text);
            return (policy.MaximumPatchBytes > 0 || policy.RejectRemovedAddresses) && baseline == null
                ? throw new ArgumentException("补丁预算和地址兼容检查必须提供基线清单。", nameof(policy))
                : new PolicySnapshot
                {
                    Total = policy.MaximumTotalBytes,
                    Bundle = policy.MaximumBundleBytes,
                    Patch = policy.MaximumPatchBytes,
                    Startup = policy.MaximumStartupBytes,
                    Count = policy.MaximumBundleCount,
                    Duplicates = policy.MaximumDuplicateAssets,
                    RejectRemoved = policy.RejectRemovedAddresses,
                    Tags = tags,
                    Baseline = baseline
                };
        }

        public static ResourceBuildQualityResult Evaluate(ResourceManifest manifest, ResourceBuildReport report, ResourceBuildQualityPolicy policy)
        {
            return policy == null ? throw new ArgumentNullException(nameof(policy)) : Evaluate(manifest, report, Capture(policy));
        }

        internal static ResourceBuildQualityResult Evaluate(ResourceManifest manifest, ResourceBuildReport report, PolicySnapshot policy)
        {
            if (manifest == null || report == null || policy == null) {
                throw new ArgumentNullException(nameof(manifest));
            }
            manifest.Validate();
            if (policy.Baseline != null && (manifest.BuildTarget != policy.Baseline.BuildTarget ||
                !string.Equals(manifest.PackageName, policy.Baseline.PackageName, StringComparison.Ordinal))) {
                throw new InvalidDataException("质量检查基线必须属于同一平台和 Package。");
            }
            var violations = new List<string>();
            var result = new ResourceBuildQualityResult
            {
                BundleCount = manifest.Bundles.Length,
                DuplicateAssets = report.RemainingDuplicates?.Length ?? 0
            };
            var oldBundles = new Dictionary<string, BundleInfo>(StringComparer.Ordinal);
            if (policy.Baseline != null) {
                foreach (BundleInfo bundle in policy.Baseline.Bundles) {
                    oldBundles.Add(bundle.Name, bundle);
                }
            }
            foreach (BundleInfo bundle in manifest.Bundles) {
                result.TotalBytes = checked(result.TotalBytes + bundle.Size);
                if (!oldBundles.TryGetValue(bundle.Name, out BundleInfo previous) || string.IsNullOrEmpty(bundle.Sha256) ||
                    !string.Equals(bundle.Sha256, previous.Sha256, StringComparison.OrdinalIgnoreCase) || bundle.Size != previous.Size) {
                    result.PatchBytes = checked(result.PatchBytes + bundle.Size);
                }
                CheckLimit(violations, "Bundle " + bundle.Name, bundle.Size, policy.Bundle);
            }
            if (policy.Tags.Length > 0) {
                var knownTags = new HashSet<string>(StringComparer.Ordinal);
                foreach (AssetInfo asset in manifest.Assets) {
                    knownTags.UnionWith(asset.Tags ?? Array.Empty<string>());
                }
                foreach (var tag in policy.Tags) {
                    if (!knownTags.Contains(tag)) {
                        violations.Add("首包标签没有匹配资源：" + tag);
                    }
                }
                ResourceSelectionResult selection = new ResourceCatalog(manifest).Select(ResourceSelection.ByTags(policy.Tags));
                var included = new HashSet<string>(selection.BundleNames, StringComparer.Ordinal);
                // 首包导出保留选中容器的物理依赖，避免低估混合容器的实际首包尺寸。
                var bundlesByName = new Dictionary<string, BundleInfo>(StringComparer.Ordinal);
                foreach (BundleInfo bundle in manifest.Bundles) {
                    bundlesByName.Add(bundle.Name, bundle);
                }
                var pending = new Stack<string>(selection.BundleNames);
                while (pending.Count > 0) {
                    foreach (var dependency in bundlesByName[pending.Pop()].Dependencies) {
                        if (included.Add(dependency)) {
                            pending.Push(dependency);
                        }
                    }
                }
                foreach (BundleInfo bundle in manifest.Bundles) {
                    if (included.Contains(bundle.Name)) {
                        result.StartupBytes = checked(result.StartupBytes + bundle.Size);
                    }
                }
            }
            if (policy.RejectRemoved) {
                var addresses = new HashSet<string>(StringComparer.Ordinal);
                foreach (AssetInfo asset in manifest.Assets) {
                    addresses.Add(asset.Address);
                }
                foreach (AssetInfo asset in policy.Baseline.Assets) {
                    if (!addresses.Contains(asset.Address)) {
                        violations.Add("删除了仍在基线中的地址：" + asset.Address);
                    }
                }
            }
            CheckLimit(violations, "总发布字节", result.TotalBytes, policy.Total);
            CheckLimit(violations, "补丁字节", result.PatchBytes, policy.Patch);
            CheckLimit(violations, "首包字节", result.StartupBytes, policy.Startup);
            CheckLimit(violations, "Bundle 数量", result.BundleCount, policy.Count);
            if (policy.Duplicates >= 0 && result.DuplicateAssets > policy.Duplicates) {
                violations.Add($"重复资源 {result.DuplicateAssets} 超过上限 {policy.Duplicates}。");
            }
            result.Violations = violations.ToArray();
            return result;
        }

        /// <summary>CI 对已落盘的发布目录再次校验内容，不上传文件或改变线上版本。</summary>
        public static ResourceBuildQualityResult VerifyDirectory(string directory, ResourceBuildQualityPolicy policy)
        {
            directory = Path.GetFullPath(directory);
            var manifest = ResourceManifest.FromJson(File.ReadAllText(Path.Combine(directory, "manifest.json")));
            ResourceBuildReport report = JsonUtility.FromJson<ResourceBuildReport>(File.ReadAllText(Path.Combine(directory, "build-report.json")));
            if (report == null || !report.Built || report.Errors == null || !report.IsValid || report.BuildTarget != manifest.BuildTarget ||
                report.PackageName != manifest.PackageName || report.PackageVersion != manifest.PackageVersion ||
                report.ActualBundles == null || report.ActualBundles.Length != manifest.Bundles.Length) {
                throw new InvalidDataException("构建报告缺失、未完成或与发布清单不匹配。");
            }
            var actualBundles = new Dictionary<string, BundleInfo>(StringComparer.Ordinal);
            foreach (BundleInfo actual in report.ActualBundles) {
                if (actual == null || string.IsNullOrEmpty(actual.Name) || !actualBundles.TryAdd(actual.Name, actual)) {
                    throw new InvalidDataException("构建报告包含非法或重复容器。");
                }
            }
            foreach (BundleInfo bundle in manifest.Bundles) {
                if (!actualBundles.TryGetValue(bundle.Name, out BundleInfo actual) || actual.Size != bundle.Size ||
                    !string.Equals(actual.Sha256, bundle.Sha256, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("构建报告容器与清单不匹配：" + bundle.Name);
                }
                var path = DownloadStorage.ValidatePath(directory, Path.Combine(directory, bundle.Name));
                if (!File.Exists(path) || new FileInfo(path).Length != bundle.Size ||
                    !string.Equals(BundleBuilder.ComputeSha256(path), bundle.Sha256, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("发布文件缺失或内容不匹配：" + bundle.Name);
                }
            }
            ResourceBuildQualityResult result = Evaluate(manifest, report, policy);
            result.ThrowIfFailed();
            return result;
        }

        public static void VerifyFromCommandLine()
        {
            var arguments = Environment.GetCommandLineArgs();
            string directory = null, policyPath = null;
            for (var index = 0; index + 1 < arguments.Length; index++) {
                if (arguments[index] == "-zrBuild") {
                    directory = arguments[++index];
                }
                else if (arguments[index] == "-zrQuality") {
                    policyPath = arguments[++index];
                }
            }
            if (directory == null || policyPath == null) {
                throw new ArgumentException("必须提供 -zrBuild 与 -zrQuality 参数。");
            }
            ResourceBuildQualityPolicy policy = AssetDatabase.LoadAssetAtPath<ResourceBuildQualityPolicy>(policyPath);
            ResourceBuildQualityResult result = VerifyDirectory(directory, policy);
            Debug.Log("ZRAsset 发布检查通过：" + JsonUtility.ToJson(result));
        }

        private static void CheckLimit(List<string> violations, string name, long actual, long limit)
        {
            if (limit > 0 && actual > limit) {
                violations.Add($"{name} {actual} 超过上限 {limit}。");
            }
        }
    }
}

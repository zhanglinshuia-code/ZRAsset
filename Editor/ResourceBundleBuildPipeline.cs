using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZRAsset.Editor
{
    /// <summary>将内置与 SBP 的真实产物统一为运行时清单；不改变收集与原始文件管线。</summary>
    internal static class ResourceBundleBuildPipeline
    {
        internal static BundleInfo[] Build(ResourceBuildPlan plan, string stage, BuildTarget target)
        {
            ResourceBuildReport report = plan.Report;
            var sbp = report.BuildBackend == ResourceBuildBackend.ScriptableBuildPipeline;
            report.BackendVersion = sbp ? ResourceBuildBackends.SbpVersion : Application.unityVersion;
            report.DependencyScope = sbp ? "Transitive" : "Direct";
            if (plan.Builds.Length == 0) {
                return Array.Empty<BundleInfo>();
            }

            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            if (!BuildPipeline.IsBuildTargetSupported(group, target)) {
                throw new NotSupportedException("未安装目标平台构建模块：" + target);
            }

            report.NativeBuildExecuted = true;
            var timer = Stopwatch.StartNew();
            try {
                BundleInfo[] bundles = sbp ? ResourceBuildBackends.BuildSbp(plan, stage, target) : BuildBuiltIn(plan, stage, target);
                // 禁止静默遗漏或额外生成未受收集计划管理的包；全部校验先于发布。
                IOrderedEnumerable<string> expected = plan.Builds.Select(b => b.assetBundleName).OrderBy(n => n, StringComparer.Ordinal);
                if (!expected.SequenceEqual(bundles.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal))) {
                    throw new InvalidDataException("构建后端返回的 Bundle 集合与收集计划不一致。");
                }

                foreach (BundleInfo bundle in bundles) {
                    var path = Path.Combine(stage, bundle.Name);
                    if (!File.Exists(path)) {
                        throw new FileNotFoundException("构建后端未生成文件：" + bundle.Name, path);
                    }

                    bundle.Size = new FileInfo(path).Length;
                    // Built-in copies and hashes in one streaming pass; SBP returns
                    // native hashes, so its output still needs a SHA-256 scan here.
                    bundle.Sha256 ??= BundleBuilder.ComputeSha256(path);
                }
                return bundles;
            }
            finally { timer.Stop(); report.NativeBuildMilliseconds = timer.Elapsed.TotalMilliseconds; }
        }

        private static BundleInfo[] BuildBuiltIn(ResourceBuildPlan plan, string stage, BuildTarget target)
        {
            return BuiltinBuildCache.Build(plan, stage, target);
        }

        internal static IEnumerable<string> PredictedDependencies(ResourceBuildPlan plan, BundleAnalysis bundle)
        {
            return plan.Report.BuildBackend != ResourceBuildBackend.ScriptableBuildPipeline
                ? bundle.Dependencies
                : plan.DependencyGraph.GetBundleClosure(bundle.Name).Where(name => name != bundle.Name);
        }

        internal static void CompleteAssetDependencies(ResourceBuildPlan plan, ResourceManifest manifest)
        {
            if (!plan.Report.UseAssetDependencies) {
                return;
            }

            ResourceDependencyGraph predictedGraph = plan.DependencyGraph;
            var actualGraph = new ResourceDependencyGraph(manifest);
            var extraDependencies = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (BundleInfo bundle in manifest.Bundles) {
                var expected = new HashSet<string>(predictedGraph.GetBundleClosure(bundle.Name), StringComparer.Ordinal);
                extraDependencies.Add(bundle.Name, bundle.Dependencies.Where(name => !expected.Contains(name)).ToArray());
            }
            foreach (AssetInfo asset in manifest.Assets) {
                var dependencies = new HashSet<string>(asset.DependencyBundles, StringComparer.Ordinal) { asset.BundleName };
                foreach (var name in dependencies.ToArray()) {
                    foreach (var extra in extraDependencies[name]) {
                        dependencies.UnionWith(actualGraph.GetBundleClosure(extra));
                    }
                }

                dependencies.Remove(asset.BundleName);
                asset.DependencyBundles = dependencies.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            }
        }
    }
}

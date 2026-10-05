using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZRAsset.Editor
{
    [Serializable]
    public sealed class BundleAnalysis
    {
        public string Name;
        public ResourceFileType FileType;
        public string[] ExplicitAssets;
        public string[] IncludedAssets;
        public string[] Dependencies;
        public long SourceBytes; // 源文件大小之和；不是压缩后大小，也不是运行时内存占用。
        public long BuiltBytes;
    }

    [Serializable]
    public sealed class SharedDependency
    {
        public string AssetPath;
        public string[] Consumers;
        public bool Extracted;
    }

    /// <summary>构建前后均可导出的诊断报告；预分析依赖与 Unity 实际构建依赖明确区分。</summary>
    [Serializable]
    public sealed class ResourceBuildReport
    {
        public string PackageName;
        public string PackageVersion;
        public string BuildTarget;
        public string GeneratedUtc;
        public bool Built;
        public ResourceBuildBackend BuildBackend;
        public bool ForceRebuild;
        public bool UseAssetDependencies;
        public bool GenerateLinkXml;
        public bool StripUnityVersion;
        public bool DisableWriteTypeTree;
        public string CacheServerHost;
        public int CacheServerPort;
        public string LinkXmlFile;
        public int DependencyQueryCount;
        public int DependencyCacheHitCount;
        public string UnityVersion;
        public string BackendVersion;
        public bool NativeBuildExecuted;
        public bool SbpCacheEnabled;
        public bool BuiltinCacheEnabled;
        public bool BuiltinCacheRestored;
        public int BuiltinUnchangedBundleCount;
        public string BuiltinCacheDirectory;
        public string DependencyScope;
        public double NativeBuildMilliseconds;
        public string NativeBuildLog;
        public ResourceBundleCompression Compression;
        public string EncryptionKeyId;
        public List<string> Errors = new();
        public List<string> Warnings = new();
        public AssetInfo[] Assets = Array.Empty<AssetInfo>();
        public BundleAnalysis[] Bundles = Array.Empty<BundleAnalysis>();
        public SharedDependency[] SharedDependencies = Array.Empty<SharedDependency>();
        public SharedDependency[] RemainingDuplicates = Array.Empty<SharedDependency>();
        public BundleInfo[] ActualBundles = Array.Empty<BundleInfo>();
        public string[] CompletedSteps = Array.Empty<string>();
        public string[] ExtensionSteps = Array.Empty<string>();
        public ResourceBuildQualityResult Quality;
        public bool IsValid
        {
            get
            {
                return Errors.Count == 0;
            }
        }

        public void Save(string path) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))); File.WriteAllText(path, JsonUtility.ToJson(this, true)); }
    }

    /// <summary>单次分析的快照，真实构建和编辑器模拟都从这里取得同一份地址与依赖。</summary>
    public sealed class ResourceBuildPlan
    {
        public ResourceBuildReport Report { get; }
        public AssetBundleBuild[] Builds { get; }
        private ResourceDependencyGraph m_dependencyGraph;
        internal ResourceDependencyGraph DependencyGraph
        {
            get
            {
                return m_dependencyGraph ??= new ResourceDependencyGraph(CreateManifest(Report.BuildTarget));
            }
        }

        internal ResourceBuildPlan(ResourceBuildReport report, AssetBundleBuild[] builds) { Report = report; Builds = builds; }
        public void ThrowIfInvalid()
        {
            if (!Report.IsValid) {
                throw new InvalidOperationException("资源校验失败：\n" + string.Join("\n", Report.Errors));
            }
        }

        public ResourceManifest CreateSimulationManifest()
        {
            ThrowIfInvalid();
            ResourceManifest manifest = CreateManifest("EditorSimulation");
            manifest.Validate();
            return manifest;
        }
        internal ResourceManifest CreateManifest(string target)
        {
            // Report is editable by build extensions: rebuild this index per snapshot, never cache stale entries.
            var rawHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (AssetInfo asset in Report.Assets) {
                if (asset.FileType == ResourceFileType.RawFile && !rawHashes.ContainsKey(asset.BundleName)) {
                    rawHashes.Add(asset.BundleName, asset.FileSha256);
                }
            }
            var manifest = new ResourceManifest
            {
                FormatVersion = Report.UseAssetDependencies ? 6 : Report.Assets.Any(a => a.Kind == ResourceKind.RawFile) ? 4 : string.IsNullOrEmpty(Report.PackageName) ? 2 : 3,
                PackageName = Report.PackageName,
                PackageVersion = Report.PackageVersion,
                BuildTarget = target,
                Assets = Report.Assets.Where(a => !a.IsDependencyOnly).ToArray(),
                Bundles = Report.Bundles.Select(b => new BundleInfo
                {
                    Name = b.Name,
                    Dependencies = b.Dependencies,
                    FileType = b.FileType,
                    Size = b.FileType == ResourceFileType.AssetBundle ? 0 : b.SourceBytes + (b.FileType == ResourceFileType.Archive ? ResourceArchive.HeaderSize : 0),
                    Sha256 = b.FileType == ResourceFileType.RawFile ? rawHashes[b.Name] : null
                }).ToArray()
            };
            return manifest;
        }
    }

    public static class ResourceBuildAnalyzer
    {
        /// <summary>收集 → 分包 → 分析 → 校验。只读取 AssetDatabase，不写产物。</summary>
        public static ResourceBuildPlan Analyze(BundleBuildConfig config, BuildTarget target)
        {
            var report = new ResourceBuildReport { BuildTarget = target.ToString(), GeneratedUtc = DateTime.UtcNow.ToString("O") };
            if (config != null && !string.IsNullOrEmpty(config.PackageName)) {
                report.PackageName = config.PackageName;
                report.PackageVersion = config.PackageVersion;
                if (!ResourcePackageIdentity.IsValidName(config.PackageName)) {
                    report.Errors.Add("Package 名格式无效。");
                }
            }
            var dependencyQuery = new ResourceBuildDependencyQuery();
            report.Assets = AssetCollector.Collect(config, report.Errors, report.Warnings, dependencyQuery);
            report.BuildBackend = config == null ? ResourceBuildBackend.BuiltIn : config.BuildBackend;
            report.ForceRebuild = config != null && config.ForceRebuild;
            report.UseAssetDependencies = config != null && config.UseAssetDependencies;
            report.GenerateLinkXml = config != null && config.GenerateLinkXml;
            report.StripUnityVersion = config != null && config.StripUnityVersion;
            report.DisableWriteTypeTree = config != null && config.DisableWriteTypeTree;
            report.CacheServerHost = string.IsNullOrWhiteSpace(config?.CacheServerHost) ? null : config.CacheServerHost.Trim();
            report.CacheServerPort = config == null ? 8126 : config.CacheServerPort;
            if (report.UseAssetDependencies && !ResourcePackageIdentity.IsValidName(report.PackageName)) {
                report.Errors.Add("资源粒度依赖要求命名 Package。");
            }

            if (report.CacheServerHost != null && (report.CacheServerPort < 1 || report.CacheServerPort > 65535)) {
                report.Errors.Add("缓存服务器端口必须在 1–65535 范围内。");
            }

            report.UnityVersion = Application.unityVersion;
            if (!Enum.IsDefined(typeof(ResourceBuildBackend), report.BuildBackend)) {
                report.Errors.Add("未知构建后端。");
            }

            report.Compression = config == null ? ResourceBundleCompression.Lz4 : config.Compression;
            report.EncryptionKeyId = config ? config.EncryptionKeyId : null;
            if (!string.IsNullOrEmpty(report.EncryptionKeyId) &&
                (!ResourceEncryption.IsValidKeyId(report.EncryptionKeyId) || !ResourcePackageIdentity.IsValidName(report.PackageName))) {
                report.Errors.Add("加密要求命名 Package 和有效密钥标识（ASCII 字母、数字、下划线、连字符，最长 64 字符）。");
            }

            if (!Enum.IsDefined(typeof(ResourceBundleCompression), report.Compression)) {
                report.Errors.Add("未知 Bundle 压缩格式。");
            }

            var fileTypes = report.Assets.GroupBy(a => a.BundleName).ToDictionary(g => g.Key, g => g.First().FileType, StringComparer.Ordinal);
            foreach (IGrouping<string, AssetInfo> group in report.Assets.GroupBy(a => a.BundleName)) {
                if (group.Select(a => a.FileType).Distinct().Count() != 1) {
                    report.Errors.Add("不同文件格式不能混包：" + group.Key);
                }

                if (group.First().FileType == ResourceFileType.RawFile && group.Select(a => a.AssetPath).Distinct().Count() != 1) {
                    report.Errors.Add("RawFile 一个容器只能包含一个源文件；多文件请使用 Archive：" + group.Key);
                }
            }
            RawFileBuildPipeline.DescribeEntries(report.Assets, report.Errors);
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (AssetInfo asset in report.Assets) {
                owners[asset.AssetPath] = asset.BundleName;
            }

            foreach (IGrouping<string, AssetInfo> group in report.Assets.GroupBy(a => a.BundleName)) {
                if (group.Select(a => a.Kind).Distinct().Count() > 1) {
                    report.Errors.Add($"场景与普通资源不能混包：{group.Key}");
                }
            }

            // 先找出未显式分包的多包依赖，记录原始消费者，便于解释自动提取的原因。
            var consumers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> root in owners.Where(p => fileTypes[p.Value] == ResourceFileType.AssetBundle).ToArray()) {
                foreach (var dependency in dependencyQuery.GetDependencies(root.Key, true)) {
                    if (owners.ContainsKey(dependency) || !AssetCollector.IsPackable(dependency)) {
                        continue;
                    }

                    if (!consumers.TryGetValue(dependency, out HashSet<string> set)) {
                        consumers.Add(dependency, set = new HashSet<string>(StringComparer.Ordinal));
                    }

                    set.Add(root.Value);
                }
            }

            report.SharedDependencies = consumers.Where(p => p.Value.Count > 1).OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new SharedDependency
                {
                    AssetPath = p.Key,
                    Consumers = p.Value.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                    Extracted = config != null && config.ExtractSharedDependencies
                }).ToArray();
            foreach (SharedDependency shared in report.SharedDependencies.Where(s => s.Extracted)) {
                owners[shared.AssetPath] = "shared_" + AssetDatabase.AssetPathToGUID(shared.AssetPath) + ".bundle";
                fileTypes[owners[shared.AssetPath]] = ResourceFileType.AssetBundle;
            }
            if (config != null && config.CollectShadersSeparately) {
                foreach (var path in owners.Keys.Concat(consumers.Keys).Distinct().ToArray()) {
                    if ((!owners.TryGetValue(path, out var owner) || fileTypes[owner] == ResourceFileType.AssetBundle) &&
                        AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(Shader)) {
                        const string shaders = "shaders.bundle";
                        owners[path] = shaders; fileTypes[shaders] = ResourceFileType.AssetBundle;
                        foreach (AssetInfo asset in report.Assets.Where(a => a.AssetPath == path)) {
                            asset.BundleName = shaders;
                        }
                    }
                }
            }

            // 命名空间必须参与 Unity 构建，不能在构建后仅重命名文件。
            if (ResourcePackageIdentity.IsValidName(report.PackageName)) {
                string Qualify(string name)
                {
                    return ResourcePackageIdentity.QualifyBundle(report.PackageName, name);
                }

                foreach (var path in owners.Keys.ToArray()) {
                    owners[path] = Qualify(owners[path]);
                }

                foreach (AssetInfo asset in report.Assets) {
                    asset.BundleName = Qualify(asset.BundleName);
                }

                foreach (SharedDependency shared in report.SharedDependencies) {
                    shared.Consumers = shared.Consumers.Select(Qualify).ToArray();
                }

                fileTypes = fileTypes.ToDictionary(pair => Qualify(pair.Key), pair => pair.Value, StringComparer.Ordinal);
            }

            var bundles = new List<BundleAnalysis>();
            foreach (IGrouping<string, KeyValuePair<string, string>> group in owners.GroupBy(p => p.Value).OrderBy(g => g.Key, StringComparer.Ordinal)) {
                var included = new HashSet<string>(StringComparer.Ordinal);
                var dependencies = new HashSet<string>(StringComparer.Ordinal);
                var pending = new Stack<string>(group.Select(asset => asset.Key));
                while (pending.Count > 0) {
                    var path = pending.Pop();
                    // 遇到其他显式 Bundle 即停止遍历；其后续依赖由那个 Bundle 自己声明。
                    if (owners.TryGetValue(path, out var owner) && owner != group.Key) {
                        if (fileTypes[owner] != ResourceFileType.AssetBundle) {
                            report.Errors.Add("Unity 资源不能依赖已配置为原始文件的资源：" + path);
                        }

                        dependencies.Add(owner); continue;
                    }
                    if (!included.Add(path) || fileTypes[group.Key] != ResourceFileType.AssetBundle) {
                        continue;
                    }

                    foreach (var dependency in dependencyQuery.GetDependencies(path, false)) {
                        if (dependency != path && AssetCollector.IsPackable(dependency)) {
                            pending.Push(dependency);
                        }
                    }
                }
                bundles.Add(new BundleAnalysis
                {
                    Name = group.Key,
                    FileType = fileTypes[group.Key],
                    ExplicitAssets = group.Select(p => p.Key).OrderBy(p => p, StringComparer.Ordinal).ToArray(),
                    IncludedAssets = included.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
                    Dependencies = dependencies.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
                    SourceBytes = included.Sum(p => File.Exists(p) ? new FileInfo(p).Length : 0)
                });
            }
            report.Bundles = bundles.ToArray();
            if (report.UseAssetDependencies) {
                foreach (AssetInfo asset in report.Assets) {
                    var dependencies = new HashSet<string>(StringComparer.Ordinal);
                    if (asset.Kind != ResourceKind.RawFile) {
                        foreach (var path in dependencyQuery.GetDependencies(asset.AssetPath, true)) {
                            if (owners.TryGetValue(path, out var owner) && owner != asset.BundleName) {
                                dependencies.Add(owner);
                            }
                        }
                    }

                    asset.DependencyBundles = dependencies.OrderBy(name => name, StringComparer.Ordinal).ToArray();
                }
            }
            report.DependencyQueryCount = dependencyQuery.QueryCount;
            report.DependencyCacheHitCount = dependencyQuery.CacheHitCount;
            report.RemainingDuplicates = bundles.SelectMany(b => b.IncludedAssets.Select(p => (Path: p, Bundle: b.Name)))
                .GroupBy(p => p.Path).Where(g => g.Select(p => p.Bundle).Distinct().Count() > 1)
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new SharedDependency
                { AssetPath = g.Key, Consumers = g.Select(p => p.Bundle).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray() }).ToArray();
            foreach (SharedDependency duplicate in report.RemainingDuplicates) {
                report.Warnings.Add($"隐式重复资源：{duplicate.AssetPath}（{duplicate.Consumers.Length} 个包）");
            }
            // 预分析环路是构建前错误，不能等运行时加载才发现死循环。
            var plan = new ResourceBuildPlan(report, bundles.Where(b => b.FileType == ResourceFileType.AssetBundle)
                .Select(b => new AssetBundleBuild { assetBundleName = b.Name, assetNames = b.ExplicitAssets }).ToArray());
            ResourceManifest manifest = plan.CreateManifest(target.ToString());
            try { manifest.Validate(); }
            catch (Exception exception) { report.Errors.Add(exception.Message); }
            report.CompletedSteps = new[] { "Collect", "Pack", "Analyze", "Validate" };
            return plan;
        }
    }
}

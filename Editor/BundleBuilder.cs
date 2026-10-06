using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace ZRAsset.Editor
{
    /// <summary>执行已验证的资源计划；先在隔离目录构建，成功后再发布清单与报告。</summary>
    public static class BundleBuilder
    {
        private static bool s_building;
        private sealed class ConfigLease: ScriptableObject
        {
            // 用 Unity 可追踪的对象引用保护已保存配置，避免 SBP 场景切换使调用方的包装对象失效。
            public BundleBuildConfig Config;
            public ResourceBuildQualityPolicy QualityPolicy;
            public TextAsset QualityBaseline;
        }
        public static string GetDefaultOutput(BundleBuildConfig config, BuildTarget target)
        {
            var root = Path.GetFullPath($"Build/ZRAsset/{target}");
            if (config == null || string.IsNullOrEmpty(config.PackageName)) {
                return root;
            }
            ResourcePackageIdentity.ValidateName(config.PackageName);
            // 分析器同时校验版本安全性，不让无效版本影响输出路径。
            return !ResourcePackageIdentity.IsValidVersion(config.PackageVersion)
                ? throw new ArgumentException("无效 Package 版本。")
                : Path.Combine(root, "Packages", config.PackageName, config.PackageVersion);
        }

        public static string GetDefaultBuiltInRoot(BundleBuildConfig config)
        {
            var root = Path.Combine(Application.streamingAssetsPath, "ZRAsset");
            if (config == null || string.IsNullOrEmpty(config.PackageName)) {
                return root;
            }
            ResourcePackageIdentity.ValidateName(config.PackageName);
            return Path.Combine(root, "Packages", config.PackageName);
        }

        internal static void CheckDestinationPackage(string directory, string packageName)
        {
            var path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path)) {
                return;
            }
            var existing = ResourceManifest.FromJson(File.ReadAllText(path));
            if ((existing.PackageName ?? "") != (packageName ?? "")) {
                throw new InvalidOperationException("输出目录属于其他 Package，不能覆盖：" + directory);
            }
        }
        [MenuItem("ZRAsset/构建所选资源配置")]
        public static void BuildSelected()
        {
            var config = Selection.activeObject as BundleBuildConfig;
            if (!config) {
                throw new InvalidOperationException("请先选中 ZRAsset Build Config。");
            }
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            var output = GetDefaultOutput(config, target);
            Build(config, output, target);
            Debug.Log($"ZRAsset 构建完成：{output}");
        }

        [MenuItem("ZRAsset/拷贝当前构建到首包目录")]
        public static void CopyCurrentBuild()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            var config = Selection.activeObject as BundleBuildConfig;
            var source = GetDefaultOutput(config, target);
            var destination = GetDefaultBuiltInRoot(config);
            ResourceManifest manifest = CopyBuild(source, destination, target);
            AssetDatabase.Refresh();
            Debug.Log($"已拷贝 {manifest.Bundles.Length} 个 Bundle 到 {destination}。历史文件保留，运行时只读取当前清单。");
        }

        /// <summary>校验发布目录中的清单、大小和 SHA-256，全部通过后再拷贝，最后发布清单。</summary>
        public static ResourceManifest CopyBuild(string source, string destination, BuildTarget target, IResourceKeyProvider manifestKeys = null)
        { return CopyBuildCore(source, destination, target, null, manifestKeys); }
        /// <summary>导出选择的完整 Bundle 闭包及真实首包清单；目标必须是空目录，避免残留未选文件。</summary>
        public static ResourceManifest CopySelectedBuild(string source, string destination, BuildTarget target, ResourceSelection selection, IResourceKeyProvider manifestKeys = null)
        {
            return selection == null
                ? throw new ArgumentNullException(nameof(selection))
                : CopyBuildCore(source, destination, target, selection, manifestKeys);
        }

        private static ResourceManifest CopyBuildCore(string source, string destination, BuildTarget target, ResourceSelection selection, IResourceKeyProvider manifestKeys)
        {
            if (EditorApplication.isPlaying) {
                throw new InvalidOperationException("请先退出 Play Mode。");
            }
            source = Path.GetFullPath(source);
            destination = Path.GetFullPath(destination);
            var manifestPath = Path.Combine(source, "manifest.json");
            if (!File.Exists(manifestPath)) {
                throw new FileNotFoundException($"构建清单不存在，请先构建 {target} 资源：{manifestPath}", manifestPath);
            }
            var manifest = ResourceManifest.FromJson(File.ReadAllText(manifestPath));
            if (manifest.BuildTarget != target.ToString()) {
                throw new InvalidDataException($"资源构建平台不匹配：清单为 {manifest.BuildTarget}，当前为 {target}。请重新构建当前平台资源。\n{manifestPath}");
            }
            if (selection != null) {
                if (destination.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                    destination.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidOperationException("选择性首包输出不能位于源构建目录中。");
                }
                if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) {
                    throw new InvalidOperationException("选择性首包输出必须是空目录；请使用新的输出目录。");
                }
                ResourceSelectionResult selected = new ResourceCatalog(manifest).Select(selection);
                var names = new System.Collections.Generic.HashSet<string>(selected.BundleNames, StringComparer.Ordinal);
                // A copied bundle also exposes LoadAllAssets; retain all of its physical dependencies.
                var dependencyGraph = new ResourceDependencyGraph(manifest);
                foreach (var name in selected.BundleNames) {
                    names.UnionWith(dependencyGraph.GetBundleClosure(name));
                }
                manifest.Bundles = manifest.Bundles.Where(bundle => names.Contains(bundle.Name)).ToArray();
                // 同一物理 Bundle 的其他条目也已随首包分发，不能在清单中假装它们不存在。
                manifest.Assets = manifest.Assets.Where(asset => names.Contains(asset.BundleName)).ToArray();
                manifest.Validate();
            }
            // 拷贝前检查全部文件，避免发现缺包时已覆盖了一半目标目录。
            // GetCRCForAssetBundle 读取的是 Unity 的 .manifest 附属文件，并不计算 Bundle 的 CRC。
            // 发布目录只保留 Bundle 和我们自己的清单；用构建时记录的 SHA-256 校验文件，CRC 留给运行时加载校验。
            foreach (BundleInfo bundle in manifest.Bundles) {
                var path = Path.Combine(source, bundle.Name);
                if (!File.Exists(path)) {
                    throw new FileNotFoundException($"构建文件缺失：{bundle.Name}。请重新构建后再拷贝。\n{path}", path);
                }
                var size = new FileInfo(path).Length;
                if (size != bundle.Size) {
                    throw new InvalidDataException($"构建文件大小不匹配：{bundle.Name}，清单 {bundle.Size} 字节，实际 {size} 字节。请重新构建后再拷贝。\n{path}");
                }
                if (string.IsNullOrEmpty(bundle.Sha256)) {
                    throw new InvalidDataException($"构建清单缺少 SHA-256：{bundle.Name}。请重新构建以生成完整校验信息。\n{manifestPath}");
                }
                var sha256 = ComputeSha256(path);
                if (!string.Equals(sha256, bundle.Sha256, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException($"构建文件 SHA-256 不匹配：{bundle.Name}。文件已变化或损坏，请重新构建后再拷贝。\n清单：{bundle.Sha256}\n实际：{sha256}\n{path}");
                }
            }
            // 先完成源产物校验，保持旧接口的具体错误提示；身份检查仍在任何写入之前。
            CheckDestinationPackage(destination, manifest.PackageName);
            // 从已校验且可能已裁剪的 JSON 生成，避免首包二进制目录仍引用未导出的资源。
            var binary = File.Exists(Path.Combine(source, "manifest.zrmb")) ? ResourceManifestBinary.Serialize(manifest) : null;
            var encodedPath = Path.Combine(source, "manifest.zrme");
            string encoded = null;
            if (File.Exists(encodedPath)) {
                var original = File.ReadAllText(encodedPath);
                var keyId = ResourceManifestEnvelope.ReadKeyId(original);
                var key = ResourceBuildEncryption.ResolveKey(keyId, manifestKeys);
                try {
                    using ResourceKeyRing ring = key == null ? null : new ResourceKeyRing(new System.Collections.Generic.Dictionary<string, byte[]> { [keyId] = key });
                    ResourceManifest decoded = ResourceManifestEnvelope.Decode(original, ring);
                    if (JsonUtility.ToJson(decoded) != JsonUtility.ToJson(ResourceManifest.FromJson(File.ReadAllText(manifestPath)))) {
                        throw new InvalidDataException("编码清单与发布清单不一致。");
                    }
                    encoded = selection == null ? original : ResourceManifestEnvelope.Encode(manifest, keyId: keyId, keys: ring);
                }
                finally { if (key != null) { Array.Clear(key, 0, key.Length); } }
            }
            var sourceLinkXml = Path.Combine(source, "link.xml");
            if (File.Exists(sourceLinkXml)) {
                ResourceLinker.ReadLinkXml(sourceLinkXml);
            }
            Directory.CreateDirectory(destination);
            foreach (BundleInfo bundle in manifest.Bundles) {
                File.Copy(Path.Combine(source, bundle.Name), Path.Combine(destination, bundle.Name), true);
            }
            var binaryPath = Path.Combine(destination, "manifest.zrmb");
            if (binary != null) {
                File.WriteAllBytes(binaryPath, binary);
            }
            else if (File.Exists(binaryPath)) {
                File.Delete(binaryPath);
            }
            var encodedDestination = Path.Combine(destination, "manifest.zrme");
            if (encoded != null) {
                File.WriteAllText(encodedDestination, encoded);
            }
            else if (File.Exists(encodedDestination)) {
                File.Delete(encodedDestination);
            }
            var targetLinkXml = Path.Combine(destination, "link.xml");
            if (File.Exists(sourceLinkXml)) {
                File.Copy(sourceLinkXml, targetLinkXml, true);
            }
            else if (File.Exists(targetLinkXml)) {
                File.Delete(targetLinkXml);
            }
            if (selection == null) {
                File.Copy(manifestPath, Path.Combine(destination, "manifest.json"), true);
            }
            else {
                File.WriteAllText(Path.Combine(destination, "manifest.json"), JsonUtility.ToJson(manifest, true));
            }
            return manifest;
        }

        public static ResourceManifest Build(BundleBuildConfig config, string output, BuildTarget target, IResourceKeyProvider encryptionKeys = null,
            IEnumerable<IResourceBuildTask> tasks = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EditorApplication.isPlaying) {
                throw new InvalidOperationException("请先退出 Play Mode。");
            }
            if (s_building || BuildPipeline.isBuildingPlayer) {
                throw new InvalidOperationException("已有构建正在进行，不能重入资源构建。");
            }
            var protect = config != null && !AssetDatabase.Contains(config);
            HideFlags flags = protect ? config.hideFlags : HideFlags.None;
            ResourceBuildQualityPolicy qualityPolicy = config?.QualityPolicy;
            TextAsset qualityBaseline = qualityPolicy?.BaselineManifest;
            var protectPolicy = qualityPolicy != null && !AssetDatabase.Contains(qualityPolicy);
            var protectBaseline = qualityBaseline != null && !AssetDatabase.Contains(qualityBaseline);
            HideFlags policyFlags = protectPolicy ? qualityPolicy.hideFlags : HideFlags.None;
            HideFlags baselineFlags = protectBaseline ? qualityBaseline.hideFlags : HideFlags.None;
            ConfigLease lease = null;
            s_building = true;
            try {
                if (config != null) {
                    lease = ScriptableObject.CreateInstance<ConfigLease>();
                    lease.hideFlags = HideFlags.HideAndDontSave; lease.Config = config;
                    lease.QualityPolicy = qualityPolicy;
                    lease.QualityBaseline = qualityBaseline;
                }
                // SBP 会恢复/重建场景；DontSave 同时覆盖场景切换与 UnloadUnusedAssets，保护临时配置。
                if (protect) {
                    config.hideFlags |= HideFlags.DontSave;
                }
                if (protectPolicy) {
                    qualityPolicy.hideFlags |= HideFlags.DontSave;
                }
                if (protectBaseline) {
                    qualityBaseline.hideFlags |= HideFlags.DontSave;
                }
                return BuildCore(config, output, target, encryptionKeys, tasks, cancellationToken);
            }
            finally {
                if (protect && config != null) {
                    config.hideFlags = flags;
                }
                if (protectPolicy && qualityPolicy != null) {
                    qualityPolicy.hideFlags = policyFlags;
                }
                if (protectBaseline && qualityBaseline != null) {
                    qualityBaseline.hideFlags = baselineFlags;
                }
                if (lease != null) {
                    UnityEngine.Object.DestroyImmediate(lease);
                }
                s_building = false;
            }
        }

        private static ResourceManifest BuildCore(BundleBuildConfig config, string output, BuildTarget target, IResourceKeyProvider encryptionKeys,
            IEnumerable<IResourceBuildTask> tasks, CancellationToken token)
        {
            ResourceBuildPlan plan = ResourceBuildAnalyzer.Analyze(config, target);
            plan.ThrowIfInvalid();
            using var shaderScope = new ResourceShaderBuildScope(config);
            // 裁剪策略不是 Unity/SBP 的内置缓存依赖；有策略时禁止复用旧 Shader 编译产物。
            if (config.ShaderVariants != null && (config.ShaderVariants.StripUnlistedVariants || config.ShaderVariants.ExcludedKeywords?.Length > 0)) {
                plan.Report.ForceRebuild = true;
            }
            bool reuseEncryption = config.ReuseEncryptedArtifacts, binaryManifest = config.ExportBinaryManifest;
            var encodedManifest = config.ExportEncodedManifest;
            var manifestKeyId = config.ManifestEncryptionKeyId;
            ResourceBuildQuality.PolicySnapshot qualityPolicy = ResourceBuildQuality.Capture(config.QualityPolicy);
            output = Path.GetFullPath(output);
            CheckDestinationPackage(output, plan.Report.PackageName);
            var assetRoot = Path.GetFullPath(Application.dataPath);
            if (output.Equals(assetRoot, StringComparison.OrdinalIgnoreCase) || output.StartsWith(assetRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException("构建目录不能位于 Assets 内，请通过拷贝命令部署到 StreamingAssets。");
            }
            // 临时目录固定在项目 Build 下，每次使用独立 GUID，只清理本次创建的目录。
            var workRoot = Path.GetFullPath("Build/ZRAssetBuildWork");
            var work = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
            var stage = Path.Combine(work, "payload");
            Directory.CreateDirectory(stage);
            byte[] encryptionKey = null;
            try {
                var context = new ResourceBuildContext(plan, stage, target,
                    (config.BuildExtensions ?? Array.Empty<ResourceBuildExtension>()).Cast<IResourceBuildTask>().Concat(tasks ?? Array.Empty<IResourceBuildTask>()), token);
                context.Run(ResourceBuildStage.PlanReady);
                plan.ThrowIfInvalid();
                encryptionKey = ResourceBuildEncryption.ResolveKey(plan.Report.EncryptionKeyId, encryptionKeys);
                BundleInfo[] bundles = ResourceBundleBuildPipeline.Build(plan, stage, target)
                    .Concat(RawFileBuildPipeline.Build(plan, stage)).OrderBy(b => b.Name, StringComparer.Ordinal).ToArray();
                ResourceManifest manifest = plan.CreateManifest(target.ToString()); manifest.Bundles = bundles;
                ResourceBundleBuildPipeline.CompleteAssetDependencies(plan, manifest);
                context.Manifest = manifest;
                context.Run(ResourceBuildStage.FilesBuilt);
                context.ValidateFiles();
                bundles = manifest.Bundles;
                ResourceBuildEncryption.Encrypt(stage, bundles, plan.Report.EncryptionKeyId, encryptionKey, reuseEncryption);
                if (encryptionKey != null) {
                    manifest.FormatVersion = Math.Max(5, manifest.FormatVersion);
                }
                context.Run(ResourceBuildStage.FilesEncrypted);
                manifest.Validate();
                context.Run(ResourceBuildStage.ManifestReady);
                manifest.Validate();
                bundles = manifest.Bundles;
                plan.Report.ActualBundles = bundles;
                Dictionary<string, BundleAnalysis> predictedBundles = plan.Report.Bundles.ToDictionary(b => b.Name, StringComparer.Ordinal);
                foreach (BundleInfo bundle in bundles) {
                    BundleAnalysis predicted = predictedBundles[bundle.Name];
                    predicted.BuiltBytes = bundle.Size;
                    if (!ResourceBundleBuildPipeline.PredictedDependencies(plan, predicted).OrderBy(n => n, StringComparer.Ordinal)
                        .SequenceEqual(bundle.Dependencies.OrderBy(n => n, StringComparer.Ordinal))) {
                        plan.Report.Warnings.Add($"{bundle.Name} 的 Unity 实际依赖与预分析不同；运行时使用实际构建清单。");
                    }
                }
                plan.Report.Built = true;
                if (qualityPolicy != null) {
                    plan.Report.Quality = ResourceBuildQuality.Evaluate(manifest, plan.Report, qualityPolicy);
                    plan.Report.Quality.ThrowIfFailed();
                }
                context.Run(ResourceBuildStage.QualityPassed);
                plan.Report.CompletedSteps = encryptionKey == null ?
                    new[] { "Collect", "Pack", "Analyze", "Validate", "BuildBundles", "GenerateManifest", "ExportReport" } :
                    new[] { "Collect", "Pack", "Analyze", "Validate", "BuildBundles", "EncryptFiles", "GenerateManifest", "ExportReport" };
                File.WriteAllText(Path.Combine(stage, "manifest.json"), JsonUtility.ToJson(manifest, true));
                if (binaryManifest) {
                    File.WriteAllBytes(Path.Combine(stage, "manifest.zrmb"), ResourceManifestBinary.Serialize(manifest));
                }
                if (encodedManifest) {
                    var manifestKey = ResourceBuildEncryption.ResolveKey(manifestKeyId, encryptionKeys);
                    try {
                        using ResourceKeyRing ring = manifestKey == null ? null : new ResourceKeyRing(new System.Collections.Generic.Dictionary<string, byte[]> { [manifestKeyId] = manifestKey });
                        File.WriteAllText(Path.Combine(stage, "manifest.zrme"), ResourceManifestEnvelope.Encode(manifest, keyId: manifestKeyId, keys: ring));
                    }
                    finally {
                        if (manifestKey != null) {
                            Array.Clear(manifestKey, 0, manifestKey.Length);
                        }
                    }
                }
                plan.Report.Save(Path.Combine(stage, "build-report.json"));

                Dictionary<string, string> metadata = context.HasTasks ? new[] { "manifest.json", "manifest.zrmb", "manifest.zrme", "link.xml" }
                    .Where(name => File.Exists(Path.Combine(stage, name))).ToDictionary(name => name, name => ComputeSha256(Path.Combine(stage, name))) : null;
                context.Run(ResourceBuildStage.BeforePublish);
                if (context.HasTasks && metadata.Any(pair => !File.Exists(Path.Combine(stage, pair.Key)) || ComputeSha256(Path.Combine(stage, pair.Key)) != pair.Value)) {
                    throw new InvalidDataException("BeforePublish 不能修改已导出的清单。");
                }
                context.ValidateArtifacts();
                context.ValidateFiles();
                if (context.HasTasks && qualityPolicy != null) {
                    plan.Report.Quality = ResourceBuildQuality.Evaluate(manifest, plan.Report, qualityPolicy);
                    plan.Report.Quality.ThrowIfFailed();
                }
                plan.Report.Save(Path.Combine(stage, "build-report.json"));
                token.ThrowIfCancellationRequested();

                // 构建或校验失败不会碰旧产物。这里是开发期发布，不承诺进程崩溃时的原子热更新。
                Directory.CreateDirectory(output);
                foreach (var artifact in context.Artifacts) { File.Copy(Path.Combine(stage, artifact), Path.Combine(output, artifact), true); }
                foreach (BundleInfo bundle in bundles) {
                    File.Copy(Path.Combine(stage, bundle.Name), Path.Combine(output, bundle.Name), true);
                }
                if (!string.IsNullOrEmpty(plan.Report.NativeBuildLog)) {
                    File.Copy(Path.Combine(stage, plan.Report.NativeBuildLog), Path.Combine(output, plan.Report.NativeBuildLog), true);
                }
                if (!string.IsNullOrEmpty(plan.Report.LinkXmlFile)) {
                    File.Copy(Path.Combine(stage, plan.Report.LinkXmlFile), Path.Combine(output, plan.Report.LinkXmlFile), true);
                }
                else if (File.Exists(Path.Combine(output, "link.xml"))) {
                    File.Delete(Path.Combine(output, "link.xml"));
                }
                File.Copy(Path.Combine(stage, "build-report.json"), Path.Combine(output, "build-report.json"), true);
                if (binaryManifest) {
                    File.Copy(Path.Combine(stage, "manifest.zrmb"), Path.Combine(output, "manifest.zrmb"), true);
                }
                else if (File.Exists(Path.Combine(output, "manifest.zrmb"))) {
                    File.Delete(Path.Combine(output, "manifest.zrmb"));
                }
                if (encodedManifest) {
                    File.Copy(Path.Combine(stage, "manifest.zrme"), Path.Combine(output, "manifest.zrme"), true);
                }
                else if (File.Exists(Path.Combine(output, "manifest.zrme"))) {
                    File.Delete(Path.Combine(output, "manifest.zrme"));
                }
                File.Copy(Path.Combine(stage, "manifest.json"), Path.Combine(output, "manifest.json"), true);
                return manifest;
            }
            finally {
                if (encryptionKey != null) {
                    Array.Clear(encryptionKey, 0, encryptionKey.Length);
                }
                if (Path.GetFullPath(work).StartsWith(workRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(work)) {
                    Directory.Delete(work, true);
                }
            }
        }

        internal static string ComputeSha256(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream file = File.OpenRead(path)) {
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}

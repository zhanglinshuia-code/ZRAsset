using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using ZRAsset.HotUpdate;
using Object = UnityEngine.Object;

namespace ZRAsset.Editor
{
    /// <summary>将经过元数据检查的 DLL 作为 TextAsset 发布，复用现有资源构建、校验和版本分发。</summary>
    public static class HotUpdateBuilder
    {
        private sealed class PreparedBinary
        {
            public HotUpdateBinary Description;
            public byte[] Bytes;
            public string[] References;
            public bool IsMetadata;
        }

        [Serializable]
        public sealed class BuildReport
        {
            public string BuildTarget;
            public string PlayerBuildId;
            public string ContentVersion;
            public string ManifestAddress;
            public string GeneratedUtc;
            public string[] AssemblyLoadOrder;
            public int AssemblyCount;
            public int MetadataCount;
            public long BinaryBytes;
            public string DependencyValidation = "Unity.Cecil";
            public string[] ExcludedHybridClrAssemblies;
            public string[] Notes;
        }

        [MenuItem("ZRAsset/构建所选热更配置")]
        public static void BuildSelected()
        {
            var config = Selection.activeObject as HotUpdateBuildConfig;
            if (!config) {
                throw new InvalidOperationException("请先选中 ZRAsset Hot Update Build Config。");
            }
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            var output = Path.GetFullPath($"Build/ZRAsset/{target}");
            Build(config, output, target);
            Debug.Log("ZRAsset 热更资源构建完成：" + output);
        }

        /// <summary>先完成所有校验与隔离构建，再发布；导入过程不会 Assembly.Load 用户 DLL。</summary>
        public static ResourceManifest Build(HotUpdateBuildConfig config, string output, BuildTarget target)
        {
            return BuildWithSettings(config, output, target, FindHybridClrSettingsType());
        }

        // 设置类型作为单次输入传入，既不保存全局覆盖状态，也不需要强依赖可选 HybridCLR 包。
        private static ResourceManifest BuildWithSettings(HotUpdateBuildConfig config, string output, BuildTarget target, Type hybridClrSettingsType)
        {
            if (EditorApplication.isPlaying) {
                throw new InvalidOperationException("请先退出 Play Mode。");
            }

            if (config == null) {
                throw new ArgumentNullException(nameof(config));
            }

            if (target != EditorUserBuildSettings.activeBuildTarget) {
                throw new InvalidOperationException("请先切换到发布目标平台，使宿主程序集依赖检查与目标 Player 一致。");
            }

            if (string.IsNullOrWhiteSpace(output)) {
                throw new ArgumentException("必须指定输出目录。", nameof(output));
            }

            output = Path.GetFullPath(output);
            var assetRoot = Path.GetFullPath(Application.dataPath);
            if (output.Equals(assetRoot, StringComparison.OrdinalIgnoreCase) ||
                output.StartsWith(assetRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException("构建目录不能位于 Assets 内。");
            }

            ValidateAddress(config.ManifestAddress);

            // 一次性读取并校验二进制快照，之后打包与哈希都使用同一份字节。
            var binaries = new List<PreparedBinary>();
            foreach (HotUpdateBuildConfig.BinaryInput input in config.AotMetadata ?? new List<HotUpdateBuildConfig.BinaryInput>()) {
                binaries.Add(Prepare(input, true));
            }

            foreach (HotUpdateBuildConfig.AssemblyInput input in config.Assemblies ?? new List<HotUpdateBuildConfig.AssemblyInput>()) {
                binaries.Add(Prepare(input, false));
            }

            var manifest = new HotUpdateManifest
            {
                BuildTarget = target.ToString(),
                PlayerBuildId = config.PlayerBuildId,
                ContentVersion = config.ContentVersion,
                EntryAssembly = config.EntryAssembly,
                EntryType = config.EntryType,
                EntryMethod = config.EntryMethod,
                AotMetadata = binaries.Where(b => b.IsMetadata).Select(b => b.Description).ToArray(),
                Assemblies = binaries.Where(b => !b.IsMetadata).Select(b => (HotUpdateAssembly)b.Description).ToArray()
            };
            manifest.Validate();
            if (binaries.Any(b => b.Description.Address == config.ManifestAddress)) {
                throw new InvalidDataException("热更清单地址与 DLL 地址冲突：" + config.ManifestAddress);
            }

            var hotNames = new HashSet<string>(manifest.Assemblies.Select(a => a.Name), StringComparer.Ordinal);
            HashSet<string> hostNames = GetHostAssemblyNames();
            hostNames.UnionWith(manifest.AotMetadata.Select(a => a.Name));
            HashSet<string> configuredHotNames = ReadEnabledHybridClrAssemblyNames(hybridClrSettingsType);
            foreach (HotUpdateBinary metadata in manifest.AotMetadata) {
                if (configuredHotNames.Contains(metadata.Name)) {
                    throw new InvalidDataException($"程序集 {metadata.Name} 已在 HybridCLR 中声明为热更或预留热更，不能作为 AOT 元数据发布。");
                }
            }
            // Unity 编译图位于 HybridCLR 的 Player 过滤器之前；预留名称也不代表已经存在的宿主 AOT。
            hostNames.ExceptWith(configuredHotNames);
            foreach (PreparedBinary binary in binaries.Where(b => !b.IsMetadata)) {
                IOrderedEnumerable<string> declared = ((HotUpdateAssembly)binary.Description).Dependencies.OrderBy(n => n, StringComparer.Ordinal);
                IOrderedEnumerable<string> actual = binary.References.Where(hotNames.Contains).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal);
                if (!declared.SequenceEqual(actual, StringComparer.Ordinal)) {
                    throw new InvalidDataException($"热更程序集 {binary.Description.Name} 的 Dependencies 与 DLL 引用不一致；实际热更依赖：{string.Join(", ", actual)}。");
                }

                var missing = binary.References.Where(name => !hotNames.Contains(name) && !hostNames.Contains(name)).ToArray();
                if (missing.Length > 0) {
                    throw new InvalidDataException($"热更程序集 {binary.Description.Name} 引用了未发布、也不在当前 Player 编译引用中的程序集：{string.Join(", ", missing)}。请补齐热更输入或宿主程序集配置。");
                }
            }

            ValidateEntry(binaries.Single(b => !b.IsMetadata && b.Description.Name == manifest.EntryAssembly), manifest);
            BundleBuilder.CheckDestinationPackage(output, config.ResourceConfig ? config.ResourceConfig.PackageName : null);

            var id = Guid.NewGuid().ToString("N");
            var fixtureRoot = "Assets/ZRAssetV7Stage";
            var workRoot = Path.GetFullPath("Build/ZRAssetHotUpdateWork");
            var work = Path.Combine(workRoot, id);
            var published = Path.Combine(work, "payload");
            BundleBuildConfig combined = null;
            var fixtureCreated = false;
            try {
                if (Directory.Exists(fixtureRoot) || File.Exists(fixtureRoot) || File.Exists(fixtureRoot + ".meta") || Directory.Exists(work)) {
                    throw new IOException("临时构建目录意外存在。");
                }

                Directory.CreateDirectory(work);
                Directory.CreateDirectory(fixtureRoot);
                fixtureCreated = true;
                var entries = new List<BundleBuildConfig.Entry>();
                for (var index = 0; index < binaries.Count; index++) {
                    PreparedBinary binary = binaries[index];
                    var path = fixtureRoot + "/" + index.ToString("D3") + "_" + binary.Description.Name + ".dll.bytes";
                    File.WriteAllBytes(path, binary.Bytes);
                    WriteStableMeta(path, "binary:" + binary.Description.Address);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    entries.Add(new BundleBuildConfig.Entry
                    {
                        Address = binary.Description.Address,
                        Asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path),
                        BundleName = binary.IsMetadata ? "hotupdate_metadata.bundle" : "hotupdate_code.bundle"
                    });
                }
                var json = JsonUtility.ToJson(manifest, true);
                var manifestPath = fixtureRoot + "/hotupdate-manifest.json";
                File.WriteAllText(manifestPath, json);
                WriteStableMeta(manifestPath, "manifest:" + config.ManifestAddress);
                AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceSynchronousImport);
                entries.Add(new BundleBuildConfig.Entry
                {
                    Address = config.ManifestAddress,
                    Asset = AssetDatabase.LoadAssetAtPath<TextAsset>(manifestPath),
                    BundleName = "hotupdate_manifest.bundle"
                });
                combined = config.ResourceConfig != null ? Object.Instantiate(config.ResourceConfig) : ScriptableObject.CreateInstance<BundleBuildConfig>();
                combined.Entries ??= new List<BundleBuildConfig.Entry>();
                combined.Entries.AddRange(entries);
                // 构建过程中 Unity 可能导入资源，持久配置避免临时 ScriptableObject 被原生侧失效。
                var combinedPath = fixtureRoot + "/BuildConfig.asset";
                AssetDatabase.CreateAsset(combined, combinedPath);
                EditorUtility.SetDirty(combined);
                AssetDatabase.SaveAssets();
                combined = AssetDatabase.LoadAssetAtPath<BundleBuildConfig>(combinedPath);
                ResourceManifest resourceManifest = BundleBuilder.Build(combined, published, target);
                File.WriteAllText(Path.Combine(published, "hotupdate-manifest.json"), json);
                var report = new BuildReport
                {
                    BuildTarget = manifest.BuildTarget,
                    PlayerBuildId = manifest.PlayerBuildId,
                    ContentVersion = manifest.ContentVersion,
                    ManifestAddress = config.ManifestAddress,
                    GeneratedUtc = DateTime.UtcNow.ToString("O"),
                    AssemblyCount = manifest.Assemblies.Length,
                    MetadataCount = manifest.AotMetadata.Length,
                    BinaryBytes = binaries.Sum(b => b.Description.Size),
                    ExcludedHybridClrAssemblies = configuredHotNames.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                    AssemblyLoadOrder = manifest.GetLoadOrder().Select(a => a.Name).ToArray(),
                    Notes = new[]
                    {
                        "DLL 仅作为 TextAsset 打包；此工具不编译 HybridCLR，也不能证明输入来自正确目标平台。",
                        "AOT 元数据必须匹配此 PlayerBuildId 对应的宿主 Player 裁剪产物。",
                        "程序集进入当前进程后不能单独卸载或以资源版本回滚方式替换。"
                    }
                };
                File.WriteAllText(Path.Combine(published, "hotupdate-build-report.json"), JsonUtility.ToJson(report, true));

                // 校验失败与 Unity 构建失败均不会修改旧发布；文件系统发布不承诺断电原子性。
                BundleBuilder.CheckDestinationPackage(output, resourceManifest.PackageName);
                Directory.CreateDirectory(output);
                foreach (BundleInfo bundle in resourceManifest.Bundles) {
                    File.Copy(Path.Combine(published, bundle.Name), Path.Combine(output, bundle.Name), true);
                }

                var sbpLog = Path.Combine(published, "sbp-buildlog.json");
                if (File.Exists(sbpLog)) {
                    File.Copy(sbpLog, Path.Combine(output, "sbp-buildlog.json"), true);
                }

                foreach (var optionalFile in new[] { "link.xml", "manifest.zrmb", "manifest.zrme" }) {
                    var sourceFile = Path.Combine(published, optionalFile);
                    var destinationFile = Path.Combine(output, optionalFile);
                    if (File.Exists(sourceFile)) {
                        File.Copy(sourceFile, destinationFile, true);
                    }
                    else if (File.Exists(destinationFile)) {
                        File.Delete(destinationFile);
                    }
                }
                foreach (var file in new[] { "build-report.json", "hotupdate-manifest.json", "hotupdate-build-report.json", "manifest.json" }) {
                    File.Copy(Path.Combine(published, file), Path.Combine(output, file), true);
                }

                return resourceManifest;
            }
            finally {
                if (combined != null && !AssetDatabase.Contains(combined)) {
                    Object.DestroyImmediate(combined);
                }

                var fixturePath = Path.GetFullPath(fixtureRoot);
                if (fixtureCreated && fixturePath.StartsWith(assetRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                    AssetDatabase.DeleteAsset(fixtureRoot);
                }

                if (Path.GetFullPath(work).StartsWith(workRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(work)) {
                    Directory.Delete(work, true);
                }
            }
        }

        private static void WriteStableMeta(string path, string identity)
        {
            using var sha = SHA256.Create();
            var guid = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("ZRAsset.HotUpdate.v1\n" + identity)))
                .Replace("-", "").Substring(0, 32).ToLowerInvariant();
            var existing = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(existing) && existing != path) {
                throw new InvalidDataException("热更临时资源 GUID 已被其他资源占用：" + existing);
            }

            File.WriteAllText(path + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");
        }

        private static PreparedBinary Prepare(HotUpdateBuildConfig.BinaryInput input, bool metadata)
        {
            if (input == null || string.IsNullOrWhiteSpace(input.FilePath)) {
                throw new InvalidDataException("热更配置包含空 DLL 路径。");
            }

            var path = Path.GetFullPath(input.FilePath);
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("输入必须是已编译的 .dll 文件：" + path);
            }

            if (!File.Exists(path)) {
                throw new FileNotFoundException("未找到热更或 AOT DLL。", path);
            }

            var name = AssemblyName.GetAssemblyName(path).Name;
            if (!string.IsNullOrEmpty(input.Name) && input.Name != name) {
                throw new InvalidDataException($"配置程序集名 {input.Name} 与 DLL 实际名称 {name} 不一致。");
            }

            var bytes = File.ReadAllBytes(path);
            var references = ReadReferences(bytes, name);
            var address = string.IsNullOrWhiteSpace(input.Address)
                ? "hotupdate/" + (metadata ? "aot/" : "dll/") + name + ".dll.bytes" : input.Address;
            ValidateAddress(address);
            string sha256;
            using (var hash = SHA256.Create()) {
                sha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }

            HotUpdateBinary description = metadata ? new HotUpdateBinary() : new HotUpdateAssembly
            {
                Dependencies = ((HotUpdateBuildConfig.AssemblyInput)input).Dependencies?.ToArray()
            };
            description.Name = name; description.Address = address; description.Size = bytes.LongLength; description.Sha256 = sha256;
            return new PreparedBinary { Description = description, Bytes = bytes, References = references, IsMetadata = metadata };
        }

        private static string[] ReadReferences(byte[] bytes, string expectedName)
        {
            return ReadMetadata(bytes, expectedName, definition =>
                    {
                        var module = Property(definition, "MainModule");
                        return ((IEnumerable)Property(module, "AssemblyReferences")).Cast<object>()
                            .Select(reference => (string)Property(reference, "Name")).ToArray();
                    });
        }

        private static object Property(object value, string name)
        {
            // Cecil 的 TypeDefinition 隐藏了基类的 DeclaringType，直接 GetProperty 会产生二义性。
            for (Type type = value.GetType(); type != null; type = type.BaseType) {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (property != null) {
                    return property.GetValue(value);
                }
            }
            throw new InvalidOperationException("Unity.Cecil 缺少元数据属性：" + name);
        }

        private static void ValidateEntry(PreparedBinary binary, HotUpdateManifest manifest)
        {
            ReadMetadata(binary.Bytes, binary.Description.Name, definition =>
            {
                // Cecil 嵌套类型用 '/'，Reflection 使用 '+'。同时验证全部外层类型的可见性和泛型参数。
                var entryType = Types(Property(definition, "MainModule")).FirstOrDefault(type =>
                    ((string)Property(type, "FullName")).Replace('/', '+') == manifest.EntryType) ?? throw new InvalidDataException("热更入口类型不存在：" + manifest.EntryType);
                for (var type = entryType; type != null; type = Property(type, "DeclaringType")) {
                    if ((bool)Property(type, "HasGenericParameters") ||
                        !(bool)Property(type, Property(type, "DeclaringType") == null ? "IsPublic" : "IsNestedPublic")) {
                        throw new InvalidDataException("热更入口必须位于 public、非开放泛型类型中：" + manifest.EntryType);
                    }
                }

                var methods = ((IEnumerable)Property(entryType, "Methods")).Cast<object>().Where(method =>
                    (string)Property(method, "Name") == manifest.EntryMethod && (bool)Property(method, "IsPublic") &&
                    (bool)Property(method, "IsStatic")).ToArray();
                if (methods.Length != 1) {
                    throw new InvalidDataException("热更入口必须有且仅有一个同名 public static 方法：" + manifest.EntryType + "." + manifest.EntryMethod);
                }

                var entry = methods[0];
                var returns = Property(entry, "ReturnType");
                var isVoid = (string)Property(returns, "FullName") == "System.Void";
                if ((bool)Property(entry, "HasGenericParameters") || (!isVoid && !IsRuntimeType(returns, typeof(ResourceOperationBase)))) {
                    throw new InvalidDataException("热更入口必须是非泛型方法，返回 void 或 ResourceOperationBase。");
                }

                if (isVoid && ((IEnumerable)Property(entry, "CustomAttributes")).Cast<object>().Any(attribute =>
                    (string)Property(Property(attribute, "AttributeType"), "FullName") == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")) {
                    throw new InvalidDataException("热更入口不能使用 async void，请返回 ResourceOperationBase。");
                }

                var parameters = ((IEnumerable)Property(entry, "Parameters")).Cast<object>().ToArray();
                return parameters.Length > 1 || (parameters.Length == 1 && !IsRuntimeType(Property(parameters[0], "ParameterType"), typeof(ResourceManager)))
                    ? throw new InvalidDataException("热更入口仅支持无参，或一个 ResourceManager 参数。")
                    : true;
            });
        }

        private static bool IsRuntimeType(object type, Type expected)
        {
            return (string)Property(type, "FullName") == expected.FullName &&
            (string)Property(Property(type, "Scope"), "Name") == expected.Assembly.GetName().Name;
        }

        private static IEnumerable<object> Types(object moduleOrType)
        {
            var property = moduleOrType.GetType().Name == "ModuleDefinition" ? "Types" : "NestedTypes";
            foreach (var type in (IEnumerable)Property(moduleOrType, property)) {
                yield return type;
                foreach (var nested in Types(type)) {
                    yield return nested;
                }
            }
        }

        private static T ReadMetadata<T>(byte[] bytes, string expectedName, Func<object, T> inspect)
        {
            // 只加载 Unity 随 Editor 分发的读取器，不加载正在发布的用户 DLL。
            Type readerType = Type.GetType("Mono.Cecil.AssemblyDefinition, Unity.Cecil", false) ?? throw new InvalidOperationException("未找到 Unity.Cecil，无法验证热更 DLL 依赖；请检查 Unity Editor 安装。");
            object definition = null;
            using var stream = new MemoryStream(bytes, false);
            try {
                definition = (readerType.GetMethod("ReadAssembly", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(Stream) }, null)?.Invoke(null, new object[] { stream })) ?? throw new InvalidOperationException("Unity.Cecil 缺少 ReadAssembly(Stream) 接口。");
                var identity = readerType.GetProperty("Name").GetValue(definition);
                var actualName = (string)identity.GetType().GetProperty("Name").GetValue(identity);
                return actualName != expectedName ? throw new InvalidDataException("读取期间 DLL 身份发生变化，请重新构建。") : inspect(definition);
            }
            catch (TargetInvocationException exception) { throw new InvalidDataException("无法读取 DLL 元数据。", exception.InnerException ?? exception); }
            finally { (definition as IDisposable)?.Dispose(); }
        }

        private static HashSet<string> GetHostAssemblyNames()
        {
            // 标准库、Unity 模块与宿主 asmdef 都从实际 Player 编译图取得，不能把所有未知引用当作 AOT。
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnityEditor.Compilation.Assembly assembly in CompilationPipeline.GetAssemblies(AssembliesType.Player)) {
                result.Add(assembly.name);
                foreach (var reference in assembly.compiledAssemblyReferences) {
                    result.Add(Path.GetFileNameWithoutExtension(reference));
                }
            }
            return result;
        }

        private static Type FindHybridClrSettingsType()
        {
            System.Reflection.Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate => candidate.GetName().Name == "HybridCLR.Editor");
            return assembly == null
                ? null
                : assembly.GetType("HybridCLR.Editor.SettingsUtil", false)
                ?? throw new InvalidOperationException("已加载 HybridCLR.Editor，但缺少 SettingsUtil；无法可靠检查宿主依赖，请检查包版本。");
        }

        private static HashSet<string> ReadEnabledHybridClrAssemblyNames(Type settingsType)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (settingsType == null) {
                return result;
            }

            try {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                PropertyInfo enable = settingsType.GetProperty("Enable", flags);
                if (enable == null || enable.PropertyType != typeof(bool)) {
                    throw new InvalidOperationException("HybridCLR SettingsUtil.Enable 接口不兼容，无法可靠检查宿主依赖。");
                }

                if (!(bool)enable.GetValue(null)) {
                    return result;
                }

                PropertyInfo property = settingsType.GetProperty("HotUpdateAssemblyNamesIncludePreserved", flags);
                if (property == null || !(property.GetValue(null) is IEnumerable names)) {
                    throw new InvalidOperationException("HybridCLR 热更程序集名单接口不兼容，无法可靠检查宿主依赖。");
                }

                foreach (var item in names) {
                    if (!(item is string name) || string.IsNullOrWhiteSpace(name) || name != name.Trim()) {
                        throw new InvalidDataException("HybridCLR 热更程序集名单包含无效名称。");
                    }

                    if (!result.Add(name)) {
                        throw new InvalidDataException("HybridCLR 热更程序集名单存在重复名称：" + name);
                    }
                }
                return result;
            }
            catch (TargetInvocationException exception) {
                throw new InvalidOperationException("读取 HybridCLR 热更设置失败；未跳过依赖检查，请修正设置或包版本。", exception.InnerException ?? exception);
            }
        }

        private static void ValidateAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address) || address != address.Trim() || address.Contains("\\") ||
                address.Split('/').Any(part => part.Length == 0 || part == "." || part == "..")) {
                throw new InvalidDataException("热更资源地址无效：" + address);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace ZRAsset.Editor
{
    /// <summary>只负责把配置展开成资源地址，不执行构建，也不改动 AssetImporter。</summary>
    public static class AssetCollector
    {
        private static HashSet<string> s_editorOnlyAssemblies;

        public static AssetInfo[] Collect(BundleBuildConfig config, List<string> errors, List<string> warnings)
        {
            return Collect(config, errors, warnings, new ResourceBuildDependencyQuery());
        }

        internal static AssetInfo[] Collect(BundleBuildConfig config, List<string> errors, List<string> warnings,
            ResourceBuildDependencyQuery dependencyQuery)
        {
            // 按编译标记识别 Editor 程序集；自定义 asmdef 不一定以 .Editor 结尾。
            s_editorOnlyAssemblies = new HashSet<string>(CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Where(a => (a.flags & AssemblyFlags.EditorAssembly) != 0).Select(a => a.name), StringComparer.Ordinal);
            if (config == null) { errors.Add("未指定打包配置。"); return Array.Empty<AssetInfo>(); }
            var groups = new Dictionary<string, bool>(StringComparer.Ordinal);
            var groupTags = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (BundleBuildConfig.CollectionGroup group in config.Groups ?? new List<BundleBuildConfig.CollectionGroup>()) {
                if (group == null || string.IsNullOrWhiteSpace(group.Name) || group.Name != group.Name.Trim()) { errors.Add("逻辑分组名称不能为空或包含首尾空白。"); continue; }
                if (!groups.TryAdd(group.Name, !group.Disabled)) {
                    errors.Add("逻辑分组名称重复：" + group.Name);
                }
                else {
                    groupTags[group.Name] = group.Tags ?? Array.Empty<string>();
                }
            }
            bool IsActive(string group)
            {
                if (config.GlobalCollectionRule != null && !config.GlobalCollectionRule.IsGroupActive(group)) {
                    return false;
                }

                if (string.IsNullOrEmpty(group)) {
                    return true;
                }

                if (groups.TryGetValue(group, out var active)) {
                    return active;
                }

                errors.Add("收集器引用了不存在的逻辑分组：" + group);
                return false;
            }
            var result = new List<AssetInfo>();
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            var roles = new Dictionary<string, ResourceCollectorRole>(StringComparer.Ordinal);
            foreach (BundleBuildConfig.Entry entry in config.Entries ?? new List<BundleBuildConfig.Entry>()) {
                if (entry != null && (entry.Disabled || !IsActive(entry.CollectionGroup))) {
                    continue;
                }

                if (entry == null || entry.Asset == null) { errors.Add("显式条目缺少资源。"); continue; }
                var path = AssetDatabase.GetAssetPath(entry.Asset);
                if (AssetDatabase.IsSubAsset(entry.Asset)) { errors.Add($"不支持子资源：{path}"); continue; }
                Add(entry.Address, path, string.IsNullOrWhiteSpace(entry.BundleName) ? SeparateName(path, entry.FileType) : entry.BundleName, entry.Tags, entry.CollectionGroup, entry.FileType, entry.CollectorRole, null);
            }

            foreach (BundleBuildConfig.CollectionRule rule in config.Rules ?? new List<BundleBuildConfig.CollectionRule>()) {
                if (rule == null) { errors.Add("存在空收集规则。"); continue; }
                if (rule.Disabled || !IsActive(rule.CollectionGroup)) {
                    continue;
                }

                var folder = AssetDatabase.GetAssetPath(rule.Folder);
                if (!folder.StartsWith("Assets/", StringComparison.Ordinal) || !AssetDatabase.IsValidFolder(folder) || IsEditorPath(folder + "/")) { errors.Add($"收集目录必须是 Assets 下的非 Editor 目录：{folder}"); continue; }
                if (!Enum.IsDefined(typeof(BundlePacking), rule.Packing)) { errors.Add("未知分包规则。"); continue; }
                if (rule.Packing == BundlePacking.Label && string.IsNullOrWhiteSpace(rule.RequiredLabel)) { errors.Add($"Label 分包必须填写 RequiredLabel：{folder}"); continue; }
                if (rule.Packing == BundlePacking.Together && string.IsNullOrWhiteSpace(rule.GroupName)) { errors.Add($"Together 分包必须填写组名：{folder}"); continue; }
                var extensions = new HashSet<string>((rule.Extensions ?? "").Split(',')
                    .Select(e => e.Trim()).Where(e => e.Length > 0).Select(e => e.StartsWith(".") ? e : "." + e), StringComparer.OrdinalIgnoreCase);
                var collected = 0;
                foreach (var path in AssetDatabase.FindAssets("", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath)
                    .Distinct().OrderBy(p => p, StringComparer.Ordinal)) {
                    if (!IsCollectable(path, rule.FileType)) {
                        continue;
                    }

                    var relative = path.Substring(folder.Length + 1);
                    if (!rule.Recursive && relative.Contains("/")) {
                        continue;
                    }

                    if (extensions.Count > 0 && !extensions.Contains(Path.GetExtension(path))) {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(rule.RequiredLabel) &&
                        !AssetDatabase.GetLabels(AssetDatabase.LoadMainAssetAtPath(path)).Contains(rule.RequiredLabel)) {
                        continue;
                    }

                    var kind = rule.FileType != ResourceFileType.AssetBundle ? rule.FileType.ToString().ToLowerInvariant() :
                        path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? "scene" : "asset";
                    var bundle = rule.Packing switch
                    {
                        BundlePacking.Directory => GroupName("dir", Path.GetDirectoryName(path).Replace('\\', '/'), kind),
                        BundlePacking.Together => GroupName("group", rule.GroupName, kind),
                        BundlePacking.Label => GroupName("label", rule.RequiredLabel, kind),
                        _ => SeparateName(path, rule.FileType)
                    };
                    var prefix = (rule.AddressPrefix ?? "").Trim().Trim('/');
                    if (rule.Packing != BundlePacking.Separately) {
                        bundle = Path.ChangeExtension(bundle, Extension(rule.FileType));
                    }

                    Add(prefix.Length == 0 ? relative : prefix + "/" + relative, path, bundle, rule.Tags, rule.CollectionGroup, rule.FileType, rule.CollectorRole, rule.Extension);
                    collected++;
                }
                if (collected == 0) {
                    warnings.Add($"收集规则没有匹配到资源：{folder}");
                }
            }
            var dependencies = new HashSet<string>(result.Where(a => roles[a.AssetPath] != ResourceCollectorRole.Dependency)
                .SelectMany(a => dependencyQuery.GetDependencies(a.AssetPath, true)), StringComparer.Ordinal);
            result.RemoveAll(a => roles[a.AssetPath] == ResourceCollectorRole.Dependency && !dependencies.Contains(a.AssetPath));
            if (result.Count == 0) {
                errors.Add("配置没有可构建资源。");
            }

            return result.OrderBy(a => a.Address, StringComparer.Ordinal).ToArray();

            void Add(string address, string path, string bundle, string[] tags, string group, ResourceFileType fileType,
                ResourceCollectorRole role, ResourceCollectionExtension extension)
            {
                if (!Enum.IsDefined(typeof(ResourceCollectorRole), role)) { errors.Add("未知收集器类型。"); return; }
                if (role != ResourceCollectorRole.Main && fileType != ResourceFileType.AssetBundle) { errors.Add("静态与依赖收集器只适用于 AssetBundle。"); return; }
                try {
                    if (config.GlobalCollectionRule != null && !config.GlobalCollectionRule.IncludeAsset(path)) {
                        return;
                    }

                    if (extension != null) {
                        if (!extension.IsGroupActive(group) || !extension.IncludeAsset(path)) {
                            return;
                        }

                        address = extension.GetAddress(path, address); bundle = extension.GetBundleName(path, bundle);
                    }
                    if (config.GlobalCollectionRule != null) { address = config.GlobalCollectionRule.GetAddress(path, address); bundle = config.GlobalCollectionRule.GetBundleName(path, bundle); }
                }
                catch (Exception error) { errors.Add("收集规则失败：" + path + " " + error.Message); return; }
                if (config.ValidateAssetPaths) {
                    var invalidPath = false;
                    foreach (var dependency in dependencyQuery.GetDependencies(path, true)) {
                        for (var i = 0; i < dependency.Length; i++) {
                            if (char.IsControl(dependency[i]) || char.GetUnicodeCategory(dependency[i]) == System.Globalization.UnicodeCategory.Format) {
                                errors.Add($"资源路径包含不可见控制字符：{dependency}，位置 {i}，U+{(int)dependency[i]:X4}（收集入口：{path}）");
                                invalidPath = true;
                            }
                        }
                    }

                    if (invalidPath) {
                        return;
                    }
                }
                if (string.IsNullOrWhiteSpace(address) || address != address.Trim() || address.Contains("\\") ||
                    address.Split('/').Any(p => p == "." || p == ".." || p.Length == 0)) { errors.Add($"资源地址格式无效：{address}"); return; }
                if (!Enum.IsDefined(typeof(ResourceFileType), fileType) || !IsCollectable(path, fileType)) { errors.Add($"不支持的资源路径或格式：{path}"); return; }
                if (fileType != ResourceFileType.AssetBundle && string.IsNullOrEmpty(config.PackageName)) { errors.Add("RawFile/Archive 要求命名 Package 与 V4 清单。"); return; }
                var mergedTags = (tags ?? Array.Empty<string>()).Concat(!string.IsNullOrEmpty(group) && groupTags.TryGetValue(group, out var inherited)
                    ? inherited : Array.Empty<string>()).Concat(AssetDatabase.GetLabels(AssetDatabase.LoadMainAssetAtPath(path)))
                    .Distinct(StringComparer.Ordinal).OrderBy(tag => tag, StringComparer.Ordinal).ToArray();
                if (mergedTags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag != tag.Trim())) { errors.Add("下载标签不能为空或包含首尾空白：" + address); return; }
                if (!ResourceManifest.IsSafeBundleName(bundle) || !bundle.EndsWith(Extension(fileType), StringComparison.Ordinal) || bundle.StartsWith("shared_", StringComparison.Ordinal)) { errors.Add($"无效或保留的 Bundle 名称：{bundle}"); return; }
                if (!addresses.Add(address)) { errors.Add($"地址冲突（包括规则重叠）：{address}"); return; }
                if (owners.TryGetValue(path, out var owner) && owner != bundle) { errors.Add($"同一资源被分配到不同 Bundle：{path} → {owner}, {bundle}"); return; }
                owners[path] = bundle;
                if (roles.TryGetValue(path, out ResourceCollectorRole previousRole) && previousRole != role) { errors.Add("同一资源使用了不同收集器类型：" + path); return; }
                roles[path] = role;
                if (path.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0) {
                    warnings.Add($"资源同时位于 Resources 目录，Player 可能存在额外副本：{path}");
                }

                result.Add(new AssetInfo
                {
                    Address = address,
                    AssetPath = path,
                    BundleName = bundle,
                    IsDependencyOnly = role != ResourceCollectorRole.Main,
                    Guid = AssetDatabase.AssetPathToGUID(path),
                    Tags = mergedTags,
                    FileType = fileType,
                    Kind = fileType != ResourceFileType.AssetBundle ? ResourceKind.RawFile :
                        path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? ResourceKind.Scene : ResourceKind.Asset
                });
            }
        }

        internal static bool IsEditorPath(string path)
        {
            return path.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsPackable(string path, bool allowScene = false)
        {
            if (string.IsNullOrEmpty(path) || !(path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)) ||
                AssetDatabase.IsValidFolder(path) || IsEditorPath(path)) {
                return false;
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".unity") {
                return allowScene;
            }

            if (extension == ".cs" || extension == ".dll" || extension == ".asmdef" || extension == ".asmref" || extension == ".meta" || extension == ".rsp") {
                return false;
            }

            Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
            // 默认文件和仅编辑器可用的配置不是运行时资源，防止扫描到自身的打包配置。
            return type != null && type != typeof(DefaultAsset) && type != typeof(MonoScript) &&
                !typeof(BundleBuildConfig).IsAssignableFrom(type) && !IsEditorAssembly(type.Assembly.GetName().Name);
        }

        private static bool IsEditorAssembly(string name)
        {
            return name == "UnityEditor" || name.StartsWith("UnityEditor.", StringComparison.Ordinal) ||
            (s_editorOnlyAssemblies != null && s_editorOnlyAssemblies.Contains(name));
        }

        private static string Extension(ResourceFileType type)
        {
            return type == ResourceFileType.RawFile ? ".raw" : type == ResourceFileType.Archive ? ".zra" : ".bundle";
        }

        private static string SeparateName(string path, ResourceFileType type)
        {
            return "asset_" + AssetDatabase.AssetPathToGUID(path) + Extension(type);
        }

        private static bool IsCollectable(string path, ResourceFileType type)
        {
            return type == ResourceFileType.AssetBundle ? IsPackable(path, true) :
                    !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal) && !IsEditorPath(path) &&
                    !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
        }

        private static string GroupName(string prefix, string key, string kind)
        {
            // 不使用 string.GetHashCode：它可能随进程变化，导致每次构建产生不同包名。
            using var hash = SHA256.Create();
            var suffix = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            return prefix + "_" + kind + "_" + suffix + ".bundle";
        }
    }
}

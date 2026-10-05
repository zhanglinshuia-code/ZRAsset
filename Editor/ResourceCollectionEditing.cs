using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZRAsset.Editor
{
    /// <summary>收集窗口的配置操作；逻辑分组只组织和启停收集器，不修改既有地址或 Bundle 命名。</summary>
    public static class ResourceCollectionEditing
    {
        public static string AddGroup(BundleBuildConfig config)
        {
            Undo.RecordObject(config, "添加资源分组");
            config.Groups ??= new List<BundleBuildConfig.CollectionGroup>();
            var name = "新分组"; var suffix = 1;
            while (config.Groups.Any(g => g != null && g.Name == name)) {
                name = "新分组 " + suffix++;
            }

            config.Groups.Add(new BundleBuildConfig.CollectionGroup { Name = name });
            EditorUtility.SetDirty(config);
            return name;
        }

        public static void RenameGroup(BundleBuildConfig config, string oldName, string newName)
        {
            newName = (newName ?? "").Trim();
            if (string.IsNullOrEmpty(newName) || config.Groups.Any(g => g != null && g.Name == newName && g.Name != oldName)) {
                throw new ArgumentException("分组名称不能为空或与已有分组重复。");
            }

            BundleBuildConfig.CollectionGroup group = config.Groups.First(g => g != null && g.Name == oldName);
            if (oldName == newName) {
                return;
            }

            Undo.RecordObject(config, "重命名资源分组");
            group.Name = newName;
            foreach (BundleBuildConfig.CollectionRule rule in config.Rules ?? new List<BundleBuildConfig.CollectionRule>()) {
                if (rule != null && rule.CollectionGroup == oldName) {
                    rule.CollectionGroup = newName;
                }
            }

            foreach (BundleBuildConfig.Entry entry in config.Entries ?? new List<BundleBuildConfig.Entry>()) {
                if (entry != null && entry.CollectionGroup == oldName) {
                    entry.CollectionGroup = newName;
                }
            }

            EditorUtility.SetDirty(config);
        }

        public static void RemoveGroup(BundleBuildConfig config, string name)
        {
            Undo.RecordObject(config, "移除资源分组并保留收集器");
            config.Groups.RemoveAll(g => g != null && g.Name == name);
            foreach (BundleBuildConfig.CollectionRule rule in config.Rules ?? new List<BundleBuildConfig.CollectionRule>()) {
                if (rule != null && rule.CollectionGroup == name) {
                    rule.CollectionGroup = "";
                }
            }

            foreach (BundleBuildConfig.Entry entry in config.Entries ?? new List<BundleBuildConfig.Entry>()) {
                if (entry != null && entry.CollectionGroup == name) {
                    entry.CollectionGroup = "";
                }
            }

            EditorUtility.SetDirty(config);
        }

        /// <summary>目录成为收集规则，主资源成为显式条目；批量拖入一次撤销，重复路径跳过。</summary>
        public static int AddObjects(BundleBuildConfig config, string group, IEnumerable<Object> objects)
        {
            Object[] inputs = objects.Where(o => o != null).Distinct().ToArray();
            Undo.RecordObject(config, "拖入资源收集器");
            config.Rules ??= new List<BundleBuildConfig.CollectionRule>();
            config.Entries ??= new List<BundleBuildConfig.Entry>();
            var added = 0;
            foreach (Object obj in inputs) {
                var path = AssetDatabase.GetAssetPath(obj);
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) || AssetCollector.IsEditorPath(path + "/")) {
                    continue;
                }

                if (AssetDatabase.IsValidFolder(path)) {
                    if (config.Rules.Any(r => r != null && AssetDatabase.GetAssetPath(r.Folder) == path)) {
                        continue;
                    }

                    config.Rules.Add(new BundleBuildConfig.CollectionRule
                    {
                        Folder = obj,
                        CollectionGroup = group,
                        AddressPrefix = Path.GetFileName(path).ToLowerInvariant()
                    });
                }
                else {
                    if (AssetDatabase.IsSubAsset(obj) || !AssetCollector.IsPackable(path, true) ||
                        config.Entries.Any(e => e != null && AssetDatabase.GetAssetPath(e.Asset) == path)) {
                        continue;
                    }

                    config.Entries.Add(new BundleBuildConfig.Entry
                    {
                        Asset = obj,
                        CollectionGroup = group,
                        Address = path.Substring("Assets/".Length)
                    });
                }
                added++;
            }
            if (added != 0) {
                EditorUtility.SetDirty(config);
            }

            return added;
        }

        public static void Duplicate(BundleBuildConfig config, bool isRule, int index)
        {
            Undo.RecordObject(config, "复制资源收集器");
            if (isRule) {
                config.Rules.Insert(index + 1, JsonUtility.FromJson<BundleBuildConfig.CollectionRule>(JsonUtility.ToJson(config.Rules[index])));
            }
            else {
                config.Entries.Insert(index + 1, JsonUtility.FromJson<BundleBuildConfig.Entry>(JsonUtility.ToJson(config.Entries[index])));
            }

            EditorUtility.SetDirty(config);
        }

        public static void MoveToGroup(BundleBuildConfig config, bool isRule, int index, string group)
        {
            Undo.RecordObject(config, "移动资源收集器到分组");
            if (isRule) {
                config.Rules[index].CollectionGroup = group;
            }
            else {
                config.Entries[index].CollectionGroup = group;
            }

            EditorUtility.SetDirty(config);
        }
    }
}

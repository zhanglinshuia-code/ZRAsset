using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

namespace ZRAsset.Editor
{
    public static class ResourceCollectionXml
    {
        public static string Export(BundleBuildConfig config)
        {
            if (config == null) {
                throw new ArgumentNullException(nameof(config));
            }
            var references = new XElement("references");
            void Add(string kind, int index, UnityEngine.Object asset)
            {
                var guid = asset == null ? "" : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
                if (asset != null && (string.IsNullOrEmpty(guid) || !AssetDatabase.IsMainAsset(asset))) {
                    throw new InvalidDataException("XML 引用必须是已保存的主资源：" + asset.name);
                }
                references.Add(new XElement("ref", new XAttribute("kind", kind), new XAttribute("index", index), new XAttribute("guid", guid)));
            }
            Add("global", 0, config.GlobalCollectionRule);
            Add("quality", 0, config.QualityPolicy);
            Add("shaders", 0, config.ShaderVariants);
            for (var i = 0; i < config.Entries.Count; i++) {
                Add("asset", i, config.Entries[i].Asset);
            }
            for (var i = 0; i < config.Rules.Count; i++) { Add("folder", i, config.Rules[i].Folder); Add("extension", i, config.Rules[i].Extension); }
            return new XDocument(new XElement("ZRAssetCollection", new XAttribute("version", 1),
                new XElement("settings", JsonUtility.ToJson(config)), references)).ToString();
        }
        public static void Import(BundleBuildConfig config, string xml)
        {
            if (config == null) {
                throw new ArgumentNullException(nameof(config));
            }
            if (xml == null || xml.Length > 8 * 1024 * 1024) {
                throw new InvalidDataException("配置为空或超过 8 MiB。");
            }
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            XElement root = XDocument.Load(reader).Root;
            if (root?.Name != "ZRAssetCollection" || (int?)root.Attribute("version") != 1) {
                throw new InvalidDataException("配置格式无效。");
            }
            BundleBuildConfig temporary = ScriptableObject.CreateInstance<BundleBuildConfig>();
            try {
                JsonUtility.FromJsonOverwrite(root.Element("settings")?.Value ?? throw new InvalidDataException("缺少配置。"), temporary);
                // 进程内 instanceID 不属于可移植配置，始终清除并从 GUID 重建。
                temporary.GlobalCollectionRule = null;
                temporary.QualityPolicy = null;
                temporary.ShaderVariants = null;
                foreach (BundleBuildConfig.Entry entry in temporary.Entries) {
                    entry.Asset = null;
                }
                foreach (BundleBuildConfig.CollectionRule rule in temporary.Rules) { rule.Folder = null; rule.Extension = null; }
                foreach (XElement item in root.Element("references")?.Elements("ref") ?? throw new InvalidDataException("缺少 GUID 引用。")) {
                    var guid = (string)item.Attribute("guid"); var index = (int)item.Attribute("index");
                    UnityEngine.Object asset = string.IsNullOrEmpty(guid) ? null : AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                    if (!string.IsNullOrEmpty(guid) && asset == null) {
                        throw new InvalidDataException("项目中找不到资源 GUID：" + guid);
                    }
                    switch ((string)item.Attribute("kind")) {
                        case "global": temporary.GlobalCollectionRule = (ResourceCollectionExtension)asset; break;
                        case "quality": temporary.QualityPolicy = (ResourceBuildQualityPolicy)asset; break;
                        case "shaders": temporary.ShaderVariants = (ResourceShaderVariantProfile)asset; break;
                        case "asset": temporary.Entries[index].Asset = asset; break;
                        case "folder": temporary.Rules[index].Folder = asset; break;
                        case "extension": temporary.Rules[index].Extension = (ResourceCollectionExtension)asset; break;
                        default: throw new InvalidDataException("未知引用类型。");
                    }
                }
                ResourceBuildAnalyzer.Analyze(temporary, EditorUserBuildSettings.activeBuildTarget).ThrowIfInvalid();
                Undo.RecordObject(config, "导入资源收集配置");
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(temporary), config); EditorUtility.SetDirty(config);
            }
            finally { UnityEngine.Object.DestroyImmediate(temporary); }
        }
        [MenuItem("ZRAsset/资源收集/导出所选配置 XML")]
        private static void ExportSelected()
        {
            if (Selection.activeObject is not BundleBuildConfig config) {
                throw new InvalidOperationException("请先选择 BundleBuildConfig。");
            }
            var path = EditorUtility.SaveFilePanel("导出配置", "", config.name, "xml");
            if (path.Length > 0) {
                File.WriteAllText(path, Export(config));
            }
        }
        [MenuItem("ZRAsset/资源收集/导入 XML 到所选配置")]
        private static void ImportSelected()
        {
            if (Selection.activeObject is not BundleBuildConfig config) {
                throw new InvalidOperationException("请先选择 BundleBuildConfig。");
            }
            var path = EditorUtility.OpenFilePanel("导入配置", "", "xml");
            if (path.Length > 0) {
                Import(config, File.ReadAllText(path));
            }
        }
    }
}

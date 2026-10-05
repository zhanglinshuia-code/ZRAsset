using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZRAsset.Editor
{
    public static class ResourceShaderVariants
    {
        /// <summary>收集包内材质当前关键字组合。业务动态关键字需通过 additional 显式补充。</summary>
        public static ShaderVariantCollection Collect(BundleBuildConfig config,
            ShaderVariantCollection.ShaderVariant[] additional = null)
        {
            ResourceBuildPlan plan = ResourceBuildAnalyzer.Analyze(config, EditorUserBuildSettings.activeBuildTarget); plan.ThrowIfInvalid();
            var result = new ShaderVariantCollection();
            try {
                foreach (Material material in plan.Report.Bundles.SelectMany(b => b.IncludedAssets).Distinct()
                    .SelectMany(AssetDatabase.LoadAllAssetsAtPath).OfType<Material>()
                    .Concat(config.ShaderVariants?.Materials ?? Array.Empty<Material>()).Where(m => m != null && m.shader != null).Distinct()) {
                    foreach (PassType pass in Enum.GetValues(typeof(PassType))) {
                        try { result.Add(new ShaderVariantCollection.ShaderVariant(material.shader, pass, material.shaderKeywords)); }
                        catch (ArgumentException) { /* Shader 不包含该 Pass，Unity 会拒绝无效变种。 */ }
                    }
                }
                foreach (ShaderVariantCollection.ShaderVariant variant in additional ?? Array.Empty<ShaderVariantCollection.ShaderVariant>()) {
                    result.Add(variant);
                }
                if (config.ShaderVariants != null) {
                    foreach (ResourceShaderVariantProfile.KeywordCase item in config.ShaderVariants.RuntimeCases ?? Array.Empty<ResourceShaderVariantProfile.KeywordCase>()) {
                        if (item?.Shader != null) { result.Add(new ShaderVariantCollection.ShaderVariant(item.Shader, item.Pass, item.Keywords)); }
                    }
                    foreach (ShaderVariantCollection collection in config.ShaderVariants.Collections ?? Array.Empty<ShaderVariantCollection>()) {
                        foreach (ShaderVariantCollection.ShaderVariant variant in ResourceShaderVariantTools.Read(collection)) { result.Add(variant); }
                    }
                }
                return result;
            }
            catch { UnityEngine.Object.DestroyImmediate(result); throw; }
        }
        [MenuItem("ZRAsset/着色器/收集所选配置变体")]
        private static void CollectSelected()
        {
            if (Selection.activeObject is not BundleBuildConfig config) {
                throw new InvalidOperationException("请先选择 BundleBuildConfig。");
            }
            var path = EditorUtility.SaveFilePanelInProject("保存 Shader 变种", "ZRAssetVariants", "shadervariants", "生成后将此资源加入收集配置。");
            if (path.Length == 0) {
                return;
            }
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) {
                throw new InvalidOperationException("请选择新的输出路径。");
            }
            ShaderVariantCollection collection = Collect(config); AssetDatabase.CreateAsset(collection, path); AssetDatabase.SaveAssets();
            Selection.activeObject = collection;
        }
    }
}

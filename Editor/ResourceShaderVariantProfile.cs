using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZRAsset.Editor
{
    [CreateAssetMenu(menuName = "ZRAsset/着色器变体配置", fileName = "ShaderVariants")]
    public sealed class ResourceShaderVariantProfile: ScriptableObject
    {
        [Serializable]
        public sealed class KeywordCase
        {
            public Shader Shader;
            public PassType Pass = PassType.Normal;
            public string[] Keywords = Array.Empty<string>();
        }

        [Serializable]
        public sealed class TerrainCase
        {
            public TerrainData Data;
            public Material Material;
            public bool DrawInstanced = true;
        }

        public Material[] Materials = Array.Empty<Material>();
        public KeywordCase[] RuntimeCases = Array.Empty<KeywordCase>();
        public TerrainCase[] Terrains = Array.Empty<TerrainCase>();
        public ShaderVariantCollection[] Collections = Array.Empty<ShaderVariantCollection>();
        [Tooltip("仅剥离集合中已有 Shader 的未列出组合。必须先覆盖实际运行时场景。")]
        public bool StripUnlistedVariants;
        [Tooltip("仅影响本次 ZRAsset 构建；包含任一关键字的变种将被排除。")]
        public string[] ExcludedKeywords = Array.Empty<string>();
    }
}

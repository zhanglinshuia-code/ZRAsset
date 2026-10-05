using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZRAsset.Editor
{
    /// <summary>目录规则的分包方式；场景始终附加独立分组，禁止与普通资源混包。</summary>
    public enum BundlePacking { Separately, Directory, Together, Label }
    public enum ResourceBundleCompression { Lz4, Lzma, Uncompressed }
    public enum ResourceBuildBackend { BuiltIn, ScriptableBuildPipeline }

    /// <summary>显式条目适合稳定业务地址，目录规则适合批量收集；两者会进行统一冲突校验。</summary>
    [CreateAssetMenu(menuName = "ZRAsset/资源构建配置", fileName = "ZRAssetBuildConfig")]
    public sealed class BundleBuildConfig: ScriptableObject
    {
        [Header("Package（留空兼容旧版单包配置）")]
        public string PackageName;
        public string PackageVersion = "1.0";
        public ResourceBuildBackend BuildBackend;
        [Tooltip("命名 Package 使用 V6 资源粒度依赖；旧版 Player 不支持该格式。")]
        public bool UseAssetDependencies;
        [Tooltip("SBP 生成 link.xml，复制首包时登记到 Player 的 UnityLinker 构建流程。")]
        public bool GenerateLinkXml = true;
        public bool StripUnityVersion;
        public bool DisableWriteTypeTree;
        [Tooltip("留空只使用本机缓存；设置后 SBP 会连接该缓存服务器。")]
        public string CacheServerHost;
        public int CacheServerPort = 8126;
        [Tooltip("SBP 禁用本次构建缓存；内置管线使用 ForceRebuildAssetBundle。不会删除全局缓存。")]
        public bool ForceRebuild;
        public ResourceBundleCompression Compression = ResourceBundleCompression.Lz4;
        [Tooltip("留空不加密；只记录密钥标识，不保存密钥。实际密钥由构建服务或环境变量提供。")]
        public string EncryptionKeyId;
        public bool ReuseEncryptedArtifacts = true;
        public bool ExportBinaryManifest;
        public bool ExportEncodedManifest;
        [Tooltip("编码清单的密钥标识；留空只进行 gzip 压缩，不加密。密钥从构建服务读取。")]
        public string ManifestEncryptionKeyId;
        public ResourceBuildQualityPolicy QualityPolicy;
        public bool ValidateAssetPaths;
        public bool CollectShadersSeparately;
        public ResourceShaderVariantProfile ShaderVariants;
        public ResourceCollectionExtension GlobalCollectionRule;
        public ResourceBuildExtension[] BuildExtensions = Array.Empty<ResourceBuildExtension>();
        [Serializable]
        public sealed class Entry
        {
            public ResourceCollectorRole CollectorRole;
            [Tooltip("逻辑分组；留空为默认分组，不改变 Bundle 命名。")]
            public string CollectionGroup;
            public bool Disabled;
            [Tooltip("业务地址，区分大小写，例如 ui/login。")]
            public string Address;
            [Tooltip("AssetBundle 使用主资源或场景；RawFile/Archive 按源文件原始字节构建。")]
            public UnityEngine.Object Asset;
            [Tooltip("小写文件名，按格式使用 .bundle、.raw 或 .zra；留空时按资源 GUID 单独分包。")]
            public string BundleName;
            public ResourceFileType FileType;
            [Tooltip("下载标签，与逻辑分组、资源自身的 Unity Labels 合并，不改变分包。")]
            public string[] Tags = Array.Empty<string>();
        }

        [Serializable]
        public sealed class CollectionRule
        {
            public ResourceCollectorRole CollectorRole;
            public ResourceCollectionExtension Extension;
            [Tooltip("逻辑分组；留空为默认分组，不改变地址或合包名称。")]
            public string CollectionGroup;
            public bool Disabled;
            [Tooltip("拖入 Assets 下的目录，不会修改资源本身的 AssetBundle 标签。")]
            public UnityEngine.Object Folder;
            public bool Recursive = true;
            [Tooltip("地址 = 前缀 + 相对文件路径（保留扩展名），例如 ui/Login.prefab。")]
            public string AddressPrefix;
            [Tooltip("逗号分隔的扩展名；留空收集所有可用源文件。排除 Editor 目录和 .meta；AssetBundle 还排除脚本。")]
            public string Extensions = ".prefab,.asset,.mat,.png,.jpg,.txt,.json,.bytes,.unity,.spriteatlas";
            [Tooltip("可选的 Unity Asset Label 过滤。Label 分包时必须指定。")]
            public string RequiredLabel;
            public BundlePacking Packing = BundlePacking.Separately;
            public ResourceFileType FileType;
            [Tooltip("Together 使用此组名；不同规则相同组名会合包。")]
            public string GroupName = "game";
            public string[] Tags = Array.Empty<string>();
        }

        [Serializable]
        public sealed class CollectionGroup
        {
            public string Name;
            public string Description;
            public bool Disabled;
            public string[] Tags = Array.Empty<string>();
        }

        [Header("逻辑分组")]
        public List<CollectionGroup> Groups = new();
        [Header("显式资源")]
        public List<Entry> Entries = new();
        [Header("批量收集规则")]
        public List<CollectionRule> Rules = new();
        [Tooltip("把多包共用的隐式资源提取为 shared_<guid>.bundle。关闭后仍会报告重复风险。")]
        public bool ExtractSharedDependencies = true;
    }
}

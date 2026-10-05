using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZRAsset.Editor
{
    // 只转换界面文本，不修改字段名、枚举值或序列化数据。
    internal static class ResourceEditorChinese
    {
        private static readonly Dictionary<string, string> s_labels = new()
        {
            { "PackageName", "资源包名称" },
            { "PackageVersion", "资源包版本" },
            { "BuildBackend", "构建管线" },
            { "UseAssetDependencies", "启用资源粒度依赖" },
            { "GenerateLinkXml", "生成防裁剪配置" },
            { "StripUnityVersion", "剥离引擎版本信息" },
            { "DisableWriteTypeTree", "禁用类型树" },
            { "CacheServerHost", "缓存服务器地址" },
            { "CacheServerPort", "缓存服务器端口" },
            { "ForceRebuild", "强制重新构建" },
            { "Compression", "压缩方式" },
            { "EncryptionKeyId", "资源加密密钥标识" },
            { "ReuseEncryptedArtifacts", "复用加密产物" },
            { "ExportBinaryManifest", "导出二进制清单" },
            { "ExportEncodedManifest", "导出压缩加密清单" },
            { "ManifestEncryptionKeyId", "清单加密密钥标识" },
            { "QualityPolicy", "构建质量策略" },
            { "ValidateAssetPaths", "校验资源路径" },
            { "CollectShadersSeparately", "着色器单独分包" },
            { "ShaderVariants", "着色器变体配置" },
            { "GlobalCollectionRule", "全局收集扩展" },
            { "CollectorRole", "收集用途" },
            { "CollectionGroup", "逻辑分组" },
            { "Disabled", "禁用" },
            { "Address", "业务地址" },
            { "Asset", "资源" },
            { "BundleName", "AB 包名称" },
            { "FileType", "文件格式" },
            { "Tags", "下载标签" },
            { "Extension", "收集扩展" },
            { "Folder", "收集目录" },
            { "Recursive", "包含子目录" },
            { "AddressPrefix", "地址前缀" },
            { "Extensions", "扩展名过滤" },
            { "RequiredLabel", "资源标签过滤" },
            { "Packing", "分包方式" },
            { "GroupName", "合包名称" },
            { "Name", "名称" },
            { "Description", "说明" },
            { "Groups", "逻辑分组" },
            { "Entries", "显式资源" },
            { "Rules", "收集规则" },
            { "ExtractSharedDependencies", "自动提取共享依赖" },
            { "ResourceConfig", "资源构建配置" },
            { "PlayerBuildId", "宿主兼容标识" },
            { "ContentVersion", "内容版本" },
            { "EntryAssembly", "入口程序集" },
            { "EntryType", "入口类型" },
            { "EntryMethod", "入口方法" },
            { "ManifestAddress", "热更清单地址" },
            { "AotMetadata", "AOT 补充元数据" },
            { "Assemblies", "热更程序集" },
            { "FilePath", "文件路径" },
            { "Dependencies", "依赖程序集" },
            { "MaximumTotalBytes", "总大小上限（字节）" },
            { "MaximumBundleBytes", "单包大小上限（字节）" },
            { "MaximumPatchBytes", "补丁大小上限（字节）" },
            { "MaximumStartupBytes", "启动下载上限（字节）" },
            { "MaximumBundleCount", "AB 包数量上限" },
            { "MaximumDuplicateAssets", "重复资源数量上限" },
            { "RejectRemovedAddresses", "禁止移除已发布地址" },
            { "StartupTags", "启动资源标签" },
            { "BaselineManifest", "基准版本清单" },
            { "Shader", "着色器" },
            { "Pass", "渲染通道" },
            { "Keywords", "关键字" },
            { "Data", "地形数据" },
            { "Material", "材质" },
            { "DrawInstanced", "启用实例化绘制" },
            { "Materials", "材质列表" },
            { "RuntimeCases", "运行时变体" },
            { "Terrains", "地形列表" },
            { "Collections", "变体集合" },
            { "StripUnlistedVariants", "裁剪未收集变体" },
            { "ExcludedKeywords", "排除关键字" },
            { "Mode", "启动模式" },
            { "AllowOfflineFallback", "允许失败后使用离线版本" },
            { "SignedReleaseUrl", "版本检查地址" },
            { "RemoteBundleBaseUrl", "资源 CDN 根地址" },
            { "TrustedKeys", "可信发布公钥" },
            { "MinimumSequence", "最低发布序号" },
            { "AllowHttpLoopback", "允许本机 HTTP 测试" },
            { "CacheDirectory", "缓存目录名称" },
            { "UseBuiltInFiles", "使用首包资源" },
            { "BuiltInVersion", "首包版本" },
            { "BuiltInManifestSha256", "首包清单 SHA256" },
            { "BuiltInDirectory", "首包目录名称" },
            { "MaxConcurrentDownloads", "最大并发下载数" },
            { "RequestTimeoutSeconds", "请求超时（秒）" },
            { "MaxConcurrentBundleLoads", "最大并发 AB 加载数" },
            { "MaxConcurrentAssetLoads", "最大并发资源加载数" },
            { "UnloadDelaySeconds", "卸载延迟（秒）" },
            { "KeyId", "公钥标识" },
            { "ModulusBase64", "公钥模数（Base64）" },
            { "ExponentBase64", "公钥指数（Base64）" },
            { "size", "数量" },
            { "Separately", "逐资源独立分包" },
            { "Directory", "按目录分包" },
            { "Together", "合并分包" },
            { "Label", "按标签分包" },
            { "Lz4", "LZ4 压缩" },
            { "Lzma", "LZMA 压缩" },
            { "Uncompressed", "不压缩" },
            { "BuiltIn", "内置构建管线" },
            { "ScriptableBuildPipeline", "可编程构建管线（SBP）" },
            { "AssetBundle", "AB 资源包" },
            { "RawFile", "原始文件" },
            { "Archive", "归档文件" },
            { "SignedUpdate", "签名在线更新" },
            { "OfflineActive", "离线活动版本" },
            { "Main", "主资源" },
            { "Static", "静态资源" },
            { "Dependency", "依赖资源" },
            { "Normal", "普通通道" },
            { "Vertex", "顶点通道" },
            { "VertexLM", "顶点光照贴图通道" },
            { "VertexLMRGBM", "顶点光照 RGBM 通道" },
            { "ForwardBase", "前向基础通道" },
            { "ForwardAdd", "前向附加通道" },
            { "LightPrePassBase", "光照预处理基础通道" },
            { "LightPrePassFinal", "光照预处理最终通道" },
            { "ShadowCaster", "阴影投射通道" },
            { "Deferred", "延迟渲染通道" },
            { "Meta", "烘焙通道" },
            { "MotionVectors", "运动矢量通道" },
            { "ScriptableRenderPipeline", "可编程渲染管线" },
            { "ScriptableRenderPipelineDefaultUnlit", "可编程管线无光照通道" },
            { "Collect", "收集资源" },
            { "Pack", "资源分包" },
            { "Analyze", "分析依赖" },
            { "Validate", "校验配置" },
            { "BuildBundles", "构建 AB 包" },
            { "EncryptFiles", "加密文件" },
            { "GenerateManifest", "生成清单" },
            { "ExportReport", "导出报告" },
            { "Loading", "加载中" },
            { "Loaded", "已加载" },
            { "Failed", "失败" },
            { "Succeeded", "成功" },
            { "Pending", "等待中" },
            { "Queued", "排队中" },
            { "Downloading", "下载中" },
            { "Completed", "已完成" },
            { "Canceled", "已取消" },
            { "Disposed", "已释放" },
            { "Unloaded", "已卸载" },
            { "AllAssets", "全部资源" },
            { "SubAssets", "子资源" },
            { "Created", "已创建" },
            { "Running", "运行中" },
            { "Destroyed", "已销毁" },
        };
        internal static string Text(string name)
        {
            return s_labels.TryGetValue(name ?? "", out var text) ? text : name;
        }

        internal static void DrawInspector(SerializedObject serialized)
        {
            serialized.Update();
            SerializedProperty property = serialized.GetIterator();
            var enter = true;
            while (property.NextVisible(enter)) {
                enter = false;
                if (property.name == "m_Script") {
                    continue;
                }

                PropertyField(property, true);
            }
            serialized.ApplyModifiedProperties();
        }

        internal static void PropertyField(SerializedProperty property, bool includeChildren = false)
        {
            PropertyField(property, null, includeChildren);
        }

        internal static void PropertyField(SerializedProperty property, GUIContent label, bool includeChildren = false)
        {
            label ??= new GUIContent(property.name.StartsWith("data[") ? "条目 " + property.name.Substring(5).TrimEnd(']') : Text(property.name), property.tooltip);
            if (property.propertyType == SerializedPropertyType.Enum) {
                EditorGUI.BeginChangeCheck();
                var selected = EditorGUILayout.Popup(label, property.enumValueIndex,
                    property.enumNames.Select(value => new GUIContent(Text(value))).ToArray());
                if (EditorGUI.EndChangeCheck()) {
                    property.enumValueIndex = selected;
                }

                return;
            }
            if (includeChildren && property.propertyType == SerializedPropertyType.Generic) {
                property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, label, true);
                if (!property.isExpanded) {
                    return;
                }

                using (new EditorGUI.IndentLevelScope()) {
                    SerializedProperty child = property.Copy();
                    SerializedProperty end = child.GetEndProperty();
                    var enter = true;
                    while (child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end)) {
                        enter = false;
                        PropertyField(child, true);
                    }
                }
                return;
            }
            EditorGUILayout.PropertyField(property, label, includeChildren);
        }
    }

    [CustomEditor(typeof(HotUpdateBuildConfig))]
    internal sealed class HotUpdateBuildConfigChineseEditor: UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            ResourceEditorChinese.DrawInspector(serializedObject);
        }
    }
    [CustomEditor(typeof(ResourceBuildQualityPolicy))]
    internal sealed class ResourceBuildQualityPolicyChineseEditor: UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            ResourceEditorChinese.DrawInspector(serializedObject);
        }
    }
    [CustomEditor(typeof(ResourceShaderVariantProfile))]
    internal sealed class ResourceShaderVariantProfileChineseEditor: UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            ResourceEditorChinese.DrawInspector(serializedObject);
        }
    }
}

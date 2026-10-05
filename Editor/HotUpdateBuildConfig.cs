using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZRAsset.Editor
{
    /// <summary>导入已按目标平台编译的热更 DLL；本工具不替代 HybridCLR 编译与裁剪流程。</summary>
    [CreateAssetMenu(menuName = "ZRAsset/热更构建配置", fileName = "ZRAssetHotUpdateBuildConfig")]
    public sealed class HotUpdateBuildConfig: ScriptableObject
    {
        [Serializable]
        public class BinaryInput
        {
            [Tooltip("已编译 DLL 的文件路径，可以相对项目根目录；请勿把原始 DLL 放入 Assets 自动加载。")]
            public string FilePath;
            [Tooltip("程序集简单名称，不含 .dll；留空则从 DLL 元数据读取。")]
            public string Name;
            [Tooltip("业务资源地址；留空则自动生成 hotupdate/aot 或 hotupdate/dll 地址。")]
            public string Address;
        }

        [Serializable]
        public sealed class AssemblyInput: BinaryInput
        {
            [Tooltip("本次发布中实际引用的其他热更程序集简单名；必须与 DLL 元数据一致。")]
            public string[] Dependencies = Array.Empty<string>();
        }

        [Tooltip("可选：将普通资源与代码资源发布到同一份资源清单，不会修改原配置。")]
        public BundleBuildConfig ResourceConfig;
        [Tooltip("与宿主 Player 一致的兼容标识。更换 AOT 程序或裁剪结果后必须更新。")]
        public string PlayerBuildId;
        public string ContentVersion = "1";
        public string EntryAssembly;
        public string EntryType;
        public string EntryMethod = "Run";
        public string ManifestAddress = "hotupdate/manifest";
        [Tooltip("使用与目标 Player 对应的 AOT 裁剪后 DLL；顺序按配置保留。")]
        public List<BinaryInput> AotMetadata = new();
        [Tooltip("依赖顺序由清单拓扑排序计算；这里不要求手工按顺序排列。")]
        public List<AssemblyInput> Assemblies = new();
    }
}

using System;
using UnityEngine;

namespace ZRAsset.Editor
{
    [CreateAssetMenu(menuName = "ZRAsset/构建质量策略", fileName = "ZRAssetQualityPolicy")]
    public sealed class ResourceBuildQualityPolicy: ScriptableObject
    {
        [Tooltip("0 表示不限制；单位为实际发布容器字节，包含加密开销。")]
        public long MaximumTotalBytes;
        public long MaximumBundleBytes;
        public long MaximumPatchBytes;
        public long MaximumStartupBytes;
        public int MaximumBundleCount;
        [Tooltip("-1 不限制；0 不允许残留重复资源。")]
        public int MaximumDuplicateAssets = -1;
        public bool RejectRemovedAddresses;
        public string[] StartupTags = Array.Empty<string>();
        [Tooltip("上一发布版本的 manifest.json；限制补丁大小或禁止移除地址时必须提供。")]
        public TextAsset BaselineManifest;
    }
}

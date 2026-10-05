using UnityEngine;

namespace ZRAsset.Editor
{
    public enum ResourceCollectorRole { Main, Static, Dependency }
    /// <summary>项目规则资产：默认行为保持内置规则；覆盖需要定制的方法即可。</summary>
    public abstract class ResourceCollectionExtension: ScriptableObject
    {
        public virtual bool IsGroupActive(string group)
        {
            return true;
        }

        public virtual bool IncludeAsset(string assetPath)
        {
            return true;
        }

        public virtual string GetAddress(string assetPath, string defaultAddress)
        {
            return defaultAddress;
        }

        public virtual string GetBundleName(string assetPath, string defaultBundleName)
        {
            return defaultBundleName;
        }
    }
}

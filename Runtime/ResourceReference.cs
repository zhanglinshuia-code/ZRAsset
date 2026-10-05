using System;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>序列化 GUID 引用，运行时通过 Package 清单定位；不把 Unity 对象直接带进 Player。</summary>
    [Serializable]
    public class ResourceReference
    {
        [UnityEngine.Serialization.FormerlySerializedAs("guid")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private string m_guid;
        public string Guid
        {
            get
            {
                return m_guid;
            }
        }

        public bool IsValid
        {
            get
            {
                return !string.IsNullOrEmpty(m_guid);
            }
        }

        public ResourceReference() { }
        public ResourceReference(string guid) { ResourceSelection.ValidateGuid(guid); m_guid = guid.ToLowerInvariant(); }
        public string Resolve(ResourcePackage package)
        {
            return package.GetAssetInfoByGuid(m_guid).Address;
        }

        public AssetHandle<T> LoadAsync<T>(ResourcePackage package) where T : UnityEngine.Object
        {
            return package.Resources.LoadAssetAsync<T>(Resolve(package));
        }

        public AssetHandle<T> LoadSync<T>(ResourcePackage package) where T : UnityEngine.Object
        {
            return package.Resources.LoadAssetSync<T>(Resolve(package));
        }
    }
}

using System;
using UnityEngine;

namespace ZRAsset.Samples
{
    /// <summary>示例独立拥有管理器；正式项目应由启动模块创建一个管理器供各业务模块共享。</summary>
    public sealed class ResourceExample: MonoBehaviour
    {
        [UnityEngine.Serialization.FormerlySerializedAs("address")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private string m_address = "demo/greeting";
        private ResourceManager m_resources;
        private AssetHandle<TextAsset> m_handle;
        private bool m_destroyed;

        private async void Start()
        {
            try {
                ResourceManager created = await ResourceManager.CreateAsync();
                if (m_destroyed) { await created.DisposeAsync(); return; }
                m_resources = created;
                m_handle = m_resources.LoadAssetAsync<TextAsset>(m_address);
                TextAsset asset = await m_handle.Operation;
                if (!m_destroyed) {
                    Debug.Log($"ZRAsset loaded: {asset.text}\n{m_resources.GetStats()}", this);
                }
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private void Update()
        {
            m_resources?.UnloadUnused();
        }

        private async void OnDestroy()
        {
            m_destroyed = true;
            m_handle?.Release();
            if (m_resources == null) {
                return;
            }

            try { await m_resources.DisposeAsync(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}

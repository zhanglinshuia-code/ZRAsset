using System;
using System.Threading;
using UnityEngine;

namespace ZRAsset.Samples
{
    /// <summary>挂到启动场景的一个根对象上；此组件拥有独立管理器，仅用于生命周期演示。</summary>
    public sealed class LifecycleExample: MonoBehaviour
    {
        [UnityEngine.Serialization.FormerlySerializedAs("prefabAddress")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private string m_prefabAddress = "demo/cube";
        [UnityEngine.Serialization.FormerlySerializedAs("sceneAddress")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private string m_sceneAddress = "demo/scene";
        private readonly CancellationTokenSource m_lifetime = new();
        private ResourceManager m_resources;
        private InstanceHandle m_instance;
        private SceneHandle m_scene;
        private bool m_destroyed;
        private bool m_busy;
        private string m_status = "Initializing...";

        private async void Start()
        {
            DontDestroyOnLoad(gameObject);
            try {
                ResourceManager created = await ResourceManager.CreateAsync(retryPolicy: new ResourceRetryPolicy(2));
                if (m_destroyed) { await created.DisposeAsync(); return; }
                m_resources = created;
                m_status = "Ready. Create/destroy instances and load/unload the additive demo scene.";
            }
            catch (Exception exception) { m_status = exception.Message; Debug.LogException(exception); }
        }

        private void Update()
        {
            m_resources?.UnloadUnused();
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(15, 15, 620, 220), GUI.skin.box);
            GUILayout.Label("ZRAsset 底层管理器生命周期示例");
            GUILayout.Label(m_status);
            GUILayout.Label(m_resources?.GetStats().ToString() ?? "No manager");
            GUI.enabled = m_resources != null && !m_busy;
            if (GUILayout.Button(m_instance == null || m_instance.IsReleased ? "Create prefab instance" : "Destroy instance")) {
                Run(async () =>
                {
                    if (m_instance != null && !m_instance.IsReleased) {
                        await m_instance.DestroyAsync();
                    }
                    else { m_instance = m_resources.InstantiateAsync(m_prefabAddress, cancellationToken: m_lifetime.Token); await m_instance.Operation; }
                });
            }

            if (GUILayout.Button(m_scene == null || m_scene.IsReleased ? "Load additive scene" : "Unload scene")) {
                Run(async () =>
                {
                    if (m_scene != null && !m_scene.IsReleased) {
                        await m_scene.UnloadAsync();
                    }
                    else { m_scene = m_resources.LoadSceneAsync(m_sceneAddress, cancellationToken: m_lifetime.Token); await m_scene.Operation; }
                });
            }

            if (GUILayout.Button("Unload unused bundles now")) {
                m_resources.UnloadUnused(true);
            }

            GUI.enabled = true;
            GUILayout.EndArea();
        }

        private async void Run(Func<ResourceOperationBase> operation)
        {
            m_busy = true;
            try { await operation(); m_status = "Completed"; }
            catch (OperationCanceledException) { m_status = "Canceled"; }
            catch (Exception exception) { m_status = exception.Message; Debug.LogException(exception); }
            finally { m_busy = false; }
        }

        private async void OnDestroy()
        {
            m_destroyed = true;
            m_lifetime.Cancel();
            try {
                if (m_instance != null) {
                    await m_instance.DestroyAsync();
                }

                if (m_scene != null) {
                    await m_scene.UnloadAsync();
                }

                if (m_resources != null) {
                    await m_resources.DisposeAsync();
                }
            }
            catch (Exception exception) { Debug.LogException(exception); }
            finally { m_lifetime.Dispose(); }
        }
    }
}

using System;
using System.Threading;
using UnityEngine;

namespace ZRAsset.Samples
{
    /// <summary>第四版下载演示：拖入构建产物 manifest.json，再填入提供 Bundle 的 HTTP 服务地址。</summary>
    public sealed class DownloadExample: MonoBehaviour
    {
        [UnityEngine.Serialization.FormerlySerializedAs("targetManifest")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField, Tooltip("由第四版构建器生成，包含 SHA-256 和文件大小。")]
        private TextAsset m_targetManifest;
        [UnityEngine.Serialization.FormerlySerializedAs("remoteBaseUrl")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private string m_remoteBaseUrl = "http://127.0.0.1:8080/";
        [UnityEngine.Serialization.FormerlySerializedAs("cacheVersion")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private string m_cacheVersion = "demo-v4";
        [UnityEngine.Serialization.FormerlySerializedAs("textAddress")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private string m_textAddress = "demo/greeting";
        [UnityEngine.Serialization.FormerlySerializedAs("useBuiltInFiles")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField, Tooltip("关闭时跳过首包文件，方便演示下载；已经验证的缓存仍优先使用。")]
        private bool m_useBuiltInFiles;

        private ResourceManager m_resources;
        private AssetHandle<TextAsset> m_asset;
        private CancellationTokenSource m_request;
        private bool m_destroyed, m_busy;
        private string m_status = "初始化中";

        private async void Start()
        {
            try {
                if (m_targetManifest == null) {
                    throw new InvalidOperationException("请在 Inspector 指定目标 manifest.json。");
                }

                var manifest = ResourceManifest.FromJson(m_targetManifest.text);
                var options = new BundleDownloadOptions(m_remoteBaseUrl, m_cacheVersion, maxConcurrentDownloads: 3);
                var builtIn = m_useBuiltInFiles ? null : string.Empty;
                ResourceManager created = await ResourceManager.CreateWithDownloadsAsync(manifest, options, builtIn);
                if (m_destroyed) { await created.DisposeAsync(); return; }
                m_resources = created;
                m_resources.DownloadProgressChanged += OnProgress;
                m_status = "就绪：预下载只准备文件；加载会再从缓存创建 Unity 对象。";
            }
            catch (Exception exception) { m_status = exception.Message; Debug.LogException(exception); }
        }

        private void OnProgress(BundleDownloadProgress progress)
        {
            m_status = $"{progress.BundleName} | {progress.State} | {progress.ReceivedBytes}/{progress.TotalBytes} 字节 | 第 {progress.Attempt} 次尝试";
        }

        private void Update()
        {
            m_resources?.UnloadUnused();
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(15, 15, 740, 220), GUI.skin.box);
            GUILayout.Label("ZRAsset V4 下载与缓存演示");
            GUILayout.Label(m_status);
            GUI.enabled = m_resources != null && !m_busy;
            if (GUILayout.Button("预下载并加载文本资源")) {
                DownloadAndLoad();
            }

            GUI.enabled = m_busy;
            if (GUILayout.Button("取消本次等待")) {
                m_request?.Cancel();
            }

            GUI.enabled = true;
            GUILayout.Label("重复运行可验证缓存命中；停止 HTTP 服务后也能读取已校验缓存。");
            GUILayout.EndArea();
        }

        private async void DownloadAndLoad()
        {
            m_busy = true;
            m_request = new CancellationTokenSource();
            try {
                m_asset?.Release(); m_asset = null;
                m_resources.UnloadUnused(true);
                // 此阶段支持取消；不会生成实例，也不增加资源 Handle 引用。
                await m_resources.DownloadDependenciesAsync(new[] { m_textAddress }, DownloadPriority.High, m_request.Token);
                m_asset = m_resources.LoadAssetAsync<TextAsset>(m_textAddress, m_request.Token);
                TextAsset text = await m_asset.Operation;
                m_status = "加载成功：" + text.text;
            }
            catch (OperationCanceledException) { m_status = "本次请求已取消；其他消费者仍可继续使用共享下载。"; }
            catch (Exception exception) { m_status = exception.Message; Debug.LogException(exception); }
            finally { m_request.Dispose(); m_request = null; m_busy = false; }
        }

        private async void OnDestroy()
        {
            m_destroyed = true;
            m_request?.Cancel();
            m_asset?.Release();
            if (m_resources == null) {
                return;
            }

            m_resources.DownloadProgressChanged -= OnProgress;
            try { await m_resources.DisposeAsync(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}

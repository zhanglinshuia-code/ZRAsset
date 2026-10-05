using System;
using System.Threading;
using UnityEngine;

namespace ZRAsset.Samples
{
    /// <summary>命名包入门示例。先构建 demo 包并复制到首包目录；本组件独占该包。</summary>
    public sealed class PackageQuickStart: MonoBehaviour
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private string m_packageName = "demo";
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private string m_address = "demo/greeting";
        [SerializeField] private bool m_showStatus = true;
        private string m_status = "正在初始化资源包…";
        public bool IsReady { get; private set; }
        public string Status => m_status;
        private readonly CancellationTokenSource m_lifetime = new();
        private ResourcePackage m_package;
        private ResourceScope m_scope;
        private ResourceOperationBase m_startup;
        private bool m_destroyed;
        private bool m_closing;

        private async void Start()
        {
            try {
                m_startup = OpenAsync();
                await m_startup;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) {
                m_status = "加载失败：" + error.Message + "\n请先执行示例的构建并打开菜单。";
                Debug.LogException(error, this);
            }
            finally {
                if (m_destroyed || m_startup == null || m_startup.IsFaulted || m_startup.IsCanceled) {
                    await CloseAsync();
                }
            }
        }

        private async ResourceOperationBase OpenAsync()
        {
            m_package = ResourcePackages.CreatePackage(m_packageName);
            await m_package.InitializeAsync(new ResourceInitializationOptions(), m_lifetime.Token);
            m_lifetime.Token.ThrowIfCancellationRequested();
            m_scope = m_package.CreateScope("quick-start", cancellationToken: m_lifetime.Token);
            TextAsset text = await m_scope.LoadAsync<TextAsset>(m_address, m_lifetime.Token);
            IsReady = true;
            m_status = "AssetBundle 加载成功\n\n" + text.text;
            Debug.Log("ZRAsset 加载成功：" + text.text, this);
        }

        private void OnGUI()
        {
            if (m_showStatus) { GUI.Label(new Rect(24, 24, Screen.width - 48, 160), m_status); }
        }

        private void Update()
        {
            if (!m_closing && m_package?.IsInitialized == true) { m_package.UnloadUnused(); }
        }

        private async void OnDestroy()
        {
            m_destroyed = true;
            if (m_closing) { return; }
            m_lifetime.Cancel();
            // 初始化尚未结束时由 Start 的 finally 关闭，避免与包初始化并发关闭。
            if (m_startup == null || m_startup.IsDone) { await CloseAsync(); }
        }

        private async ResourceOperationBase CloseAsync()
        {
            if (m_closing) { return; }
            m_closing = true;
            try {
                if (m_scope != null) { await m_scope.DisposeAsync(); }
                if (m_package != null) { await m_package.DisposeAsync(); }
            }
            catch (Exception error) { Debug.LogException(error); }
            finally { m_lifetime.Dispose(); }
        }
    }
}

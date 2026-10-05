using System;
using System.Threading;
using UnityEngine;
using ZRAsset.HotUpdate;

namespace ZRAsset.Samples
{
    /// <summary>可放在业务已有启动场景的根对象上；不创建场景、不指定业务入口，也不自动安装任何插件。</summary>
    public sealed class HotUpdateBootstrapComponent: MonoBehaviour
    {
        [UnityEngine.Serialization.FormerlySerializedAs("configuration")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private HotUpdateStartupConfig m_configuration;
        [UnityEngine.Serialization.FormerlySerializedAs("startAutomatically")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private bool m_startAutomatically = true;
        [UnityEngine.Serialization.FormerlySerializedAs("showStatus")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private bool m_showStatus = true;
        [UnityEngine.Serialization.FormerlySerializedAs("confirmDownload")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private bool m_confirmDownload;
        private OperationCompletionSource<bool> m_downloadConsent;
        [UnityEngine.Serialization.FormerlySerializedAs("simulateAlreadyLoadedCodeInEditor")]
        [Tooltip("仅 Editor 调试使用；运行的是 Editor 已加载程序集，不是下载的 DLL。Player 忽略此选项。")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private bool m_simulateAlreadyLoadedCodeInEditor;
        private CancellationTokenSource m_cancellation;
        private ResourceOperationBase<HotUpdateBootstrapResult> m_startup;
        private string m_status = "尚未启动";
        private bool m_destroyed;
        public HotUpdateBootstrap Bootstrap { get; private set; }
        public ResourceManager Resources
        {
            get
            {
                return Bootstrap?.Resources;
            }
        }

        private void Awake()
        {
            if (transform.parent == null) {
                DontDestroyOnLoad(gameObject);
            }
        }
        private async void Start()
        {
            if (!m_startAutomatically) {
                return;
            }

            try { await StartConfiguredAsync(); }
            catch (Exception error) { m_status = "启动失败：" + error.Message; Debug.LogException(error); }
        }
        public ResourceOperationBase<HotUpdateBootstrapResult> StartConfiguredAsync()
        {
            if (m_destroyed) {
                throw new ObjectDisposedException(nameof(HotUpdateBootstrapComponent));
            }

            if (m_startup != null) {
                return m_startup;
            }

            if (m_configuration == null) {
                throw new InvalidOperationException("请指定 ZRAsset 启动配置。");
            }

            if (m_confirmDownload && !m_showStatus) {
                throw new InvalidOperationException("示例下载确认需要启用状态界面。");
            }

            IHotUpdateRuntime runtime = null;
#if UNITY_EDITOR
            if (m_simulateAlreadyLoadedCodeInEditor) {
                runtime = new EditorSimulationHotUpdateRuntime();
            }
#endif
            Bootstrap = new HotUpdateBootstrap(m_configuration.CreateOptions(beforePrepareAsync: ConfirmDownloadAsync), runtime);
            Bootstrap.StateChanged += state => m_status = Describe(state);
            Bootstrap.DownloadProgressChanged += progress => m_status = $"下载 {progress.BundleName}：{progress.ReceivedBytes}/{progress.TotalBytes} 字节";
            m_cancellation = new CancellationTokenSource();
            return m_startup = Bootstrap.StartAsync(m_cancellation.Token);
        }
        public void CancelStartup()
        {
            m_cancellation?.Cancel();
        }

        private async ResourceOperationBase ConfirmDownloadAsync(ResourcePreparationPlan plan, CancellationToken token)
        {
            if (!m_confirmDownload || plan.DownloadBytes == 0) {
                return;
            }

            m_downloadConsent = new OperationCompletionSource<bool>();
            try {
                using (token.Register(() => m_downloadConsent.TrySetCanceled())) {
                    await m_downloadConsent.Operation;
                }
            }
            finally { m_downloadConsent = null; }
        }
        private void Update()
        {
            Resources?.UnloadUnused();
        }

        private void OnGUI()
        {
            if (!m_showStatus) {
                return;
            }

            GUILayout.BeginArea(new Rect(16, 16, 620, 240), GUI.skin.box);
            GUILayout.Label("ZRAsset 资源与代码启动");
            GUILayout.Label(m_status);
            if (Bootstrap != null && Bootstrap.PreparationProgress.TotalBundles > 0) {
                GUILayout.Label($"资源检查/准备：{Bootstrap.PreparationProgress.CompletedBundles}/{Bootstrap.PreparationProgress.TotalBundles} 个包，" +
                    $"{Bootstrap.PreparationProgress.CompletedBytes}/{Bootstrap.PreparationProgress.TotalBytes} 字节");
            }

            if (m_downloadConsent != null && GUILayout.Button("确认下载")) {
                m_downloadConsent.TrySetResult(true);
            }

            if (Bootstrap != null && !Bootstrap.CacheCleanupOperation.IsDone) {
                GUILayout.Label("旧主包缓存清理中，资源已可使用");
            }

            if (Bootstrap?.CacheCleanup != null) {
                GUILayout.Label($"旧主包清理：释放 {Bootstrap.CacheCleanup.DeletedBytes} 字节，占用待清理 {Bootstrap.CacheCleanup.BusyHosts} 个目录");
            }

            if (Bootstrap?.PreparationPlan != null) {
                GUILayout.Label($"预计下载 {Bootstrap.PreparationPlan.DownloadBytes} 字节，旧版本复用 {Bootstrap.PreparationPlan.ReuseBytes} 字节");
            }

            if (Bootstrap?.UpdateError != null) {
                GUILayout.Label((Bootstrap.State == HotUpdateBootstrapState.Started
                ? "更新未完成，已使用本地活动版本：" : "在线更新失败：") + Bootstrap.UpdateError.Message);
            }

            if (Bootstrap?.Error != null) {
                GUILayout.Label(Bootstrap.Error.Message);
            }

            if (m_startup != null && !m_startup.IsDone && GUILayout.Button("取消代码提交前的启动")) {
                CancelStartup();
            }

            GUILayout.EndArea();
        }
        private async void OnDestroy()
        {
            m_destroyed = true;
            m_cancellation?.Cancel();
            try {
                if (m_startup != null) { try { await m_startup; } catch { } }
                if (Bootstrap != null) {
                    await Bootstrap.DisposeAsync();
                }
            }
            catch (Exception error) { Debug.LogException(error); }
            finally { m_cancellation?.Dispose(); m_cancellation = null; }
        }
        private static string Describe(HotUpdateBootstrapState state)
        {
            return state switch
            {
                HotUpdateBootstrapState.CheckingEnvironment => "检查热更环境",
                HotUpdateBootstrapState.CheckingRelease => "验证签名发布",
                HotUpdateBootstrapState.Planning => "检查缓存与下载量",
                HotUpdateBootstrapState.AwaitingDownloadConsent => "等待下载确认",
                HotUpdateBootstrapState.Preparing => "准备资源版本",
                HotUpdateBootstrapState.Activating => "激活资源版本",
                HotUpdateBootstrapState.OpeningResources => "打开资源",
                HotUpdateBootstrapState.StartingCode => "加载代码并执行入口",
                HotUpdateBootstrapState.Started => "启动完成",
                HotUpdateBootstrapState.RequiresRestart => "代码提交后发生错误，需要重启进程",
                HotUpdateBootstrapState.Failed => "启动失败",
                HotUpdateBootstrapState.Closed => "资源已关闭",
                _ => "尚未启动"
            };
        }
    }
}

using System;
using UnityEngine;
using UnityEngine.Events;
using ZRAsset.HotUpdate;

namespace ZRAsset.Samples
{
    /// <summary>启动流程参考界面。业务就绪事件应加载首个业务界面，成功后调用 ConfirmBusinessReady。</summary>
    public sealed class ProductionStartupComponent: MonoBehaviour
    {
        [UnityEngine.Serialization.FormerlySerializedAs("configuration")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private HotUpdateStartupConfig m_configuration;
        [UnityEngine.Serialization.FormerlySerializedAs("startAutomatically")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private bool m_startAutomatically = true;
        [UnityEngine.Serialization.FormerlySerializedAs("confirmDownload")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private bool m_confirmDownload = true;
        [UnityEngine.Serialization.FormerlySerializedAs("showInterface")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Unity serialization requires a named mutable backing field.")]
        [SerializeField] private bool m_showInterface = true;
        [UnityEngine.Serialization.FormerlySerializedAs("businessReadyTimeoutSeconds")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField, Min(1)] private float m_businessReadyTimeoutSeconds = 60;
        [UnityEngine.Serialization.FormerlySerializedAs("playerUpdateUrl")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private string m_playerUpdateUrl;
        [UnityEngine.Serialization.FormerlySerializedAs("interfaceFont")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private Font m_interfaceFont;
        [UnityEngine.Serialization.FormerlySerializedAs("onBusinessReadyRequested")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Unity serialization requires a named mutable backing field.")]
        [SerializeField] private UnityEvent m_onBusinessReadyRequested = new();
        [UnityEngine.Serialization.FormerlySerializedAs("onStarted")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Unity serialization requires a named mutable backing field.")]
        [SerializeField] private UnityEvent m_onStarted = new();
        [UnityEngine.Serialization.FormerlySerializedAs("onRestartRequested")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private UnityEvent m_onRestartRequested = new();
        [UnityEngine.Serialization.FormerlySerializedAs("onStorageHelpRequested")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private UnityEvent m_onStorageHelpRequested = new();
        [UnityEngine.Serialization.FormerlySerializedAs("onSupportRequested")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Unity assigns serialized fields when loading assets.")]
        [SerializeField] private UnityEvent m_onSupportRequested = new();
        private bool m_destroyed;
        private string m_configurationError;
        private string m_cacheRootOverride;
        private Vector2 m_scroll;
        private GUIStyle m_label, m_button;
        public HotUpdateStartupController Controller { get; private set; }
        public ResourceManager Resources
        {
            get
            {
                return Controller?.Resources;
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Unity serialization requires a named mutable backing field.")]
        public UnityEvent BusinessReadyRequested
        {
            get
            {
                return m_onBusinessReadyRequested;
            }
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Unity serialization requires a named mutable backing field.")]
        public UnityEvent Started
        {
            get
            {
                return m_onStarted;
            }
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Unity serialization requires a named mutable backing field.")]
        public bool IsInterfaceVisible
        {
            get
            {
                return m_showInterface;
            }
        }
        public Font InterfaceFont
        {
            get
            {
                return m_interfaceFont;
            }
            set { m_interfaceFont = value; m_label = m_button = null; }
        }

        /// <summary>在启动前配置，便于宿主代码接线；开始后配置由本次会话固定。</summary>
        public void Configure(HotUpdateStartupConfig settings, bool automaticStart = true, string cacheRootOverride = null)
        {
            if (m_destroyed || Controller != null) {
                throw new InvalidOperationException("启动后不能替换配置。");
            }

            m_configuration = settings ? settings : throw new ArgumentNullException(nameof(settings));
            m_startAutomatically = automaticStart;
            m_cacheRootOverride = cacheRootOverride;
            m_configurationError = null;
        }

        public void HideInterface()
        {
            m_showInterface = false;
        }

        private void Awake()
        {
            if (transform.parent == null) {
                DontDestroyOnLoad(gameObject);
            }
        }
        private void Start()
        {
            if (m_startAutomatically) {
                Begin();
            }
        }
        public void Begin()
        {
            if (m_destroyed) {
                return;
            }

            if (m_configuration == null) { m_configurationError = "请配置启动策略。"; return; }
            if (Controller == null) {
                Controller = new HotUpdateStartupController(controller => new HotUpdateBootstrap(
                    m_configuration.CreateOptions(cacheRootOverride: m_cacheRootOverride, beforePrepareAsync: controller.WaitForDownloadConsentAsync)))
                {
                    RequireDownloadConsent = m_confirmDownload,
                    BusinessReadyTimeoutSeconds = m_businessReadyTimeoutSeconds
                };
                Controller.BusinessReadyRequested += _ => BusinessReadyRequested.Invoke();
                Controller.Started += _ => Started.Invoke();
            }
            Observe(Controller.StartAsync());
        }

        public void ConfirmDownload()
        {
            Controller?.ConfirmDownload();
        }

        public void ConfirmBusinessReady()
        {
            Controller?.ConfirmBusinessReady();
        }

        public void ReportBusinessFailure(string message)
        {
            Controller?.FailBusinessReady(new InvalidOperationException(message));
        }

        public void Cancel()
        {
            Controller?.Cancel();
        }

        public void Retry()
        {
            if (Controller?.CanRetry == true) {
                Observe(Controller.RetryAsync());
            }
        }
        public void RecheckContent()
        {
            if (Controller?.CanRecheckContent == true) {
                Observe(Controller.RecheckContentAsync());
            }
        }
        private async void Observe(ResourceOperationBase operation)
        {
            try { await operation; }
            catch (OperationCanceledException) { }
            catch (Exception error) { Debug.LogException(error); }
        }

        private void Update()
        {
            Resources?.UnloadUnused();
        }

        private void OnGUI()
        {
            if (!IsInterfaceVisible || m_destroyed) {
                return;
            }

            if (m_label == null) {
                m_label = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 18, font = m_interfaceFont };
                m_button = new GUIStyle(GUI.skin.button) { fontSize = 18, font = m_interfaceFont, fixedHeight = 42 };
            }
            var margin = Math.Min(24, Math.Min(Screen.width, Screen.height) * .04f);
            GUILayout.BeginArea(new Rect(margin, margin, Math.Max(1, Math.Min(620, Screen.width - (margin * 2))),
                Math.Max(1, Math.Min(420, Screen.height - (margin * 2)))), GUI.skin.box);
            m_scroll = GUILayout.BeginScrollView(m_scroll);
            GUILayout.Label(m_configurationError ?? Controller?.StatusText ?? "准备启动", m_label);
            if (Controller == null) {
                if (GUILayout.Button("开始", m_button)) {
                    Begin();
                }
            }
            else {
                ResourcePreparationPlan plan = Controller.Plan;
                if (plan != null) {
                    GUILayout.Label($"预计下载 {Size(plan.DownloadBytes)}，本地可复用 {Size(plan.TotalBytes - plan.DownloadBytes)}", m_label);
                    GUILayout.Label($"下载内容已收到 {Size(Controller.DownloadedBytes)}", m_label);
                    var stage = Controller.Preparation.Phase == ResourcePreparationPhase.Planning ? "本地资源检查" : "资源准备";
                    GUILayout.Label($"{stage} {Controller.Preparation.CompletedBundles}/{Controller.Preparation.TotalBundles}", m_label);
                }
                if (Controller.AwaitingDownloadConsent && GUILayout.Button("确认下载", m_button)) {
                    ConfirmDownload();
                }

                if (Controller.IsRunning && GUILayout.Button("取消", m_button)) {
                    Cancel();
                }

                if (Controller.CanRetry && GUILayout.Button("重试", m_button)) {
                    Retry();
                }

                if (Controller.CanRecheckContent && GUILayout.Button("重新检查资源", m_button)) {
                    RecheckContent();
                }

                ResourceRecoveryAction recovery = Controller.Failure.Recovery;
                if ((recovery & ResourceRecoveryAction.FreeStorage) != 0 && GUILayout.Button("查看空间清理帮助", m_button)) {
                    m_onStorageHelpRequested.Invoke();
                }

                if ((recovery & ResourceRecoveryAction.UpdatePlayer) != 0) {
                    if (Uri.TryCreate(m_playerUpdateUrl, UriKind.Absolute, out Uri url) && url.Scheme == Uri.UriSchemeHttps) {
                        if (GUILayout.Button("更新客户端", m_button)) {
                            Application.OpenURL(url.AbsoluteUri);
                        }
                    }
                    else {
                        GUILayout.Label("请通过游戏发行渠道更新客户端。", m_label);
                    }
                }
                if ((recovery & ResourceRecoveryAction.Restart) != 0) {
                    GUILayout.Label("请退出游戏后重新启动。", m_label);
                    if (GUILayout.Button("请求重启", m_button)) {
                        m_onRestartRequested.Invoke();
                    }
                }
                if ((recovery & ResourceRecoveryAction.ContactSupport) != 0 && GUILayout.Button("联系客服", m_button)) {
                    m_onSupportRequested.Invoke();
                }

                if (Controller.Failure.Code != ResourceErrorCode.None) {
                    GUILayout.Label($"错误编号：{Controller.Failure.Code} / {Controller.Failure.Stage}，尝试次数：{Controller.Attempt}", m_label);
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static string Size(long bytes)
        {
            return $"{bytes / (1024d * 1024):F2} MiB";
        }

        private async void OnDestroy()
        {
            m_destroyed = true;
            if (Controller == null) {
                return;
            }

            try { await Controller.DisposeAsync(); }
            catch (Exception error) { Debug.LogException(error); }
        }
    }
}

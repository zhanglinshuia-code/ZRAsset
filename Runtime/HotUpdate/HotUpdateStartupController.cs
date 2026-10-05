using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ZRAsset.HotUpdate
{
    /// <summary>与 UI 无关的启动流程。下载同意、业务就绪及恢复动作均由宿主明确驱动。</summary>
    public sealed class HotUpdateStartupController
    {
        private readonly HotUpdateStartupSession m_session;
        private readonly Dictionary<string, long> m_received = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_sizes = new(StringComparer.Ordinal);
        private OperationCompletionSource<bool> m_consent;
        private OperationCompletionSource<bool> m_ready;
        private ResourceOperationBase<HotUpdateBootstrapResult> m_operation;
        private bool m_closed;
        private bool m_isReady;

        public bool RequireDownloadConsent { get; set; } = true;
        public double BusinessReadyTimeoutSeconds { get; set; } = 60;
        public HotUpdateBootstrap Bootstrap
        {
            get
            {
                return m_session.Bootstrap;
            }
        }

        public ResourceManager Resources
        {
            get
            {
                return Bootstrap?.Resources;
            }
        }

        public ResourceFailure Failure
        {
            get
            {
                return m_session.Failure;
            }
        }

        public ResourcePreparationPlan Plan { get; private set; }
        public ResourcePreparationProgress Preparation { get; private set; }
        public long DownloadedBytes { get; private set; }
        public bool AwaitingDownloadConsent
        {
            get
            {
                return m_consent != null;
            }
        }

        public bool AwaitingBusinessReady
        {
            get
            {
                return m_ready != null;
            }
        }

        public bool CanRetry
        {
            get
            {
                return !m_closed && m_session.CanRetry;
            }
        }

        public bool CanRecheckContent
        {
            get
            {
                return !m_closed && m_session.CanRecheckContent;
            }
        }

        public bool IsRunning { get; private set; }
        public bool IsReady
        {
            get
            {
                return !m_closed && m_isReady;
            }
        }

        public int Attempt
        {
            get
            {
                return m_session.Attempt;
            }
        }

        public string StatusText { get; private set; } = "准备启动";
        public event Action Changed;
        public event Action<ResourceManager> BusinessReadyRequested;
        public event Action<HotUpdateBootstrapResult> Started;

        /// <param name="factory">每次返回新启动器；将 BeforePrepareAsync 绑定到 controller.WaitForDownloadConsentAsync。</param>
        public HotUpdateStartupController(Func<HotUpdateStartupController, HotUpdateBootstrap> factory)
        {
            if (factory == null) {
                throw new ArgumentNullException(nameof(factory));
            }

            m_session = new HotUpdateStartupSession(() =>
            {
                Plan = null; Preparation = default; DownloadedBytes = 0;
                m_received.Clear(); m_sizes.Clear();
                HotUpdateBootstrap bootstrap = factory(this) ?? throw new InvalidOperationException("启动器工厂返回 null。");
                bootstrap.StateChanged += state => { StatusText = Describe(state); Notify(Changed); };
                bootstrap.PreparationProgressChanged += progress => { Preparation = progress; Notify(Changed); };
                bootstrap.DownloadProgressChanged += OnDownload;
                bootstrap.ConfirmReadyAsync = WaitForBusinessReadyAsync;
                return bootstrap;
            });
        }

        public ResourceOperationBase<HotUpdateBootstrapResult> StartAsync()
        {
            CheckOpen();
            return m_operation ??= ObserveAsync(m_session.StartAsync());
        }

        public ResourceOperationBase<HotUpdateBootstrapResult> RetryAsync()
        {
            CheckOpen();
            return m_operation = ObserveAsync(m_session.RetryAsync());
        }

        public ResourceOperationBase<HotUpdateBootstrapResult> RecheckContentAsync()
        {
            CheckOpen();
            return m_operation = ObserveAsync(m_session.RecheckContentAsync());
        }

        public void Cancel() { CheckOpen(); m_session.Cancel(); }
        public void ConfirmDownload() { CheckOpen(); m_consent?.TrySetResult(true); }
        public void ConfirmBusinessReady() { CheckOpen(); m_ready?.TrySetResult(true); }
        public void FailBusinessReady(Exception error) { CheckOpen(); m_ready?.TrySetException(error ?? new InvalidOperationException("业务启动失败。")); }

        public async ResourceOperationBase WaitForDownloadConsentAsync(ResourcePreparationPlan plan, CancellationToken token)
        {
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            m_received.Clear(); m_sizes.Clear(); DownloadedBytes = 0;
            foreach (ResourcePreparationBundle bundle in plan.Bundles) {
                m_sizes.Add(bundle.Name, bundle.Bytes);
            }

            if (!RequireDownloadConsent || plan.DownloadBytes == 0) { Notify(Changed); return; }
            m_consent = new OperationCompletionSource<bool>();
            try {
                Notify(Changed);
                using (token.Register(() => m_consent?.TrySetCanceled())) {
                    await m_consent.Operation;
                }
            }
            finally { m_consent = null; Notify(Changed); }
        }

        private async ResourceOperationBase WaitForBusinessReadyAsync(ResourceManager resources, CancellationToken token)
        {
            if (double.IsNaN(BusinessReadyTimeoutSeconds) || double.IsInfinity(BusinessReadyTimeoutSeconds) || BusinessReadyTimeoutSeconds <= 0) {
                throw new ArgumentOutOfRangeException(nameof(BusinessReadyTimeoutSeconds));
            }

            m_ready = new OperationCompletionSource<bool>();
            var started = Stopwatch.GetTimestamp();
            try {
                Notify(Changed);
                // 业务事件的异常必须使启动失败，不能误报为已经就绪。
                BusinessReadyRequested?.Invoke(resources);
                while (!m_ready.Operation.IsDone) {
                    token.ThrowIfCancellationRequested();
                    if ((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency >= BusinessReadyTimeoutSeconds) {
                        throw new TimeoutException("等待业务就绪超时。");
                    }

                    await ResourceOperationBase.Yield();
                }
                await m_ready.Operation;
            }
            finally { m_ready = null; Notify(Changed); }
        }

        private async ResourceOperationBase<HotUpdateBootstrapResult> ObserveAsync(ResourceOperationBase<HotUpdateBootstrapResult> operation)
        {
            IsRunning = true; m_isReady = false;
            try {
                HotUpdateBootstrapResult result = await operation;
                IsRunning = false; m_isReady = true;
                StatusText = result.UsedOfflineFallback ? "已使用本地版本进入游戏" : "启动完成";
                Notify(Changed);
                if (Started != null) {
                    foreach (Action<HotUpdateBootstrapResult> listener in Started.GetInvocationList()) {
                        try { listener(result); } catch (Exception error) { UnityEngine.Debug.LogException(error); }
                    }
                }

                return result;
            }
            catch {
                IsRunning = false; m_isReady = false;
                StatusText = DescribeFailure(m_session.Failure);
                Notify(Changed);
                throw;
            }
        }

        private void OnDownload(BundleDownloadProgress progress)
        {
            if (!m_sizes.TryGetValue(progress.BundleName, out var size)) {
                return;
            }

            m_received.TryGetValue(progress.BundleName, out var previous);
            var received = Math.Max(0, Math.Min(size, progress.ReceivedBytes));
            m_received[progress.BundleName] = received;
            DownloadedBytes += received - previous;
            Notify(Changed);
        }

        public async ResourceOperationBase DisposeAsync()
        {
            ResourcePackages.CheckThread();
            if (m_closed) {
                return;
            }

            await m_session.DisposeAsync();
            m_closed = true;
            Changed = null; BusinessReadyRequested = null; Started = null;
        }

        private void CheckOpen()
        {
            ResourcePackages.CheckThread();
            if (m_closed) {
                throw new ObjectDisposedException(nameof(HotUpdateStartupController));
            }
        }

        private static void Notify(Action listeners)
        {
            if (listeners == null) {
                return;
            }

            foreach (Action listener in listeners.GetInvocationList()) {
                try { listener(); } catch (Exception error) { UnityEngine.Debug.LogException(error); }
            }
        }

        public static string DescribeFailure(ResourceFailure failure)
        {
            return failure.Code switch
            {
                ResourceErrorCode.Cancelled => "已取消，可重新开始",
                ResourceErrorCode.Network or ResourceErrorCode.Timeout => "连接失败，请检查网络后重试",
                ResourceErrorCode.InsufficientSpace => "存储空间不足，请释放空间后重试",
                ResourceErrorCode.Integrity or ResourceErrorCode.NotFound => "资源校验失败，可重新检查并准备资源",
                ResourceErrorCode.HostUpgradeRequired => "需要更新游戏客户端",
                ResourceErrorCode.RestartRequired => "需要重启游戏后恢复",
                _ => "启动失败，请联系客服并提供错误编号：" + failure.Code
            };
        }

        private static string Describe(HotUpdateBootstrapState state)
        {
            return state switch
            {
                HotUpdateBootstrapState.CheckingEnvironment => "检查运行环境",
                HotUpdateBootstrapState.CheckingRelease => "检查更新",
                HotUpdateBootstrapState.Planning => "检查本地资源",
                HotUpdateBootstrapState.AwaitingDownloadConsent => "等待确认下载",
                HotUpdateBootstrapState.Preparing => "下载并校验资源",
                HotUpdateBootstrapState.Activating => "应用更新",
                HotUpdateBootstrapState.OpeningResources => "打开资源",
                HotUpdateBootstrapState.StartingCode => "启动游戏",
                HotUpdateBootstrapState.AwaitingReady => "加载游戏内容",
                HotUpdateBootstrapState.Started => "启动完成",
                HotUpdateBootstrapState.RequiresRestart => "需要重启游戏后恢复",
                HotUpdateBootstrapState.Failed => "启动失败",
                HotUpdateBootstrapState.Closed => "已关闭",
                _ => "准备启动"
            };
        }
    }
}

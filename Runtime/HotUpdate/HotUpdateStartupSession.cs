using System;
using System.Threading;

namespace ZRAsset.HotUpdate
{
    /// <summary>为启动页面管理取消和重试。只在代码尚未提交且错误允许重试时创建新启动器。</summary>
    public sealed class HotUpdateStartupSession
    {
        private readonly Func<HotUpdateBootstrap> m_factory;
        private CancellationTokenSource m_cancellation;
        private ResourceOperationBase<HotUpdateBootstrapResult> m_attempt;
        private bool m_closed;
        private bool m_closing;

        public HotUpdateBootstrap Bootstrap { get; private set; }
        public int Attempt { get; private set; }
        public ResourceFailure Failure { get { return m_attempt == null ? default : m_attempt.Failure; } }
        public bool CanRetry
        {
            get
            {
                return !m_closed && !m_closing && m_attempt != null && m_attempt.IsDone && m_attempt.Status != OperationStatus.Succeeded &&
                    Bootstrap?.State != HotUpdateBootstrapState.RequiresRestart &&
                    (Failure.Code == ResourceErrorCode.Cancelled || (Failure.Recovery & ResourceRecoveryAction.Retry) != 0);
            }
        }

        /// <summary>允许用户重新校验并准备内容；不删除活动版本、不放宽签名或宿主校验。</summary>
        public bool CanRecheckContent
        {
            get
            {
                return !m_closed && !m_closing && m_attempt != null && m_attempt.IsDone &&
            m_attempt.Status != OperationStatus.Succeeded && Bootstrap?.State != HotUpdateBootstrapState.RequiresRestart &&
            (Failure.Recovery & ResourceRecoveryAction.RepairContent) != 0;
            }
        }

        public ResourceOperationBase<HotUpdateBootstrapResult> RecheckContentAsync(CancellationToken cancellationToken = default)
        {
            CheckOpen();
            return !CanRecheckContent ? throw new InvalidOperationException("当前启动状态不允许重新校验内容。") : BeginAttempt(cancellationToken);
        }

        public HotUpdateStartupSession(Func<HotUpdateBootstrap> factory)
        {
            m_factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public ResourceOperationBase<HotUpdateBootstrapResult> StartAsync(CancellationToken cancellationToken = default)
        {
            CheckOpen();
            return m_attempt ?? BeginAttempt(cancellationToken);
        }

        public ResourceOperationBase<HotUpdateBootstrapResult> RetryAsync(CancellationToken cancellationToken = default)
        {
            CheckOpen();
            return !CanRetry ? throw new InvalidOperationException("当前启动状态不允许重试；代码提交后需要重启进程。") : BeginAttempt(cancellationToken);
        }

        public void Cancel()
        {
            ResourcePackages.CheckThread();
            if (m_attempt != null && !m_attempt.IsDone) {
                m_cancellation.Cancel();
            }
        }

        /// <summary>先释放业务作用域；关闭不撤销已经加载的代码。</summary>
        public async ResourceOperationBase DisposeAsync()
        {
            ResourcePackages.CheckThread();
            if (m_closed) {
                return;
            }
            if (m_closing) {
                throw new InvalidOperationException("启动会话正在关闭。");
            }
            m_closing = true;
            try {
                Cancel();
                if (m_attempt != null) {
                    try {
                        await m_attempt;
                    }
                    catch (Exception) {
                        // 原启动操作保留错误；这里继续释放其持有的资源。
                    }
                }
                if (Bootstrap != null) {
                    await Bootstrap.DisposeAsync();
                }
                m_cancellation?.Dispose();
                m_closed = true;
            }
            finally {
                m_closing = false;
            }
        }

        private ResourceOperationBase<HotUpdateBootstrapResult> BeginAttempt(CancellationToken token)
        {
            m_cancellation?.Dispose();
            m_cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Attempt++;
            m_attempt = RunAsync(m_cancellation.Token);
            return m_attempt;
        }

        private async ResourceOperationBase<HotUpdateBootstrapResult> RunAsync(CancellationToken token)
        {
            await ResourceOperationBase.Yield();
            token.ThrowIfCancellationRequested();
            HotUpdateBootstrap previous = Bootstrap;
            if (previous != null) {
                await previous.DisposeAsync();
            }
            token.ThrowIfCancellationRequested();
            HotUpdateBootstrap next = m_factory() ?? throw new InvalidOperationException("启动器工厂返回了 null。");
            if (ReferenceEquals(previous, next)) {
                throw new InvalidOperationException("每次重试必须创建新的启动器。");
            }
            Bootstrap = next;
            if (Attempt > 1) {
                ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.Recovery, attempt: Attempt));
            }
            return await next.StartAsync(token);
        }

        private void CheckOpen()
        {
            ResourcePackages.CheckThread();
            if (m_closed || m_closing) {
                throw new ObjectDisposedException(nameof(HotUpdateStartupSession));
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace ZRAsset
{
    public sealed partial class ResourceManager
    {
        private int m_disposalReservations;

        private ResourceOperationBase BeginDisposal(bool force)
        {
            m_closing = true;
            m_shutdownRequested = true;
            var completion = new OperationCompletionSource<bool>();
            if (force) { m_forceDisposal = completion.Operation; }
            else { m_disposeTask = completion.Operation; }
            _ = CompleteDisposalAsync(completion, force);
            return completion.Operation;
        }

        private async ResourceOperationBase CompleteDisposalAsync(OperationCompletionSource<bool> completion, bool force)
        {
            try {
                if (force) { await ForceDisposeCoreAsync(); }
                else { await DrainAsync(); }
                completion.TrySetResult(true);
            }
            catch (Exception error) { completion.TrySetException(error); }
        }

        /// <summary>先封锁整组入口，再启动任何不可逆关闭；检查失败时撤销所有尚未提交的屏障。</summary>
        internal static ResourceOperationBase DisposeTogether(IEnumerable<ResourceManager> managers)
        {
            var reservations = new List<DisposalReservation>();
            try {
                foreach (ResourceManager manager in managers) { reservations.Add(new DisposalReservation(manager)); }
                return ResourceOperationBase.WhenAll(reservations.Select(reservation => reservation.Commit()).ToArray());
            }
            finally {
                for (var i = reservations.Count - 1; i >= 0; i--) { reservations[i].Dispose(); }
            }
        }

        private sealed class DisposalReservation: IDisposable
        {
            private readonly ResourceManager m_owner;
            private readonly bool m_wasClosing;
            private bool m_committed;

            internal DisposalReservation(ResourceManager owner)
            {
                owner.CheckThread();
                if (owner.m_unloadAll != null && !owner.m_unloadAll.IsDone) {
                    throw new InvalidOperationException("请先等待全部资源卸载完成，再关闭管理器。");
                }
                m_owner = owner;
                m_wasClosing = owner.m_closing;
                owner.m_closing = true;
                owner.m_disposalReservations++;
                try { owner.EnsureCanDispose(); }
                catch { owner.m_disposalReservations--; owner.m_closing = m_wasClosing; throw; }
            }

            internal ResourceOperationBase Commit()
            {
                m_committed = true;
                return m_owner.m_forceDisposal ?? m_owner.m_disposeTask ?? m_owner.BeginDisposal(false);
            }

            public void Dispose()
            {
                m_owner.m_disposalReservations--;
                if (!m_committed) { m_owner.m_closing = m_wasClosing; }
            }
        }
    }
}

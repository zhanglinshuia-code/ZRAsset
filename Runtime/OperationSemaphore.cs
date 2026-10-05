using System;
using System.Collections.Generic;
using System.Threading;

namespace ZRAsset
{
    /// <summary>主线程 FIFO 并发许可。等待者取消后不会消耗后续归还的名额。</summary>
    internal sealed class OperationSemaphore
    {
        private readonly LinkedList<PermitOperation> m_waiters = new LinkedList<PermitOperation>();
        private readonly int m_maximum;
        private int m_available;
        internal OperationSemaphore(int initial, int maximum) { m_available = initial; m_maximum = maximum; }
        internal bool TryWait()
        {
            if (m_available == 0) {
                return false;
            }

            m_available--;
            return true;
        }
        internal ResourceOperationBase WaitAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (m_available > 0) { m_available--; return ResourceOperationBase.CompletedOperation; }
            var operation = new PermitOperation(token);
            operation.Node = m_waiters.AddLast(operation);
            return OperationSystem.Start(operation);
        }
        internal void Release()
        {
            while (m_waiters.Count > 0) {
                PermitOperation operation = m_waiters.First.Value;
                m_waiters.RemoveFirst();
                if (operation.Grant()) {
                    return;
                }
            }
            if (m_available == m_maximum) {
                throw new InvalidOperationException("重复归还并发许可。");
            }

            m_available++;
        }
        private sealed class PermitOperation: ResourceOperationBase
        {
            internal LinkedListNode<PermitOperation> Node;
            private readonly CancellationToken m_token;
            internal PermitOperation(CancellationToken token)
            {
                m_token = token;
            }

            protected override void OnUpdate()
            {
                m_token.ThrowIfCancellationRequested();
            }

            protected override void OnCleanup()
            {
                Node?.List?.Remove(Node);
                Node = null;
            }

            internal bool Grant()
            {
                Tick();
                if (IsDone) {
                    return false;
                }

                Succeed(); return true;
            }
        }
    }
}

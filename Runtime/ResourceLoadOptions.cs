using System;
using System.Collections.Generic;
using System.Threading;

namespace ZRAsset
{
    /// <summary>限制一次资源突发触发的文件打开与 Unity 对象反序列化数量，不改变共享与引用计数语义。</summary>
    public sealed class ResourceLoadOptions
    {
        public int MaxConcurrentBundleLoads { get; }
        public int MaxConcurrentAssetLoads { get; }
        public ResourceLocationMatch LocationMatching { get; }

        public ResourceLoadOptions(int maxConcurrentBundleLoads = 4, int maxConcurrentAssetLoads = 8,
            ResourceLocationMatch locationMatching = ResourceLocationMatch.ExactAddress)
        {
            if (maxConcurrentBundleLoads < 1 || maxConcurrentBundleLoads > 64) {
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentBundleLoads));
            }
            if (maxConcurrentAssetLoads < 1 || maxConcurrentAssetLoads > 64) {
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentAssetLoads));
            }
            MaxConcurrentBundleLoads = maxConcurrentBundleLoads;
            MaxConcurrentAssetLoads = maxConcurrentAssetLoads;
            if ((locationMatching & ~(ResourceLocationMatch.AssetPath | ResourceLocationMatch.Extensionless | ResourceLocationMatch.IgnoreCase)) != 0) {
                throw new ArgumentOutOfRangeException(nameof(locationMatching));
            }
            LocationMatching = locationMatching;
        }
    }

    public readonly struct ResourceLoadStats
    {
        public readonly int ActiveBundleLoads, QueuedBundleLoads, ActiveAssetLoads, QueuedAssetLoads;
        public ResourceLoadStats(int activeBundles, int queuedBundles, int activeAssets, int queuedAssets)
        { ActiveBundleLoads = activeBundles; QueuedBundleLoads = queuedBundles; ActiveAssetLoads = activeAssets; QueuedAssetLoads = queuedAssets; }
    }

    /// <summary>共享 Provider 保留其使用者提出过的最高优先级；取消单个句柄不会降低其他使用者的优先级。</summary>
    internal sealed class ResourceLoadPriority
    {
        internal int Value { get; private set; }
        internal event Action Raised;
        internal ResourceLoadPriority(int value) { Value = value; }
        internal void Raise(int value)
        {
            if (value <= Value) {
                return;
            }
            Value = value;
            Raised?.Invoke();
        }
    }

    /// <summary>主线程优先级许可队列；同优先级 FIFO，不抢占在途加载，重试退避不占许可。</summary>
    internal sealed class ResourceOperationLimiter
    {
        private readonly List<Waiter> m_waiters = new();
        private int m_available;
        private long m_sequence;
        private sealed class Waiter
        {
            internal readonly OperationCompletionSource<bool> Completion = new();
            internal ResourceLoadPriority Priority;
            internal Action Raised;
            internal long Sequence;
            internal int Index;
        }
        public int Active { get; private set; }
        public int Queued { get; private set; }
        public ResourceOperationLimiter(int count) { m_available = count; }
        public T RunSync<T>(Func<T> operation, bool allowBorrow = false)
        {
            var borrowed = m_available == 0;
            if (borrowed && !allowBorrow) { throw new InvalidOperationException("加载并发额度已占满，请等待现有异步请求完成后再同步加载。"); }
            if (!borrowed) { m_available--; }
            Active++;
            try { return operation(); }
            finally { Active--; if (!borrowed) { Release(); } }
        }

        public async ResourceOperationBase<T> RunAsync<T>(Func<ResourceOperationBase<T>> operation, ResourceLoadPriority priority = null)
        {
            Queued++;
            try { await WaitAsync(priority); }
            finally { Queued--; }
            Active++;
            try { return await operation(); }
            finally { Active--; Release(); }
        }

        private ResourceOperationBase WaitAsync(ResourceLoadPriority priority)
        {
            if (m_available > 0) { m_available--; return ResourceOperationBase.CompletedOperation; }
            var waiter = new Waiter { Priority = priority, Sequence = m_sequence++, Index = m_waiters.Count };
            if (priority != null) {
                waiter.Raised = () => SiftUp(waiter.Index);
                priority.Raised += waiter.Raised;
            }
            m_waiters.Add(waiter);
            SiftUp(waiter.Index);
            return waiter.Completion.Operation;
        }

        private void Release()
        {
            if (m_waiters.Count == 0) { m_available++; return; }
            Waiter next = m_waiters[0];
            var last = m_waiters.Count - 1;
            Swap(0, last);
            m_waiters.RemoveAt(last);
            if (m_waiters.Count > 0) {
                SiftDown(0);
            }
            if (next.Priority != null) {
                next.Priority.Raised -= next.Raised;
            }
            next.Index = -1;
            next.Completion.SetResult(true);
        }

        private static bool Before(Waiter left, Waiter right)
        {
            int a = left.Priority?.Value ?? 0, b = right.Priority?.Value ?? 0;
            return a > b || (a == b && left.Sequence < right.Sequence);
        }
        private void SiftUp(int index)
        {
            while (index > 0) {
                var parent = (index - 1) / 2;
                if (!Before(m_waiters[index], m_waiters[parent])) {
                    break;
                }
                Swap(index, parent); index = parent;
            }
        }
        private void SiftDown(int index)
        {
            while ((index * 2) + 1 < m_waiters.Count) {
                var child = (index * 2) + 1;
                if (child + 1 < m_waiters.Count && Before(m_waiters[child + 1], m_waiters[child])) {
                    child++;
                }
                if (!Before(m_waiters[child], m_waiters[index])) {
                    break;
                }
                Swap(index, child); index = child;
            }
        }
        private void Swap(int a, int b)
        {
            (m_waiters[a], m_waiters[b]) = (m_waiters[b], m_waiters[a]);
            m_waiters[a].Index = a; m_waiters[b].Index = b;
        }
    }
}

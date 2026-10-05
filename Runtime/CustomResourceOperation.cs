using System;
using System.Collections.Generic;

namespace ZRAsset
{
    /// <summary>可由 Package 托管的主线程操作。OnStart 只执行一次；终态通过 OnCleanup 统一释放资源。</summary>
    public abstract class CustomResourceOperation: ResourceOperationBase
    {
        private bool m_started;

        protected virtual void OnStart() { }
        protected abstract void OnTick();
        protected virtual void OnAbort() { }

        protected sealed override void OnUpdate()
        {
            if (!m_started) {
                m_started = true;
                OnStart();
            }
            if (!IsDone) { OnTick(); }
        }

        public void Abort()
        {
            ResourcePackages.CheckThread();
            if (IsDone) { return; }
            try { OnAbort(); }
            catch (Exception error) { Fail(error); }
            finally { Fail(new OperationCanceledException("自定义操作已终止。")); }
        }
    }

    public sealed partial class ResourcePackage
    {
        private readonly HashSet<ResourceOperationBase> m_operations = new();
        private uint m_packagePriority;

        /// <summary>下一调度轮生效。先比较包优先级，再比较操作优先级，同级按启动顺序执行。</summary>
        public uint PackagePriority
        {
            get { return m_packagePriority; }
            set
            {
                ResourcePackages.CheckThread();
                if (IsDisposed) {
                    throw new ObjectDisposedException("Package " + Name);
                }

                m_packagePriority = value;
                OperationSystem.MarkPrioritiesDirty();
            }
        }

        public T StartOperation<T>(T operation) where T : CustomResourceOperation
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(this);
            CheckAvailable();
            if (operation == null) { throw new ArgumentNullException(nameof(operation)); }
            if (operation.OwnerPackage != null && operation.OwnerPackage != this) {
                throw new InvalidOperationException("操作已经属于另一个 Package。");
            }
            if (operation.IsDone) { return operation; }
            if (operation.IsScheduled && operation.OwnerPackage == null) {
                throw new InvalidOperationException("操作已经在全局调度器启动。");
            }
            operation.OwnerPackage = this;
            m_operations.Add(operation);
            return OperationSystem.Start(operation);
        }

        internal void ForgetOperation(ResourceOperationBase operation)
        {
            m_operations.Remove(operation);
        }

        private void AbortOperations()
        {
            if (m_operations.Count == 0) { return; }
            // Abort/清理回调会移除自身；快照避免枚举失效，且仅在销毁包时分配。
            var snapshot = new ResourceOperationBase[m_operations.Count];
            m_operations.CopyTo(snapshot);
            foreach (CustomResourceOperation operation in snapshot) { operation.Abort(); }
        }
    }
}

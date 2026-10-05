using System;
using System.Threading;
using Object = UnityEngine.Object;

namespace ZRAsset
{
    public enum ProviderState { LoadingBundles, LoadingAsset, Succeeded, Failed, Canceled }

    /// <summary>业务持有的资源使用凭证。释放是幂等的，但释放后不允许再次访问 Asset/ResourceOperationBase。</summary>
    public sealed class AssetHandle<T>: IDisposable where T : Object
    {
        private readonly ResourceManager m_owner;
        private readonly AssetProvider m_provider;
        private readonly ResourceOperationBase<T> m_task;
        private bool m_released;
        public bool IsReleased { get { return m_released || m_provider.Revoked; } }
        public bool IsDone
        {
            get
            {
                return m_task.IsDone;
            }
        }

        public ProviderState State
        {
            get
            {
                return m_task.IsCanceled ? ProviderState.Canceled : m_provider.State;
            }
        }

        public Exception Error
        {
            get
            {
                return m_task.Exception?.GetBaseException();
            }
        }

        public ResourceOperationBase<T> Operation { get { Check(); return m_task; } }
        public T Asset
        {
            get
            {
                Check();
                return !m_task.IsDone
                    ? throw new InvalidOperationException("Asset is still loading. Await handle.Operation first.")
                    : m_task.GetAwaiter().GetResult();
            }
        }

        internal AssetHandle(ResourceManager owner, AssetProvider provider, CancellationToken cancellationToken)
        {
            m_owner = owner;
            m_provider = provider;
            m_task = WaitAsync(provider.Operation, cancellationToken);

        }

        private async ResourceOperationBase<T> WaitAsync(ResourceOperationBase<object> source, CancellationToken cancellationToken)
        {
            try {
                await ResourceOperations.WaitAsync(source, cancellationToken);
                return m_provider.Revoked ? throw new ObjectDisposedException(nameof(AssetHandle<T>)) : (T)await source;
            }
            catch (OperationCanceledException) { Release(); throw; }
        }
        private void Check()
        {
            m_owner.CheckThread();
            if (IsReleased) {
                throw new ObjectDisposedException(nameof(AssetHandle<T>));
            }
        }

        /// <summary>交回所有权，不强行停止底层共享加载；只有最后一个使用者释放后才允许回收。</summary>
        public void Release()
        {
            m_owner.CheckThread();
            if (IsReleased) {
                return;
            }

            m_released = true;
            m_owner.Release(m_provider);
        }

        public void Dispose()
        {
            Release();
        }
    }
}

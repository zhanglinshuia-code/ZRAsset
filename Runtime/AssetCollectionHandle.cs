using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Object = UnityEngine.Object;

namespace ZRAsset
{
    /// <summary>整组资源共享一份使用凭证；只读结果不转移资源所有权，释放后不能继续使用其中的对象。</summary>
    public sealed class AssetCollectionHandle<T>: IDisposable where T : Object
    {
        private readonly ResourceManager m_owner;
        private readonly AssetProvider m_provider;
        private readonly ResourceOperationBase<IReadOnlyList<T>> m_operation;
        private bool m_released;
        public bool IsReleased { get { return m_released || m_provider.Revoked; } }
        public bool IsDone
        {
            get
            {
                return m_operation.IsDone;
            }
        }

        public ProviderState State
        {
            get
            {
                return m_operation.IsCanceled ? ProviderState.Canceled : m_provider.State;
            }
        }

        public Exception Error
        {
            get
            {
                return m_operation.Exception?.GetBaseException();
            }
        }

        public ResourceOperationBase<IReadOnlyList<T>> Operation { get { Check(); return m_operation; } }
        public IReadOnlyList<T> Assets { get { Check(); return m_operation.Result; } }

        internal AssetCollectionHandle(ResourceManager owner, AssetProvider provider, CancellationToken token)
        { m_owner = owner; m_provider = provider; m_operation = WaitAsync(token); }

        private async ResourceOperationBase<IReadOnlyList<T>> WaitAsync(CancellationToken token)
        {
            try {
                await ResourceOperations.WaitAsync(m_provider.Operation, token);
                return m_provider.Revoked
                    ? throw new ObjectDisposedException(nameof(AssetCollectionHandle<T>))
                    : (IReadOnlyList<T>)Array.AsReadOnly(((Object[])m_provider.Operation.Result).Cast<T>().ToArray());
            }
            catch (OperationCanceledException) { Release(); throw; }
        }

        /// <summary>按对象名精确查询；同名不自动选第一个，避免不同资源被静默混淆。</summary>
        public T GetAsset(string name)
        {
            Check();
            if (name == null) {
                throw new ArgumentNullException(nameof(name));
            }

            T found = null;
            foreach (T asset in Assets) {
                if (!string.Equals(asset.name, name, StringComparison.Ordinal)) {
                    continue;
                }

                if (found != null) {
                    throw new System.Reflection.AmbiguousMatchException("资源集合中存在多个同名对象：" + name);
                }

                found = asset;
            }
            return found ?? throw new KeyNotFoundException("资源集合中没有对象：" + name);
        }
        private void Check()
        {
            m_owner.CheckThread();
            if (IsReleased) {
                throw new ObjectDisposedException(nameof(AssetCollectionHandle<T>));
            }
        }
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

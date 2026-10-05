using System.Collections.Generic;
using System.Threading;

namespace ZRAsset
{
    public sealed partial class ResourceScope
    {
        /// <summary>取得作用域拥有的资源；成功后由作用域保活，失败自动移除条目，调用方不再管理 Handle。</summary>
        public async ResourceOperationBase<T> LoadAsync<T>(string address, CancellationToken cancellationToken = default,
            int priority = 0) where T : UnityEngine.Object
        {
            CheckOpen();
            AssetHandle<T> handle = TrackAsync(token => m_manager.LoadAssetAsync<T>(address, token, priority), address, cancellationToken);
            try {
                T asset = await handle.Operation;
                CheckOpen();
                cancellationToken.ThrowIfCancellationRequested();
                return asset;
            }
            catch (System.Exception error) {
                try { if (m_owned.TryGetValue(handle, out LinkedListNode<Entry> node)) { await ReleaseEntry(node); } }
                catch (System.Exception cleanup) { throw ResourceFailure.WithCleanup(error, cleanup); }
                throw;
            }
        }
    }
}

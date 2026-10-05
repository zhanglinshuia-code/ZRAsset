using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace ZRAsset.Integrations
{
    public static class ResourceUniTaskExtensions
    {
        /// <summary>取消只停止此次等待。调用方仍需释放其拥有的 Handle。</summary>
        public static async UniTask<T> AsUniTask<T>(this ResourceOperationBase<T> operation, CancellationToken token = default)
        {
            if (operation == null) { throw new ArgumentNullException(nameof(operation)); }
            await UniTask.WaitUntil(operation, static value => value.IsDone, cancellationToken: token);
            return operation.Result;
        }

        public static async UniTask AsUniTask(this ResourceOperationBase operation, CancellationToken token = default)
        {
            if (operation == null) { throw new ArgumentNullException(nameof(operation)); }
            await UniTask.WaitUntil(operation, static value => value.IsDone, cancellationToken: token);
            operation.GetAwaiter().GetResult();
        }
    }
}

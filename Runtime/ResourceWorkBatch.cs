using System;
using System.Collections.Generic;
using System.Threading;

namespace ZRAsset
{
    /// <summary>有界工作者复用；尚未派发的条目不创建操作/取消注册，同步命中也定期让出主线程。</summary>
    internal static class ResourceWorkBatch
    {
        internal static ResourceOperationBase RunAsync<T>(IReadOnlyList<T> items, int concurrency,
            Func<T, CancellationToken, ResourceOperationBase> action, CancellationToken token)
        {
            return RunIndexedAsync(items.Count, concurrency, (index, cancellation) => action(items[index], cancellation), token);
        }

        internal static async ResourceOperationBase<TResult[]> MapAsync<T, TResult>(IReadOnlyList<T> items, int concurrency,
            Func<T, CancellationToken, ResourceOperationBase<TResult>> action, CancellationToken token)
        {
            var results = new TResult[items.Count];
            await RunIndexedAsync(items.Count, concurrency, StoreAsync, token);
            return results;
            async ResourceOperationBase StoreAsync(int index, CancellationToken cancellation)
            { results[index] = await action(items[index], cancellation); }
        }

        private static async ResourceOperationBase RunIndexedAsync(int count, int concurrency,
            Func<int, CancellationToken, ResourceOperationBase> action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cursor = 0;
            var workers = new ResourceOperationBase[Math.Min(count, Math.Max(1, concurrency))];
            for (var i = 0; i < workers.Length; i++) { workers[i] = WorkAsync(); }
            await ResourceOperationBase.WhenAll(workers);
            token.ThrowIfCancellationRequested();

            async ResourceOperationBase WorkAsync()
            {
                var synchronous = 0;
                try {
                    while (cursor < count) {
                        cancellation.Token.ThrowIfCancellationRequested();
                        ResourceOperationBase operation = action(cursor++, cancellation.Token);
                        var ready = operation.IsDone;
                        await operation;
                        if (ready && ++synchronous >= 8) {
                            synchronous = 0;
                            await ResourceOperationBase.Yield();
                        }
                    }
                }
                catch (Exception error) {
                    try { cancellation.Cancel(); }
                    catch (Exception cleanup) { throw ResourceFailure.WithCleanup(error, cleanup); }
                    throw;
                }
            }
        }
    }
}

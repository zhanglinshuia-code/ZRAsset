#if ZRASSET_WWISE_2024
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZRAsset.Integrations
{
    /// <summary>Wwise 2024 Unity 的 MemoryCopy 异步绑定。支持 RawFile/Archive/解密结果；流式媒体仍由宿主 I/O 管理。</summary>
    public sealed class WwiseSdkBank
    {
        private uint m_bankId;
        private bool m_loaded;
        private ResourceOperationBase m_disposal;

        public static async ResourceOperationBase<WwiseSdkBank> LoadAsync(ResourcePackage package, string address,
            int maximumBytes = 64 * 1024 * 1024, CancellationToken token = default)
        {
            if (package == null) { throw new ArgumentNullException(nameof(package)); }
            using RawFileHandle file = package.LoadRawFileAsync(address, token);
            await file.Operation;
            byte[] bytes = await file.ReadBytesAsync(maximumBytes, token);
            token.ThrowIfCancellationRequested();
            var bank = new WwiseSdkBank();
            var complete = new OperationCompletionSource<uint>();
            GCHandle pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try {
                AKRESULT started = AkUnitySoundEngine.LoadBankMemoryCopy(pinned.AddrOfPinnedObject(), (uint)bytes.Length,
                    (id, pointer, result, cookie) =>
                    {
                        if (result == AKRESULT.AK_Success) { complete.TrySetResult(id); }
                        else { complete.TrySetException(new InvalidOperationException("Wwise LoadBank: " + result)); }
                    }, null, out _);
                if (started != AKRESULT.AK_Success) { throw new InvalidOperationException("Wwise LoadBank 未能入队：" + started); }
                // 入队后等待 SDK 完成，取消不能提前释放原生正在读取的固定内存。
                bank.m_bankId = await complete.Operation;
                bank.m_loaded = true;
            }
            finally { pinned.Free(); }
            // 已经取得的 SDK 所有权交付给调用方；调用方随后 DisposeAsync，避免取消时丢失卸载失败的重试入口。
            return bank;
        }

        public ResourceOperationBase DisposeAsync()
        {
            if (m_disposal == null || m_disposal.IsFaulted) { m_disposal = UnloadAsync(); }
            return m_disposal;
        }
        private async ResourceOperationBase UnloadAsync()
        {
            if (!m_loaded) { return; }
            var complete = new OperationCompletionSource<bool>();
            AKRESULT started = AkUnitySoundEngine.UnloadBank(m_bankId, IntPtr.Zero,
                (id, pointer, result, cookie) =>
                {
                    if (result == AKRESULT.AK_Success) { complete.TrySetResult(true); }
                    else { complete.TrySetException(new InvalidOperationException("Wwise UnloadBank: " + result)); }
                }, null);
            if (started != AKRESULT.AK_Success) { throw new InvalidOperationException("Wwise UnloadBank 未能入队：" + started); }
            await complete.Operation;
            m_loaded = false;
        }
    }
}
#endif

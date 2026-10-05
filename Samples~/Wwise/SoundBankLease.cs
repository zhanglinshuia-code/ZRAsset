using System;
using System.Threading;

namespace ZRAsset.Integrations
{
    /// <summary>把独立 RawFile 的绝对路径交给项目 Wwise 加载桥接；卸载回调须在 Wwise 真正完成后返回。</summary>
    public sealed class SoundBankLease
    {
        private RawFileHandle m_file;
        private Func<ResourceOperationBase> m_unload;
        private ResourceOperationBase m_disposal;

        public static async ResourceOperationBase<SoundBankLease> LoadAsync(ResourcePackage package, string address,
            Func<string, ResourceOperationBase<Func<ResourceOperationBase>>> loadBank, CancellationToken token = default)
        {
            if (package == null) { throw new ArgumentNullException(nameof(package)); }
            if (loadBank == null) { throw new ArgumentNullException(nameof(loadBank)); }
            var lease = new SoundBankLease();
            try {
                lease.m_file = package.LoadRawFileAsync(address, token);
                await lease.m_file.Operation;
                token.ThrowIfCancellationRequested();
                // SDK 开始加载后必须等其返回所有权；不能在取消时提前释放文件。
                lease.m_unload = await loadBank(lease.m_file.GetRawFilePath());
                return lease.m_unload == null ? throw new InvalidOperationException("必须返回 SDK 卸载回调。") : lease;
            }
            catch { lease.m_file?.Release(); throw; }
        }

        public ResourceOperationBase DisposeAsync()
        {
            if (m_disposal == null || m_disposal.IsFaulted) { m_disposal = UnloadAsync(); }
            return m_disposal;
        }

        private async ResourceOperationBase UnloadAsync()
        {
            if (m_unload != null) { await m_unload(); }
            m_file?.Release();
            m_file = null;
            m_unload = null;
        }
    }
}

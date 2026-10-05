using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ZRAsset
{
    [Flags]
    public enum ResourceFileCapabilities { None = 0, SynchronousRead = 1, RandomAccess = 2, Persistent = 4 }

    /// <summary>已定位文件的不可变描述；不代表文件永远存在，也不代替缓存使用租约。</summary>
    public sealed class ResourceFileLocation
    {
        public string Location { get; }
        public ResourceLocationKind Kind { get; }
        public string LocalPath { get; }
        public ResourceFileCapabilities Capabilities { get; }
        public ResourceDownloadPolicy NetworkPolicy { get; }

        public ResourceFileLocation(string location, ResourceDownloadPolicy networkPolicy = null)
        {
            if (string.IsNullOrWhiteSpace(location)) {
                throw new ArgumentException("文件位置不能为空。", nameof(location));
            }

            Kind = ResourcePath.Classify(location);
            NetworkPolicy = networkPolicy;
            Location = Kind == ResourceLocationKind.LocalFile ? Path.GetFullPath(location) : ResourcePath.NormalizeUri(location);
            LocalPath = Kind == ResourceLocationKind.LocalFile ? Location :
                Kind == ResourceLocationKind.FileUri ? new Uri(Location).LocalPath : null;
            Capabilities = DescribeCapabilities(Kind);
            if (UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WebGLPlayer && LocalPath != null &&
                !LocalPath.StartsWith(Path.GetFullPath(UnityEngine.Application.persistentDataPath).TrimEnd('/') + "/", StringComparison.Ordinal)) {
                Capabilities &= ~ResourceFileCapabilities.Persistent;
            }
        }

        internal static ResourceFileCapabilities DescribeCapabilities(ResourceLocationKind kind)
        {
            if (kind != ResourceLocationKind.LocalFile && kind != ResourceLocationKind.FileUri) {
                return ResourceFileCapabilities.None;
            }
            // WebGL 仅在显式恢复持久化后端后声明持久化能力。
            return ResourceFileCapabilities.SynchronousRead | ResourceFileCapabilities.RandomAccess |
                (ResourcePlatform.Current.SupportsDiskDownloads ? ResourceFileCapabilities.Persistent : ResourceFileCapabilities.None);
        }
    }

    /// <summary>
    /// 只负责文件定位/准备，不创建 Unity 对象。每个实例由一个管理器独占；所有方法在创建线程（Unity 主线程）调用。
    /// DisposeAsync 停止新请求并排空在途操作；已取得的读取租约由持有者在 Unity 对象卸载后释放。
    /// </summary>
    public interface IResourceFileSystem
    {
        /// <summary>所有解析结果都保证具备的能力；单个结果可能提供更多能力。</summary>
        ResourceFileCapabilities FileCapabilities { get; }
        bool SupportsDownloads { get; }
        bool SupportsPersistentCache { get; }
        bool OwnsRetries { get; }
        ResourceOperationBase<ResourceFileLocation> ResolveAsync(BundleInfo info, DownloadPriority priority = DownloadPriority.Normal,
            CancellationToken cancellationToken = default);
        IDisposable AcquireReadLease();
        ResourceOperationBase DisposeAsync();
    }

    /// <summary>可选下载诊断接口；自定义文件系统不必依赖内置下载队列。</summary>
    public interface IResourceDownloadSource { BundleDownloadQueue DownloadQueue { get; } }

    /// <summary>资源后端可选择暴露文件服务；现有 Editor/业务 IResourceBackend 无需新增成员。</summary>
    public interface IResourceFileBackend { IResourceFileSystem FileSystem { get; } }

    /// <summary>提供快照、取消、排空和线程检查，供自定义文件系统复用。</summary>
    public abstract class ResourceFileSystem: IResourceFileSystem
    {
        private readonly int m_threadId = Thread.CurrentThread.ManagedThreadId;
        private readonly HashSet<ResourceOperationBase> m_pending = new();
        private readonly CancellationTokenSource m_lifetime = new();
        private bool m_closing;
        private ResourceOperationBase m_disposeOperation;
        public abstract ResourceFileCapabilities FileCapabilities { get; }
        public virtual bool SupportsDownloads
        {
            get
            {
                return false;
            }
        }

        public virtual bool SupportsPersistentCache
        {
            get
            {
                return false;
            }
        }

        public virtual bool OwnsRetries
        {
            get
            {
                return false;
            }
        }

        public ResourceOperationBase<ResourceFileLocation> ResolveAsync(BundleInfo info, DownloadPriority priority = DownloadPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            CheckAvailable();
            cancellationToken.ThrowIfCancellationRequested();
            BundleInfo snapshot = Snapshot(info);
            return RunTrackedAsync(token => ResolveCheckedAsync(snapshot, priority, token), cancellationToken);
        }

        private async ResourceOperationBase<ResourceFileLocation> ResolveCheckedAsync(BundleInfo info, DownloadPriority priority, CancellationToken token)
        {
            ResourceFileLocation file = await ResolveCoreAsync(info, priority, token) ?? throw new IOException("文件系统返回了空的文件位置。");
            return (file.Capabilities & FileCapabilities) != FileCapabilities ? throw new InvalidOperationException("文件系统返回的文件不具备声明的能力。") : file;
        }

        protected ResourceOperationBase<T> RunTrackedAsync<T>(Func<CancellationToken, ResourceOperationBase<T>> action, CancellationToken token,
            bool cancelAfterCompletion = true)
        {
            CheckAvailable();
            token.ThrowIfCancellationRequested();
            // 先登记占位操作，再进入可扩展后端；同步回调重入 Dispose 时也能看到在途读取。
            var completion = new OperationCompletionSource<T>();
            m_pending.Add(completion.Operation);
            _ = RunLinkedAsync(action, token, completion, cancelAfterCompletion);
            return completion.Operation;
        }

        private async ResourceOperationBase RunLinkedAsync<T>(Func<CancellationToken, ResourceOperationBase<T>> action, CancellationToken token,
            OperationCompletionSource<T> completion, bool cancelAfterCompletion)
        {
            try {
                T result;
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, m_lifetime.Token)) {
                    result = await action(linked.Token);
                    // 持有原生对象的 action 在内部决定取消与所有权交付的胜者，之后不能再丢弃其结果。
                    if (cancelAfterCompletion) {
                        linked.Token.ThrowIfCancellationRequested();
                    }
                }
                m_pending.Remove(completion.Operation);
                completion.SetResult(result);
            }
            catch (Exception error) {
                m_pending.Remove(completion.Operation);
                completion.SetException(error);
            }
        }

        protected abstract ResourceOperationBase<ResourceFileLocation> ResolveCoreAsync(BundleInfo info, DownloadPriority priority, CancellationToken token);
        protected virtual ResourceOperationBase ShutdownAsync()
        {
            return ResourceOperationBase.CompletedOperation;
        }

        protected virtual IDisposable AcquireReadLeaseCore()
        {
            return null;
        }

        public IDisposable AcquireReadLease() { CheckAvailable(); return AcquireReadLeaseCore(); }

        public ResourceOperationBase DisposeAsync()
        {
            CheckThread();
            if (m_disposeOperation != null) {
                return m_disposeOperation;
            }

            m_closing = true;
            var completion = new OperationCompletionSource<bool>();
            m_disposeOperation = completion.Operation;
            _ = CompleteCloseAsync(completion);
            return m_disposeOperation;
        }

        private async ResourceOperationBase CompleteCloseAsync(OperationCompletionSource<bool> completion)
        {
            try { await DrainAsync(); completion.SetResult(true); }
            catch (Exception error) { completion.SetException(error); }
        }

        private async ResourceOperationBase DrainAsync()
        {
            try {
                try { m_lifetime.Cancel(); }
                finally { await ShutdownAsync(); }
            }
            finally {
                try { await ResourceOperationBase.WhenAll(m_pending.ToArray()); }
                catch { /* 原操作保留失败信息，关闭仍须排空其他操作。 */ }
                m_lifetime.Dispose();
            }
        }

        protected void CheckAvailable()
        {
            CheckThread();
            if (m_closing) {
                throw new ObjectDisposedException(GetType().Name);
            }
        }
        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != m_threadId) {
                throw new InvalidOperationException("文件系统必须在 Unity 主线程使用。");
            }
        }

        internal static BundleInfo Snapshot(BundleInfo info)
        {
            if (info == null) {
                throw new ArgumentNullException(nameof(info));
            }

            if (!ResourceManifest.IsSafeBundleName(info.Name) || !DownloadStorage.IsSafeSegment(info.Name) || info.Size < 0) {
                throw new InvalidDataException("Bundle 名称或大小无效。");
            }

            if (!string.IsNullOrEmpty(info.Sha256) && !DownloadStorage.IsSha256(info.Sha256)) {
                throw new InvalidDataException("Bundle SHA-256 无效：" + info.Name);
            }

            if (!Enum.IsDefined(typeof(ResourceFileType), info.FileType)) {
                throw new NotSupportedException("不支持的文件格式。");
            }

            ResourceEncryption.ValidateMetadata(info);
            return new BundleInfo
            {
                Name = info.Name,
                Size = info.Size,
                Sha256 = info.Sha256,
                Hash = info.Hash,
                Crc = info.Crc,
                Encryption = info.Encryption,
                EncryptionKeyId = info.EncryptionKeyId,
                UnencryptedSize = info.UnencryptedSize,
                UnencryptedSha256 = info.UnencryptedSha256,
                FileType = info.FileType,
                Dependencies = info.Dependencies?.ToArray() ?? Array.Empty<string>()
            };
        }
    }
}

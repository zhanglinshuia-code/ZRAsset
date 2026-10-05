using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    public interface IResourceUnpackPolicy { bool ShouldUnpack(BundleInfo info, IReadOnlyList<string> tags); }
    /// <summary>URI 首包落盘策略。默认只落盘 RawFile、Archive 和加密文件；AllFiles 也准备普通 Bundle。</summary>
    public sealed class ResourceUnpackOptions
    {
        public string CacheRoot { get; }
        public bool AllFiles { get; }
        public int MaxConcurrentUnpacks { get; }
        public int RequestTimeoutSeconds { get; }
        public ResourceDiskPolicy DiskPolicy { get; }
        public IResourceUnpackPolicy Policy { get; }
        public ResourceDownloadPolicy NetworkPolicy { get; }

        public ResourceUnpackOptions(string cacheRoot = null, bool allFiles = false, int maxConcurrentUnpacks = 3,
            int requestTimeoutSeconds = 60, ResourceDiskPolicy diskPolicy = null, IResourceUnpackPolicy policy = null,
            ResourceDownloadPolicy networkPolicy = null)
        {
            ResourcePlatform.RequireDiskDownloads("首包文件落盘");
            if (maxConcurrentUnpacks < 1 || maxConcurrentUnpacks > 32) {
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentUnpacks));
            }

            if (requestTimeoutSeconds < 1 || requestTimeoutSeconds > 600) {
                throw new ArgumentOutOfRangeException(nameof(requestTimeoutSeconds));
            }

            CacheRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(cacheRoot) ? Path.Combine(Application.persistentDataPath, "ZRAssetUnpacked") : cacheRoot);
            AllFiles = allFiles; MaxConcurrentUnpacks = maxConcurrentUnpacks; RequestTimeoutSeconds = requestTimeoutSeconds;
            DiskPolicy = diskPolicy ?? new ResourceDiskPolicy();
            Policy = policy;
            NetworkPolicy = networkPolicy;
            ResourcePersistence.RegisterCacheRoot(CacheRoot);
        }
    }

    /// <summary>主线程协调的首包提取器；共享写入、固定缓冲传输、校验后发布，沿用缓存维护租约。</summary>
    internal sealed class ResourceUnpacker
    {
        private sealed class Job
        {
            internal readonly CancellationTokenSource Cancellation = new();
            internal readonly OperationCompletionSource<ResourceFileLocation> Completion = new();
            internal int Waiters;
            internal long Size;
        }
        private readonly ResourceUnpackOptions m_options;
        private readonly string m_root, m_target;
        private readonly IDownloadTransport m_transport;
        private readonly OperationSemaphore m_slots;
        private readonly Dictionary<string, Job> m_jobs = new(DownloadStorage.PathComparer);
        private CacheUsageLease m_usage;

        internal ResourceUnpacker(string root, string target, ResourceUnpackOptions options, IDownloadTransport transport)
        {
            m_root = root; m_target = DownloadStorage.IsSafeSegment(target) ? target : "legacy";
            m_options = options; m_transport = transport ?? new UnityWebRequestDownloadTransport();
            m_slots = new OperationSemaphore(options.MaxConcurrentUnpacks, options.MaxConcurrentUnpacks);
        }

        internal IDisposable AcquireReadLease()
        {
            return CacheUsageLease.Acquire(m_options.CacheRoot);
        }

        private void KeepUsage()
        {
            m_usage ??= CacheUsageLease.Acquire(m_options.CacheRoot);
        }

        private string GetPath(BundleInfo info)
        {
            return !DownloadStorage.IsSha256(info.Sha256)
                ? throw new InvalidDataException("首包落盘要求清单提供 SHA-256：" + info.Name)
                : DownloadStorage.ValidatePath(m_options.CacheRoot,
                Path.Combine(m_options.CacheRoot, "unpack", m_target, info.Sha256.ToLowerInvariant(), info.Name));
        }

        internal async ResourceOperationBase<ResourceFileLocation> TryResolveAsync(BundleInfo info, CancellationToken token)
        {
            if (!DownloadStorage.IsSha256(info.Sha256)) {
                return null;
            }

            KeepUsage();
            using IDisposable reading = AcquireReadLease();
            var path = GetPath(info);
            return await IsVerifiedAsync(path, info, token) ? new ResourceFileLocation(path) : null;
        }

        internal ResourceFileLocation Resolve(BundleInfo info)
        {
            KeepUsage();
            var file = new ResourceFileLocation(GetPath(info));
            BuiltInResourceFileSystem.ValidateLocalFile(file, info);
            // 旧版 AssetBundle 的 Size=0 可表示未知；落盘协议必须使用精确长度。
            return new FileInfo(file.LocalPath).Length != info.Size ? throw new InvalidDataException("首包落盘文件长度不匹配。") : file;
        }

        internal async ResourceOperationBase<ResourceFileLocation> ResolveAsync(BundleInfo info, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            KeepUsage();
            var path = GetPath(info);
            if (m_jobs.TryGetValue(path, out Job job)) {
                if (job.Size != info.Size) {
                    throw new InvalidDataException("同一落盘目标的长度不一致。");
                }

                if (job.Cancellation.IsCancellationRequested) {
                    try { await ResourceOperations.WaitAsync(job.Completion.Operation, token); }
                    catch (Exception) when (!token.IsCancellationRequested) { }
                    return await ResolveAsync(info, token);
                }
                job.Waiters++;
            }
            else {
                job = new Job { Waiters = 1, Size = info.Size };
                m_jobs.Add(path, job);
                _ = RunAsync(path, info, job);
            }
            try {
                await ResourceOperations.WaitAsync(job.Completion.Operation, token);
                return await job.Completion.Operation;
            }
            finally {
                if (--job.Waiters == 0 && !job.Completion.Operation.IsDone) {
                    job.Cancellation.Cancel();
                }
            }
        }

        private async ResourceOperationBase RunAsync(string path, BundleInfo info, Job job)
        {
            ResourceFileLocation result = null; Exception failure = null;
            string temporary = null; var entered = false;
            try {
                CancellationToken token = job.Cancellation.Token;
                // 同目标跨实例也只允许一个写入者；取得锁后必须重新校验。
                using ResourceFileLock held = await CacheDestinationLock.AcquireAsync(m_options.CacheRoot, path, token);
                if (!await IsVerifiedAsync(path, info, token)) {
                    await m_slots.WaitAsync(token); entered = true;
                    DownloadStorage.ValidatePath(m_options.CacheRoot, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    temporary = DownloadStorage.ValidatePath(m_options.CacheRoot, path + ".reuse." + Guid.NewGuid().ToString("N") + ".tmp");
                    using IDisposable reservation = m_options.DiskPolicy.Reserve(m_options.CacheRoot, info.Size, (temporary, 0));
                    CacheOwnership.Register(m_options.CacheRoot, path, info);
                    var request = new DownloadTransportRequest(ResourcePath.Combine(m_root, info.Name), temporary, 0, null,
                        m_options.RequestTimeoutSeconds, info.Size, networkPolicy: m_options.NetworkPolicy);
                    DownloadTransportResponse response = await m_transport.SendAsync(request, null, token);
                    token.ThrowIfCancellationRequested();
                    if (response == null) {
                        throw new IOException("首包传输没有返回结果。");
                    }

                    if (response.Cancelled) {
                        throw new OperationCanceledException(token);
                    }

                    if (response.ExceededLimit) {
                        throw new InvalidDataException("首包传输超过清单长度：" + info.Name);
                    }

                    var http = ResourcePath.Classify(m_root) == ResourceLocationKind.HttpUri;
                    if (!string.IsNullOrEmpty(response.Error) || (http ? response.StatusCode != 200 : response.StatusCode != 0 && response.StatusCode != 200)) {
                        throw new IOException("首包传输失败：" + info.Name + " " + response.StatusCode + " " + response.Error);
                    }

                    if (!await IsVerifiedAsync(temporary, info, token)) {
                        throw new InvalidDataException("首包文件长度或 SHA-256 校验失败：" + info.Name);
                    }

                    token.ThrowIfCancellationRequested();
                    DownloadStorage.ValidatePath(m_options.CacheRoot, path);
                    // 不删除旧文件，直到完整替代文件已经通过校验。
                    if (File.Exists(path)) {
                        DownloadStorage.DeleteFile(path);
                    }

                    File.Move(temporary, path);
                    CacheContentIndex.Published(m_options.CacheRoot, path, info);
                    await ResourcePersistence.FlushAsync();
                }
                token.ThrowIfCancellationRequested();
                result = new ResourceFileLocation(path);
            }
            catch (Exception error) { failure = error; }
            finally {
                try {
                    if (temporary != null) {
                        DownloadStorage.DeleteFile(temporary);
                    }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { /* 遗留隔离文件由所有权标记及缓存维护回收，不作为正式文件读取。 */ }
                if (entered) {
                    m_slots.Release();
                }

                m_jobs.Remove(path);
                job.Cancellation.Dispose();
            }
            if (failure == null) {
                job.Completion.SetResult(result);
            }
            else {
                job.Completion.SetException(failure);
            }
        }

        private static async ResourceOperationBase<bool> IsVerifiedAsync(string path, BundleInfo info, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            DownloadStorage.RejectLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length != info.Size) {
                return false;
            }

            var hash = await ResourceFileIO.Shared.ComputeSha256Async(path, token);
            return string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        internal async ResourceOperationBase DisposeAsync()
        {
            Job[] current = m_jobs.Values.ToArray();
            foreach (Job job in current) {
                job.Cancellation.Cancel();
            }

            try { await ResourceOperationBase.WhenAll(current.Select(job => job.Completion.Operation)); }
            catch { /* 消费者保留原始错误，关闭只负责排空。 */ }
            finally { m_usage?.Dispose(); m_usage = null; }
        }
    }
}

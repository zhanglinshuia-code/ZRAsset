using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace ZRAsset
{
    /// <summary>文件任务的并发和内存上限。构造服务后会复制这些选项，后续修改不会改变正在运行的任务。</summary>
    public sealed class ResourceFileIOOptions
    {
        public int MaxConcurrentOperations = 2;
        public int BufferSizeBytes = 256 * 1024;
        public double CooperativeSliceMilliseconds = 2;
        public bool PreferBackgroundThreads = true;
    }

    /// <summary>线程安全的累计诊断快照；字节数包括已取消任务实际处理过的部分。</summary>
    public readonly struct ResourceFileIODiagnostics
    {
        public readonly int ActiveOperations, QueuedOperations, PeakConcurrentOperations;
        public readonly long CompletedOperations, CanceledOperations, FailedOperations, BytesProcessed, CooperativeYields;
        public readonly bool UsesBackgroundThreads;

        internal ResourceFileIODiagnostics(int active, int queued, int peak, long completed, long canceled,
            long failed, long bytes, long yields, bool background)
        {
            ActiveOperations = active; QueuedOperations = queued; PeakConcurrentOperations = peak;
            CompletedOperations = completed; CanceledOperations = canceled; FailedOperations = failed;
            BytesProcessed = bytes; CooperativeYields = yields; UsesBackgroundThreads = background;
        }
    }

    /// <summary>
    /// 有界文件处理服务：普通平台在后台执行哈希和流复制，WebGL Player 使用主线程时间片。
    /// 后台逻辑仅使用 System API；队列等待不会占用工作线程，活动任务的缓冲区大小固定，不随文件增长。
    /// </summary>
    public sealed class ResourceFileIO
    {
        public static ResourceFileIO Shared { get; } = new ResourceFileIO();

        private readonly OperationSemaphore m_slots;
        private readonly object m_diagnosticsLock = new object();
        private readonly int m_bufferSize;
        private readonly double m_sliceMilliseconds;
        private const int CooperativeChunkBytes = 16 * 1024;
        private int m_active, m_queued, m_peak;
        private long m_completed, m_canceled, m_failed, m_bytes, m_yields;

        public int MaxConcurrentOperations { get; }
        public bool UsesBackgroundThreads { get; }

        internal static string HashRange(Stream stream, long offset, long count, CancellationToken token = default)
        {
            using var sha = SHA256.Create(); stream.Position = offset;
            var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(64 * 1024, Math.Max(1, count)));
            try {
                while (count > 0) {
                    token.ThrowIfCancellationRequested(); var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                    if (read == 0) { throw new EndOfStreamException(); }
                    sha.TransformBlock(buffer, 0, read, null, 0); count -= read;
                }
                token.ThrowIfCancellationRequested();
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, true); }
        }
        internal ResourceOperationBase<string> HashRangeAsync(Stream stream, long offset, long count, CancellationToken token = default)
        {
            return RunAsync(() => HashRange(stream, offset, count, token), () => HashRangeCooperative(stream, offset, count, token), token);
        }

        internal ResourceOperationBase<bool> WriteAtomicAsync(string path, string contents)
        {
            return RunAsync(() => { ResourceVersionManager.WriteAtomic(path, contents); return true; },
                () => WriteAtomicCooperative(path, contents), default);
        }

        private static async ResourceOperationBase<bool> WriteAtomicCooperative(string path, string contents)
        {
            await ResourceOperationBase.Yield();
            ResourceVersionManager.WriteAtomic(path, contents);
            return true;
        }

        internal ResourceOperationBase<byte[]> ReadRawBytesAsync(Stream stream, int maximumBytes, CancellationToken token)
        {
            return RunAsync(() => RawFileHandle.ReadAll(stream, maximumBytes, token), () => CooperateRead(stream, maximumBytes, token), token);
        }

        private async ResourceOperationBase<byte[]> CooperateRead(Stream stream, int maximumBytes, CancellationToken token)
        {
            if (maximumBytes < 0) {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            if (stream.Length > maximumBytes) {
                throw new IOException("Raw file exceeds the requested memory limit.");
            }

            token.ThrowIfCancellationRequested();
            // 给调用者一次取消机会；之后按时间片推进，避免快速流在首次调用中读完整个文件。
            if (stream.Length > 0) {
                await ResourceOperationBase.Yield();
            }

            token.ThrowIfCancellationRequested();
            var bytes = new byte[(int)stream.Length]; var offset = 0;
            var slice = Stopwatch.StartNew();
            while (offset < bytes.Length) {
                token.ThrowIfCancellationRequested(); var count = stream.Read(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset));
                if (count == 0) {
                    throw new EndOfStreamException();
                }

                offset += count;
                if (offset < bytes.Length && slice.Elapsed.TotalMilliseconds >= m_sliceMilliseconds) { await ResourceOperationBase.Yield(); slice.Restart(); }
            }
            token.ThrowIfCancellationRequested(); return bytes;
        }

        private async ResourceOperationBase<string> HashRangeCooperative(Stream stream, long offset, long count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (count > 0) { await ResourceOperationBase.Yield(); }
            using var sha = SHA256.Create(); stream.Position = offset;
            var chunkSize = GetBufferSize(count, true);
            var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
            var slice = Stopwatch.StartNew();
            try {
                while (count > 0) {
                    token.ThrowIfCancellationRequested(); var read = stream.Read(buffer, 0, (int)Math.Min(chunkSize, count));
                    if (read == 0) { throw new EndOfStreamException(); }
                    sha.TransformBlock(buffer, 0, read, null, 0); count -= read;
                    Interlocked.Add(ref m_bytes, read);
                    if (count > 0 && slice.Elapsed.TotalMilliseconds >= m_sliceMilliseconds) {
                        Interlocked.Increment(ref m_yields);
                        await ResourceOperationBase.Yield(); slice.Restart();
                    }
                }
                token.ThrowIfCancellationRequested();
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, true); }
        }

        public ResourceFileIO(ResourceFileIOOptions options = null)
        {
            options ??= new ResourceFileIOOptions();
            if (options.MaxConcurrentOperations < 1 || options.MaxConcurrentOperations > 32) {
                throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentOperations), "文件并发数必须介于 1 与 32。 ");
            }

            if (options.BufferSizeBytes < 4096 || options.BufferSizeBytes > 4 * 1024 * 1024) {
                throw new ArgumentOutOfRangeException(nameof(options.BufferSizeBytes), "文件缓冲区必须介于 4 KiB 与 4 MiB。 ");
            }

            if (double.IsNaN(options.CooperativeSliceMilliseconds) || options.CooperativeSliceMilliseconds <= 0 || options.CooperativeSliceMilliseconds > 50) {
                throw new ArgumentOutOfRangeException(nameof(options.CooperativeSliceMilliseconds), "协作时间片必须大于零且不超过 50 ms。 ");
            }

            MaxConcurrentOperations = options.MaxConcurrentOperations;
            m_bufferSize = options.BufferSizeBytes;
            m_sliceMilliseconds = options.CooperativeSliceMilliseconds;
#if UNITY_WEBGL && !UNITY_EDITOR
            // Unity WebGL Player 不依赖线程池；即使选项要求后台处理也必须走兼容路径。
            UsesBackgroundThreads = false;
#else
            UsesBackgroundThreads = options.PreferBackgroundThreads;
#endif
            m_slots = new OperationSemaphore(MaxConcurrentOperations, MaxConcurrentOperations);
        }

        public ResourceFileIODiagnostics GetDiagnostics()
        {
            lock (m_diagnosticsLock) {
                return new ResourceFileIODiagnostics(m_active, m_queued, m_peak, m_completed, m_canceled, m_failed,
                    Interlocked.Read(ref m_bytes), Interlocked.Read(ref m_yields), UsesBackgroundThreads);
            }
        }

        /// <summary>返回小写十六进制 SHA-256。取消或异常时关闭文件，释放并发名额。</summary>
        public ResourceOperationBase<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
        {
            var fullPath = Path.GetFullPath(path);
            return RunAsync(() => HashAsync(fullPath, false, cancellationToken).GetAwaiter().GetResult(),
                () => HashAsync(fullPath, true, cancellationToken), cancellationToken);
        }

        /// <summary>按固定字符块读取清单，取消时关闭文件，WebGL 走协作时间片。</summary>
        public ResourceOperationBase<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
        {
            var fullPath = Path.GetFullPath(path);
            return RunAsync(() => ReadTextCoreAsync(fullPath, false, cancellationToken).GetAwaiter().GetResult(),
                () => ReadTextCoreAsync(fullPath, true, cancellationToken), cancellationToken);
        }

        private async ResourceOperationBase<string> ReadTextCoreAsync(string path, bool cooperative, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var reader = new StreamReader(path)) {
                var result = new System.Text.StringBuilder();
                var buffer = new char[4096];
                var timer = Stopwatch.StartNew();
                int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0) {
                    token.ThrowIfCancellationRequested();
                    result.Append(buffer, 0, count);
                    if (cooperative && timer.Elapsed.TotalMilliseconds >= m_sliceMilliseconds) { await ResourceOperationBase.Yield(); timer.Restart(); }
                }
                token.ThrowIfCancellationRequested();
                return result.ToString();
            }
        }

        /// <summary>复制到不存在的文件。失败或取消时删除本次新建的目标；现有目标不会被覆盖。</summary>
        public ResourceOperationBase CopyAsync(string source, string destination, CancellationToken cancellationToken = default)
        {
            return TransferAsync(source, destination, false, cancellationToken);
        }

        /// <summary>在同一次流复制中校验实际写入的字节，避免再读取整个临时副本。</summary>
        public ResourceOperationBase<string> CopyAndHashAsync(string source, string destination, CancellationToken cancellationToken = default,
            long maximumBytes = long.MaxValue)
        {
            string input = Path.GetFullPath(source), output = Path.GetFullPath(destination);
            return DownloadStorage.PathComparer.Equals(input, output)
                ? throw new ArgumentException("源文件与目标文件不能相同。")
                : maximumBytes < 0
                ? throw new ArgumentOutOfRangeException(nameof(maximumBytes))
                : RunAsync(() => TransferCoreAsync(input, output, false, false, cancellationToken, true, maximumBytes).GetAwaiter().GetResult(),
                () => TransferCoreAsync(input, output, false, true, cancellationToken, true, maximumBytes), cancellationToken);
        }

        /// <summary>追加完整文件。失败或取消时恢复追加前长度，保留原有连续前缀。</summary>
        public ResourceOperationBase AppendAsync(string source, string destination, CancellationToken cancellationToken = default)
        {
            return TransferAsync(source, destination, true, cancellationToken);
        }

        private ResourceOperationBase TransferAsync(string source, string destination, bool append, CancellationToken token)
        {
            string input = Path.GetFullPath(source), output = Path.GetFullPath(destination);
            return DownloadStorage.PathComparer.Equals(input, output)
                ? throw new ArgumentException("源文件与目标文件不能相同。", nameof(destination))
                : (ResourceOperationBase)RunAsync(() => TransferCoreAsync(input, output, append, false, token).GetAwaiter().GetResult(),
                () => TransferCoreAsync(input, output, append, true, token), token);
        }

        private async ResourceOperationBase<T> RunAsync<T>(Func<T> background, Func<ResourceOperationBase<T>> cooperative, CancellationToken token)
        {
            bool entered = false, waiting = true;
            lock (m_diagnosticsLock) {
                m_queued++;
            }

            try {
                await m_slots.WaitAsync(token);
                entered = true;
                lock (m_diagnosticsLock) {
                    m_queued--; waiting = false; m_active++; m_peak = Math.Max(m_peak, m_active);
                }
                token.ThrowIfCancellationRequested();
                T result;
#if UNITY_WEBGL && !UNITY_EDITOR
                result = await cooperative();
#else
                // 只在拿到名额后排入线程池；不会创建一批阻塞在 Semaphore 上的线程。
                result = UsesBackgroundThreads ? await ResourceOperationBase.Run(background) : await cooperative();
#endif
                lock (m_diagnosticsLock) {
                    m_completed++;
                }

                return result;
            }
            catch (OperationCanceledException) {
                lock (m_diagnosticsLock) {
                    m_canceled++;
                }

                throw;
            }
            catch {
                lock (m_diagnosticsLock) {
                    m_failed++;
                }

                throw;
            }
            finally {
                lock (m_diagnosticsLock) {
                    if (waiting) {
                        m_queued--;
                    }

                    if (entered) {
                        m_active--;
                    }
                }
                if (entered) {
                    m_slots.Release();
                }
            }
        }

        private async ResourceOperationBase<string> HashAsync(string path, bool cooperative, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            DownloadStorage.RejectLinks(path);
            using (var sha = SHA256.Create())
            using (FileStream file = OpenInput(path)) {
                var chunkSize = GetBufferSize(file.Length, cooperative);
                var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
                var slice = Stopwatch.StartNew();
                try {
                    int count;
                    while ((count = file.Read(buffer, 0, chunkSize)) > 0) {
                        token.ThrowIfCancellationRequested();
                        sha.TransformBlock(buffer, 0, count, null, 0);
                        Interlocked.Add(ref m_bytes, count);
                        if (cooperative && slice.Elapsed.TotalMilliseconds >= m_sliceMilliseconds) {
                            Interlocked.Increment(ref m_yields);
                            await ResourceOperationBase.Yield();
                            slice.Restart();
                        }
                        token.ThrowIfCancellationRequested();
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    token.ThrowIfCancellationRequested();
                    return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, true); }
            }
        }

        private async ResourceOperationBase<string> TransferCoreAsync(string source, string destination, bool append, bool cooperative, CancellationToken token,
            bool hash = false, long maximumBytes = long.MaxValue)
        {
            token.ThrowIfCancellationRequested();
            DownloadStorage.RejectLinks(source);
            DownloadStorage.RejectLinks(destination);
            bool created = false, success = false;
            using SHA256 sha = hash ? SHA256.Create() : null;
            try {
                using (FileStream input = OpenInput(source))
                using (var output = new FileStream(destination, append ? FileMode.OpenOrCreate : FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan)) {
                    var originalLength = output.Length;
                    created = !append || originalLength == 0;
                    output.Position = originalLength;
                    byte[] buffer = null;
                    try {
                        var chunkSize = GetBufferSize(input.Length, cooperative);
                        buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
                        var slice = Stopwatch.StartNew();
                        int count;
                        while ((count = input.Read(buffer, 0, chunkSize)) > 0) {
                            token.ThrowIfCancellationRequested();
                            if (count > maximumBytes - (output.Position - originalLength)) {
                                throw new InvalidDataException("复制源超过预留的字节上限。");
                            }

                            output.Write(buffer, 0, count);
                            sha?.TransformBlock(buffer, 0, count, null, 0);
                            Interlocked.Add(ref m_bytes, count);
                            if (cooperative && slice.Elapsed.TotalMilliseconds >= m_sliceMilliseconds) {
                                Interlocked.Increment(ref m_yields);
                                await ResourceOperationBase.Yield();
                                slice.Restart();
                            }
                            token.ThrowIfCancellationRequested();
                        }
                        output.Flush(true);
                        token.ThrowIfCancellationRequested();
                    }
                    catch (Exception error) {
                        if (append) {
                            // 合并尚未完成时不留下半次追加，下载队列仍可从旧断点重试。
                            try { output.SetLength(originalLength); output.Flush(); }
                            catch (Exception rollbackError) {
                                throw new IOException("追加失败，且原始文件长度未能恢复。", new AggregateException(error, rollbackError));
                            }
                        }
                        throw;
                    }
                    finally { if (buffer != null) { ArrayPool<byte>.Shared.Return(buffer, true); } }
                }
                success = true;
            }
            finally {
                if (!success && created && !append) {
                    DownloadStorage.DeleteFile(destination);
                }
            }
            if (sha == null) {
                return null;
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }

        private FileStream OpenInput(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.SequentialScan);
        }

        private int GetBufferSize(long length, bool cooperative)
        {
            // 计算片段与配置的最大缓冲分离；大缓冲不能使一次 SHA 计算占满多个时间片。
            var maximum = cooperative ? Math.Min(m_bufferSize, CooperativeChunkBytes) : m_bufferSize;
            return (int)Math.Min(maximum, Math.Max(1, length));
        }
    }
}

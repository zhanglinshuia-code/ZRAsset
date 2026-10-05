using System;
using System.IO;
using System.Text;
using System.Threading;

namespace ZRAsset
{
    /// <summary>原始文件/归档条目的使用凭证。打开的流单独持有引用，释放 Handle 不关闭已打开的流。</summary>
    public sealed class RawFileHandle: IDisposable
    {
        public const int DefaultMaximumReadBytes = 32 * 1024 * 1024;
        private readonly ResourceManager m_owner;
        private readonly RawFileProvider m_provider;
        private readonly ResourceOperationBase m_operation;
        private bool m_released;
        public bool IsReleased { get { return m_released || m_provider.Revoked; } }
        public bool IsDone
        {
            get
            {
                return m_operation.IsDone;
            }
        }

        public Exception Error
        {
            get
            {
                return m_operation.Error;
            }
        }

        public ResourceOperationBase Operation { get { Check(); return m_operation; } }
        public long Length
        {
            get
            {
                return Source.Location.Length;
            }
        }

        public long FileOffset
        {
            get
            {
                return Source.Location.Offset;
            }
        }

        public string ContainerPath
        {
            get
            {
                return Source.Location.File.LocalPath;
            }
        }

        public ResourceFileType FileType
        {
            get
            {
                return m_provider.Asset.FileType;
            }
        }

        public bool IsEncrypted
        {
            get
            {
                return Source.Location.IsEncrypted;
            }
        }

        private RawFileSource Source { get { Check(); m_operation.GetAwaiter().GetResult(); return m_provider.Operation.Result; } }
        internal RawFileHandle(ResourceManager owner, RawFileProvider provider, CancellationToken token)
        { m_owner = owner; m_provider = provider; m_operation = WaitAsync(token); }
        private async ResourceOperationBase WaitAsync(CancellationToken token)
        {
            try {
                await ResourceOperations.WaitAsync(m_provider.Operation, token);
                if (m_provider.Revoked) {
                    throw new ObjectDisposedException(nameof(RawFileHandle));
                }
            }
            catch (OperationCanceledException) { Release(); throw; }
        }
        public string GetRawFilePath()
        {
            RawFileLocation location = Source.Location;
            return FileType != ResourceFileType.RawFile || location.File.LocalPath == null || location.IsEncrypted
                ? throw new NotSupportedException("该条目没有独立本地文件路径，请使用 OpenRead/ReadBytes。")
                : location.File.LocalPath;
        }
        public Stream OpenRead()
        {
            RawFileSource source = Source; m_provider.References++;
            return new RawReadStream(source, () =>
            {
                if (OperationSystem.IsMainThread) {
                    m_owner.ReleaseRaw(m_provider);
                }
                else {
                    OperationSystem.Post(() => m_owner.ReleaseRaw(m_provider));
                }
            });
        }
        public byte[] ReadBytes(int maximumBytes = DefaultMaximumReadBytes)
        { using Stream stream = OpenRead(); return ReadAll(stream, maximumBytes, default); }
        public string ReadText(int maximumBytes = DefaultMaximumReadBytes)
        {
            return Decode(ReadBytes(maximumBytes));
        }

        public async ResourceOperationBase<byte[]> ReadBytesAsync(int maximumBytes = DefaultMaximumReadBytes, CancellationToken cancellationToken = default)
        {
            Check(); cancellationToken.ThrowIfCancellationRequested();
            using Stream stream = OpenRead();
            return await ResourceFileIO.Shared.ReadRawBytesAsync(stream, maximumBytes, cancellationToken);
        }
        public async ResourceOperationBase<string> ReadTextAsync(int maximumBytes = DefaultMaximumReadBytes, CancellationToken cancellationToken = default)
        {
            return Decode(await ReadBytesAsync(maximumBytes, cancellationToken));
        }

        private static string Decode(byte[] bytes)
        {
            var start = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
        }
        internal static byte[] ReadAll(Stream stream, int maximumBytes, CancellationToken token)
        {
            if (maximumBytes < 0) {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            if (stream.Length > maximumBytes) {
                throw new IOException("Raw file exceeds the requested memory limit.");
            }

            token.ThrowIfCancellationRequested(); var bytes = new byte[(int)stream.Length]; var offset = 0;
            while (offset < bytes.Length) {
                token.ThrowIfCancellationRequested(); var read = stream.Read(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset));
                if (read == 0) {
                    throw new EndOfStreamException();
                }

                offset += read;
            }
            return bytes;
        }
        private void Check()
        {
            m_owner.CheckThread(); if (IsReleased) {
                throw new ObjectDisposedException(nameof(RawFileHandle));
            }
        }
        public void Release()
        { m_owner.CheckThread(); if (IsReleased) { return; } m_released = true; m_owner.ReleaseRaw(m_provider); }
        public void Dispose()
        {
            Release();
        }
    }

    internal sealed class RawReadStream: Stream
    {
        private readonly RawFileSource m_source;
        private Action m_release;
        private long m_position;
        internal RawReadStream(RawFileSource source, Action release) { m_source = source; m_release = release; }
        private void Check()
        {
            if (m_release == null || m_source.IsDisposed) {
                throw new ObjectDisposedException(nameof(RawReadStream));
            }
        }
        public override bool CanRead
        {
            get
            {
                return m_release != null && !m_source.IsDisposed;
            }
        }

        public override bool CanSeek
        {
            get
            {
                return m_release != null && !m_source.IsDisposed;
            }
        }

        public override bool CanWrite
        {
            get
            {
                return false;
            }
        }

        public override long Length { get { Check(); return m_source.Location.Length; } }
        public override long Position { get { Check(); return m_position; } set { Seek(value, SeekOrigin.Begin); } }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Check();
            if (buffer == null) {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || count < 0 || offset > buffer.Length - count) {
                throw new ArgumentOutOfRangeException();
            }

            var read = m_source.ReadAt(m_position, buffer, offset, (int)Math.Min(count, Length - m_position)); m_position += read; return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Check();
            var value = checked(origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => m_position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            });
            return value < 0 || value > Length ? throw new IOException("Seek outside raw file entry.") : (m_position = value);
        }
        public override void Flush() { Check(); }
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        { if (disposing) { Interlocked.Exchange(ref m_release, null)?.Invoke(); } base.Dispose(disposing); }
    }
}

using System;
using System.Collections.Generic;
using System.IO;

namespace ZRAsset
{
    // A manager owns this cache. Verification is reused only while the verified stream is held open.
    internal sealed class RawFileContainerCache
    {
        private sealed class Entry
        {
            internal int References;
            internal readonly OperationCompletionSource<Stream> Completion = new();
            internal readonly object Gate = new();
        }

        private readonly Dictionary<(string, string, long, bool, ResourceFileType), Entry> m_entries = new();

        internal Stream Open(RawFileLocation location)
        {
            if (location.File.LocalPath == null) {
                throw new NotSupportedException("同步原始文件读取要求本地文件，请先异步准备。");
            }

            (string, string, long, bool, ResourceFileType) key = Key(location);
            if (!m_entries.TryGetValue(key, out Entry entry)) {
                entry = new Entry();
                m_entries.Add(key, entry);
                try {
                    Stream stream = CreateLocal(location);
                    try { RawFileSource.CheckLayout(location, stream); RawFileSource.CheckHash(ResourceFileIO.HashRange(stream, 0, location.ContainerSize), location.ContainerSha256); }
                    catch { stream.Dispose(); throw; }
                    entry.Completion.SetResult(stream);
                }
                catch { m_entries.Remove(key); throw; }
            }
            ResourceOperationBase<Stream> operation = entry.Completion.Operation;
            if (!operation.IsDone) {
                throw new InvalidOperationException("归档正在异步校验，不能同步等待。");
            }

            Stream value = operation.GetAwaiter().GetResult();
            entry.References++;
            return new Lease(value, entry.Gate, () => Release(key, entry));
        }

        internal async ResourceOperationBase<Stream> OpenAsync(RawFileLocation location)
        {
            (string, string, long, bool, ResourceFileType) key = Key(location);
            if (!m_entries.TryGetValue(key, out Entry entry)) {
                entry = new Entry();
                m_entries.Add(key, entry);
                _ = CompleteAsync(location, entry);
            }
            entry.References++;
            try {
                Stream value = await entry.Completion.Operation;
                return new Lease(value, entry.Gate, () => Release(key, entry));
            }
            catch { Release(key, entry); throw; }
        }

        private static async ResourceOperationBase CompleteAsync(RawFileLocation location, Entry entry)
        {
            Stream stream = null;
            try {
                stream = location.OpenDecodedStream != null || location.File.LocalPath != null ? CreateLocal(location) :
                    new MemoryStream(await RawFileSource.ReadUriAsync(location), false);
                RawFileSource.CheckLayout(location, stream);
                RawFileSource.CheckHash(await ResourceFileIO.Shared.HashRangeAsync(stream, 0, location.ContainerSize), location.ContainerSha256);
                entry.Completion.SetResult(stream);
            }
            catch (Exception error) {
                try { stream?.Dispose(); }
                catch (Exception cleanup) { error = new AggregateException(error, cleanup); }
                entry.Completion.SetException(error);
            }
        }

        private static Stream CreateLocal(RawFileLocation location)
        {
            return location.OpenDecodedStream != null ? location.OpenDecodedStream() : ResourceFileReader.OpenRead(location.File);
        }

        private static (string, string, long, bool, ResourceFileType) Key(RawFileLocation location)
        {
            return (location.File.Location, location.ContainerSha256.ToLowerInvariant(), location.ContainerSize, location.IsEncrypted, location.FileType);
        }

        private void Release((string, string, long, bool, ResourceFileType) key, Entry entry)
        {
            if (entry.References == 1) {
                // Keep ownership intact if a custom stream fails to close, so unload can retry.
                if (entry.Completion.Operation.Status == OperationStatus.Succeeded) {
                    entry.Completion.Operation.Result.Dispose();
                }

                m_entries.Remove(key);
            }
            entry.References--;
        }

        // Each consumer has its own cursor; only seek+read on the shared stream is synchronized.
        private sealed class Lease: Stream
        {
            private readonly Stream m_stream;
            private readonly object m_gate;
            private Action m_release;
            private long m_position;
            internal Lease(Stream stream, object gate, Action release) { m_stream = stream; m_gate = gate; m_release = release; }
            private void Check()
            {
                if (m_release == null) {
                    throw new ObjectDisposedException(nameof(Lease));
                }
            }
            public override bool CanRead
            {
                get
                {
                    return m_release != null;
                }
            }

            public override bool CanSeek
            {
                get
                {
                    return m_release != null;
                }
            }

            public override bool CanWrite
            {
                get
                {
                    return false;
                }
            }

            public override long Length { get { Check(); lock (m_gate) { return m_stream.Length; } } }
            public override long Position { get { Check(); return m_position; } set { Seek(value, SeekOrigin.Begin); } }
            public override int Read(byte[] buffer, int offset, int count)
            {
                Check();
                lock (m_gate) {
                    m_stream.Position = m_position;
                    var read = m_stream.Read(buffer, offset, count);
                    m_position += read;
                    return read;
                }
            }
            public override long Seek(long offset, SeekOrigin origin)
            {
                Check();
                var next = checked(origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => m_position + offset,
                    SeekOrigin.End => Length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                });
                return next < 0 || next > Length ? throw new IOException("Seek outside raw container.") : (m_position = next);
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
            {
                if (disposing) { m_release?.Invoke(); m_release = null; }
                base.Dispose(disposing);
            }
        }
    }
}

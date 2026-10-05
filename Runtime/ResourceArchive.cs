using System;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine.Networking;

namespace ZRAsset
{
    /// <summary>ZRAsset 自有未压缩归档：固定头 + 按清单索引拼接的文件。不兼容 ZIP 或 YooAsset 私有格式。</summary>
    public static class ResourceArchive
    {
        public const int HeaderSize = ResourceManifestRules.ArchiveHeaderSize;
        private static readonly byte[] s_header = { 90, 82, 65, 82, 67, 72, 48, 49 }; // ZRARCH01
        public static void WriteHeader(Stream stream)
        {
            stream.Write(s_header, 0, s_header.Length);
        }

        internal static void ValidateHeader(Stream stream)
        {
            stream.Position = 0;
            foreach (var value in s_header) {
                if (stream.ReadByte() != value) {
                    throw new InvalidDataException("Invalid ZRArchive header.");
                }
            }
        }
    }

    /// <summary>原始文件加载的位置及校验边界；自定义后端和 Editor 模拟可提供独立源文件位置。</summary>
    public sealed class RawFileLocation
    {
        public ResourceFileLocation File { get; }
        public ResourceFileType FileType { get; }
        public long ContainerSize { get; }
        public string ContainerSha256 { get; }
        public long Offset { get; }
        public long Length { get; }
        public string Sha256 { get; }
        public bool IsEncrypted { get; private set; }
        internal Func<Stream> OpenDecodedStream { get; private set; }
        public RawFileLocation(ResourceFileLocation file, ResourceFileType fileType, long containerSize, string containerSha256,
            long offset, long length, string sha256)
        {
            File = file ?? throw new ArgumentNullException(nameof(file));
            if (fileType != ResourceFileType.RawFile && fileType != ResourceFileType.Archive) {
                throw new ArgumentOutOfRangeException(nameof(fileType));
            }

            if (offset < 0 || length < 0 || offset > containerSize || length > containerSize - offset ||
                (fileType == ResourceFileType.RawFile && (offset != 0 || length != containerSize)) ||
                (fileType == ResourceFileType.Archive && offset < ResourceArchive.HeaderSize)) {
                throw new ArgumentException("Invalid raw file range.");
            }

            if (!DownloadStorage.IsSha256(containerSha256) || !DownloadStorage.IsSha256(sha256)) {
                throw new ArgumentException("Raw files require SHA-256.");
            }

            FileType = fileType; ContainerSize = containerSize; ContainerSha256 = containerSha256;
            Offset = offset; Length = length; Sha256 = sha256;
        }
        internal static RawFileLocation From(ResourceFileLocation file, BundleInfo bundle, AssetInfo asset, IResourceDecryptionServices services = null)
        {
            BundleInfo snapshot = ResourceFileSystem.Snapshot(bundle);
            return new RawFileLocation(file, bundle.FileType, bundle.ContentSize, bundle.ContentSha256, asset.FileOffset, asset.FileSize, asset.FileSha256)
            { IsEncrypted = snapshot.IsEncrypted, OpenDecodedStream = snapshot.IsEncrypted ? () => ResourceEncryption.OpenRead(file, snapshot, services) : null };
        }
    }

    public interface IRawFileBackend
    {
        ResourceOperationBase<RawFileLocation> ResolveRawFileAsync(BundleInfo container, AssetInfo asset);
        RawFileLocation ResolveRawFile(BundleInfo container, AssetInfo asset);
    }

    internal sealed class RawFileSource: IDisposable
    {
        // URI 不能定位读取，显式限制整个容器的内存占用；大文件应通过缓存文件系统落盘。
        internal const int MaximumUriBytes = 32 * 1024 * 1024;
        internal readonly RawFileLocation Location;
        private readonly Stream m_stream;
        private readonly object m_gate = new();
        private bool m_disposed;
        internal bool IsDisposed { get { lock (m_gate) { return m_disposed; } } }
        private RawFileSource(RawFileLocation location, Stream stream) { Location = location; m_stream = stream; }
        internal static RawFileSource Open(RawFileLocation location, RawFileContainerCache containers = null)
        {
            if (location.File.LocalPath == null) {
                throw new NotSupportedException("同步原始文件读取要求本地文件，请先异步准备。");
            }

            Stream stream = (containers ?? new RawFileContainerCache()).Open(location);
            try {
                if (!UsesContainerHash(location)) {
                    CheckHash(ResourceFileIO.HashRange(stream, location.Offset, location.Length), location.Sha256);
                }
                return new RawFileSource(location, stream);
            }
            catch { stream.Dispose(); throw; }
        }
        internal static async ResourceOperationBase<RawFileSource> OpenAsync(RawFileLocation location, RawFileContainerCache containers = null)
        {
            Stream stream = await (containers ?? new RawFileContainerCache()).OpenAsync(location);
            try {
                if (!UsesContainerHash(location)) {
                    CheckHash(await ResourceFileIO.Shared.HashRangeAsync(stream, location.Offset, location.Length), location.Sha256);
                }
                return new RawFileSource(location, stream);
            }
            catch { stream.Dispose(); throw; }
        }
        // Open/OpenAsync hold a lease on the stream whose entire container was just verified.
        // Only an identical range AND digest can reuse that proof; archive entries still need their own hash.
        private static bool UsesContainerHash(RawFileLocation location)
        {
            return location.Offset == 0 && location.Length == location.ContainerSize &&
                string.Equals(location.Sha256, location.ContainerSha256, StringComparison.OrdinalIgnoreCase);
        }

        internal static void CheckLayout(RawFileLocation location, Stream stream)
        {
            if (stream.Length != location.ContainerSize) {
                throw new InvalidDataException("Raw container size mismatch.");
            }

            if (location.FileType == ResourceFileType.Archive) {
                ResourceArchive.ValidateHeader(stream);
            }
        }
        internal static void CheckHash(string actual, string expected)
        {
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("Raw file SHA-256 mismatch.");
            }
        }
        private sealed class BoundedDownload: DownloadHandlerScript
        {
            internal readonly MemoryStream Bytes = new();
            private readonly long m_maximum;
            internal bool Exceeded;
            internal BoundedDownload(long maximum) : base(new byte[64 * 1024]) { m_maximum = maximum; }
            protected override bool ReceiveData(byte[] data, int count)
            {
                if (data == null || count < 0 || Bytes.Length + count > m_maximum) { Exceeded = true; return false; }
                Bytes.Write(data, 0, count); return true;
            }
        }
        internal static async ResourceOperationBase<byte[]> ReadUriAsync(RawFileLocation location)
        {
            if (location.ContainerSize > MaximumUriBytes) {
                throw new NotSupportedException("URI 原始文件容器超过 32 MiB，请使用缓存文件系统下载到本地。");
            }

            using var handler = new BoundedDownload(location.ContainerSize);
            using var request = new UnityWebRequest(location.File.Location, UnityWebRequest.kHttpVerbGET, handler, null);
            request.timeout = 30; request.redirectLimit = 0;
            try {
                location.File.NetworkPolicy?.Configure(request, new ResourceRequestContext(location.File.Location, location.File.Location, ResourceRequestKind.RawFile));
                request.redirectLimit = 0;
                await UnityOperations.WaitAsync(request.SendWebRequest());
                return handler.Exceeded
                    ? throw new InvalidDataException("Raw file response exceeds its manifest size.")
                    : request.result != UnityWebRequest.Result.Success
                    ? throw new IOException("Raw file request failed: " + request.error)
                    : handler.Bytes.ToArray();
            }
            finally { handler.Bytes.Dispose(); }
        }
        internal int ReadAt(long position, byte[] buffer, int offset, int count)
        { lock (m_gate) { if (m_disposed) { throw new ObjectDisposedException(nameof(RawFileSource)); } m_stream.Position = Location.Offset + position; return m_stream.Read(buffer, offset, count); } }
        public void Dispose() { lock (m_gate) { if (m_disposed) { return; } m_stream.Dispose(); m_disposed = true; } }
    }
}

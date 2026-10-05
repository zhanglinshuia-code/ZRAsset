using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ZRAsset
{
    /// <summary>返回调用方拥有的 64 字节副本：AES-256 密钥与独立的 HMAC-SHA256 密钥各 32 字节。</summary>
    public interface IResourceKeyProvider { byte[] GetKey(string keyId); }

    /// <summary>解密服务返回可定位、可跨线程读取的只读明文流；调用方负责在资源卸载之后关闭。</summary>
    public interface IResourceDecryptionServices { Stream OpenRead(ResourceFileLocation file, BundleInfo info); }

    /// <summary>内存密钥环。构造与返回均复制密钥，不写入配置或清单；应用负责安全分发密钥。</summary>
    public sealed class ResourceKeyRing: IResourceKeyProvider, IDisposable
    {
        private readonly Dictionary<string, byte[]> m_keys = new(StringComparer.Ordinal);
        private readonly object m_gate = new();
        private bool m_disposed;
        public ResourceKeyRing(IDictionary<string, byte[]> values)
        {
            if (values == null) {
                throw new ArgumentNullException(nameof(values));
            }

            try {
                foreach (KeyValuePair<string, byte[]> pair in values) {
                    if (!ResourceEncryption.IsValidKeyId(pair.Key)) {
                        throw new ArgumentException("Invalid encryption key ID.");
                    }

                    ResourceEncryption.ValidateKey(pair.Value); m_keys.Add(pair.Key, (byte[])pair.Value.Clone());
                }
            }
            catch { Dispose(); throw; }
        }
        public byte[] GetKey(string keyId)
        {
            lock (m_gate) {
                return m_disposed
                    ? throw new ObjectDisposedException(nameof(ResourceKeyRing))
                    : keyId == null || !m_keys.TryGetValue(keyId, out var key)
                    ? throw new KeyNotFoundException("Encryption key unavailable: " + keyId)
                    : (byte[])key.Clone();
            }
        }
        public void Dispose()
        { lock (m_gate) { if (m_disposed) { return; } m_disposed = true; foreach (var key in m_keys.Values) { Array.Clear(key, 0, key.Length); } m_keys.Clear(); } }
    }

    public sealed class AesResourceDecryptionServices: IResourceDecryptionServices
    {
        private readonly IResourceKeyProvider m_keys;
        public AesResourceDecryptionServices(IResourceKeyProvider keys) { m_keys = keys ?? throw new ArgumentNullException(nameof(keys)); }
        public Stream OpenRead(ResourceFileLocation file, BundleInfo info)
        {
            if (file == null) {
                throw new ArgumentNullException(nameof(file));
            }

            BundleInfo snapshot = ResourceFileSystem.Snapshot(info);
            if (!snapshot.IsEncrypted) {
                throw new ArgumentException("The file is not encrypted.");
            }

            if (file.LocalPath == null) {
                throw new NotSupportedException("加密文件需要本地可定位来源，请先通过缓存文件系统下载。");
            }

            var key = m_keys.GetKey(snapshot.EncryptionKeyId);
            try {
                ResourceEncryption.ValidateKey(key);
                FileStream input = ResourceFileReader.OpenRead(file);
                try { return new ResourceDecryptionStream(input, snapshot, key); }
                catch { input.Dispose(); throw; }
            }
            finally {
                if (key != null) {
                    Array.Clear(key, 0, key.Length);
                }
            }
        }
    }

    /// <summary>分块 AES-256-CBC + 独立 HMAC-SHA256（先认证再解密），使用平台密码库，不使用自制密码算法。</summary>
    public static class ResourceEncryption
    {
        public const string Algorithm = ResourceManifestRules.Algorithm;
        public const int ChunkSize = ResourceManifestRules.ChunkSize;
        internal const int HeaderSize = 72, PrefixSize = HeaderSize + 32;
        private static readonly byte[] s_magic = Encoding.ASCII.GetBytes("ZRENC001");
        public static bool IsValidKeyId(string value)
        {
            return value != null && value.Length <= 64 && DownloadStorage.IsSafeSegment(value) &&
            value.All(c => (char.IsLetterOrDigit(c) && c < 128) || c == '_' || c == '-');
        }

        internal static void ValidateKey(byte[] key)
        {
            if (key == null || key.Length != 64) {
                throw new ArgumentException("Encryption requires 64 random key bytes (32 AES + 32 HMAC).");
            }
        }
        public static long GetEncryptedSize(long plainSize)
        {
            return ResourceManifestRules.GetEncryptedSize(plainSize);
        }

        internal static void ValidateMetadata(BundleInfo info)
        {
            ResourceManifestRules.ValidateEncryption(info);
        }

        internal static Stream OpenRead(ResourceFileLocation file, BundleInfo info, IResourceDecryptionServices services)
        {
            if (services == null) {
                throw new InvalidOperationException("加密资源未配置解密服务。");
            }

            Stream stream = services.OpenRead(file, ResourceFileSystem.Snapshot(info));
            try {
                if (stream == null || !stream.CanRead || !stream.CanSeek || stream.CanWrite || stream.Length != info.ContentSize) {
                    throw new InvalidDataException("解密服务必须返回指定长度的可定位只读内容流。");
                }

                stream.Position = 0; return stream;
            }
            catch { stream?.Dispose(); throw; }
        }
        /// <summary>写入新的加密内容。input 从头读取；output 必须为空。随机文件标识和逐块 IV 使重复构建产生不同密文。</summary>
        public static void Encrypt(Stream input, Stream output, byte[] key, string plaintextSha256)
        {
            ValidateKey(key);
            if (input == null || !input.CanRead || !input.CanSeek || output == null || !output.CanWrite || !output.CanSeek || output.Length != 0) {
                throw new ArgumentException("Encryption requires a seekable input and an empty output.");
            }

            if (!DownloadStorage.IsSha256(plaintextSha256)) {
                throw new ArgumentException("Plaintext SHA-256 is required.");
            }

            var size = input.Length; _ = GetEncryptedSize(size); input.Position = 0; output.Position = 0;
            byte[] aesKey = key.Take(32).ToArray(), macKey = key.Skip(32).ToArray(), buffer = new byte[ChunkSize];
            try {
                using var random = RandomNumberGenerator.Create(); using var sha = SHA256.Create();
                var header = new byte[HeaderSize]; Buffer.BlockCopy(s_magic, 0, header, 0, 8);
                Put(header, 8, ChunkSize, 4); Put(header, 12, size, 8);
                Buffer.BlockCopy(Hex(plaintextSha256), 0, header, 20, 32); var nonce = new byte[16]; random.GetBytes(nonce);
                Buffer.BlockCopy(nonce, 0, header, 52, 16);
                var headerMac = Mac(macKey, new byte[] { 0 }, header);
                output.Write(header, 0, header.Length); output.Write(headerMac, 0, headerMac.Length);
                long remaining = size, index = 0;
                while (remaining > 0) {
                    var count = (int)Math.Min(ChunkSize, remaining); ReadExactly(input, buffer, count);
                    sha.TransformBlock(buffer, 0, count, null, 0);
                    var iv = new byte[16]; random.GetBytes(iv);
                    byte[] cipher;
                    using (Aes aes = CreateAes(aesKey, iv))
                    using (ICryptoTransform encryptor = aes.CreateEncryptor()) {
                        cipher = encryptor.TransformFinalBlock(buffer, 0, count);
                    }

                    var number = new byte[8]; Put(number, 0, index++, 8);
                    var tag = Mac(macKey, new byte[] { 1 }, headerMac, number, iv, cipher);
                    output.Write(iv, 0, iv.Length); output.Write(cipher, 0, cipher.Length); output.Write(tag, 0, tag.Length);
                    remaining -= count;
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                if (input.ReadByte() != -1 || !Equal(sha.Hash, Hex(plaintextSha256))) {
                    throw new InvalidDataException("Plaintext changed during encryption.");
                }
            }
            finally { Array.Clear(aesKey, 0, aesKey.Length); Array.Clear(macKey, 0, macKey.Length); Array.Clear(buffer, 0, buffer.Length); }
        }
        internal static Aes CreateAes(byte[] key, byte[] iv)
        { var aes = Aes.Create(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; aes.KeySize = 256; aes.Key = key; aes.IV = iv; return aes; }
        internal static byte[] Mac(byte[] key, params byte[][] values)
        {
            using var mac = new HMACSHA256(key);
            foreach (var bytes in values) {
                mac.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }

            mac.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return mac.Hash;
        }
        internal static bool Equal(byte[] left, byte[] right)
        {
            return CryptographicOperations.FixedTimeEquals(left, right);
        }

        internal static byte[] Hex(string value)
        {
            return Enumerable.Range(0, value.Length / 2).Select(i => Convert.ToByte(value.Substring(i * 2, 2), 16)).ToArray();
        }

        internal static void Put(byte[] buffer, int offset, long value, int count)
        {
            for (var i = 0; i < count; i++) {
                buffer[offset + i] = (byte)((ulong)value >> (i * 8));
            }
        }
        internal static long Get(byte[] buffer, int offset, int count)
        { ulong value = 0; for (var i = 0; i < count; i++) { value |= (ulong)buffer[offset + i] << (i * 8); } return unchecked((long)value); }
        internal static void ReadExactly(Stream input, byte[] buffer, int count)
        { var offset = 0; while (offset < count) { var read = input.Read(buffer, offset, count - offset); if (read == 0) { throw new EndOfStreamException(); } offset += read; } }
        internal static void ValidateHeader(byte[] header, BundleInfo info)
        {
            if (!Equal(header.Take(8).ToArray(), s_magic) || Get(header, 8, 4) != ChunkSize || Get(header, 12, 8) != info.ContentSize ||
                !Equal(header.Skip(20).Take(32).ToArray(), Hex(info.ContentSha256)) || Get(header, 68, 4) != 0) {
                throw new InvalidDataException("Encrypted header does not match the manifest.");
            }
        }
    }

    /// <summary>一次仅缓存一个明文块；Seek/Read/Dispose 串行保护，允许 Unity 从原生工作线程调用及越过 EOF 定位。</summary>
    internal sealed class ResourceDecryptionStream: Stream
    {
        private readonly Stream m_input;
        private readonly byte[] m_aesKey, m_macKey, m_headerMac;
        private readonly long m_length;
        private readonly object m_gate = new();
        private byte[] m_cache;
        private long m_position, m_cachedIndex = -1;
        private bool m_disposed;
        internal ResourceDecryptionStream(Stream input, BundleInfo info, byte[] key)
        {
            m_input = input; m_length = info.ContentSize; m_aesKey = key.Take(32).ToArray(); m_macKey = key.Skip(32).ToArray();
            try {
                if (!input.CanSeek || input.Length != info.Size) {
                    throw new InvalidDataException("Encrypted container size mismatch.");
                }

                input.Position = 0; var header = new byte[ResourceEncryption.HeaderSize]; m_headerMac = new byte[32];
                ResourceEncryption.ReadExactly(input, header, header.Length); ResourceEncryption.ReadExactly(input, m_headerMac, 32);
                if (!ResourceEncryption.Equal(m_headerMac, ResourceEncryption.Mac(m_macKey, new byte[] { 0 }, header))) {
                    throw new InvalidDataException("Encrypted content authentication failed.");
                }

                ResourceEncryption.ValidateHeader(header, info);
            }
            catch { Array.Clear(m_aesKey, 0, m_aesKey.Length); Array.Clear(m_macKey, 0, m_macKey.Length); throw; }
        }
        public override bool CanRead
        {
            get
            {
                return !m_disposed;
            }
        }

        public override bool CanSeek
        {
            get
            {
                return !m_disposed;
            }
        }

        public override bool CanWrite
        {
            get
            {
                return false;
            }
        }

        private void Check()
        {
            if (m_disposed) {
                throw new ObjectDisposedException(nameof(ResourceDecryptionStream));
            }
        }
        public override long Length { get { lock (m_gate) { Check(); return m_length; } } }
        public override long Position
        {
            get { lock (m_gate) { Check(); return m_position; } }

            set
            {
                Seek(value, SeekOrigin.Begin);
            }
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (m_gate) {
                Check(); if (buffer == null) {
                    throw new ArgumentNullException(nameof(buffer));
                }

                if (offset < 0 || count < 0 || offset > buffer.Length - count) {
                    throw new ArgumentOutOfRangeException();
                }

                var total = 0;
                while (count > 0 && m_position < m_length) {
                    var index = m_position / ResourceEncryption.ChunkSize; EnsureChunk(index);
                    int within = (int)(m_position % ResourceEncryption.ChunkSize), copied = Math.Min(count, m_cache.Length - within);
                    Buffer.BlockCopy(m_cache, within, buffer, offset, copied); count -= copied; offset += copied; total += copied; m_position += copied;
                }
                return total;
            }
        }
        private void EnsureChunk(long index)
        {
            if (m_cachedIndex == index) {
                return;
            }

            if (m_cache != null) { Array.Clear(m_cache, 0, m_cache.Length); m_cache = null; }
            m_cachedIndex = -1;
            var plainCount = (int)Math.Min(ResourceEncryption.ChunkSize, m_length - (index * ResourceEncryption.ChunkSize));
            var iv = new byte[16]; var cipher = new byte[((plainCount / 16) + 1) * 16]; var tag = new byte[32];
            m_input.Position = checked(ResourceEncryption.PrefixSize + (index * (ResourceEncryption.ChunkSize + 64L)));
            ResourceEncryption.ReadExactly(m_input, iv, 16); ResourceEncryption.ReadExactly(m_input, cipher, cipher.Length); ResourceEncryption.ReadExactly(m_input, tag, 32);
            var number = new byte[8]; ResourceEncryption.Put(number, 0, index, 8);
            if (!ResourceEncryption.Equal(tag, ResourceEncryption.Mac(m_macKey, new byte[] { 1 }, m_headerMac, number, iv, cipher))) {
                throw new InvalidDataException("Encrypted content authentication failed.");
            }

            using Aes aes = ResourceEncryption.CreateAes(m_aesKey, iv); using ICryptoTransform decryptor = aes.CreateDecryptor();
            m_cache = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
            if (m_cache.Length != plainCount) { Array.Clear(m_cache, 0, m_cache.Length); m_cache = null; throw new InvalidDataException("Invalid encrypted block length."); }
            m_cachedIndex = index;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (m_gate) {
                Check(); var next = checked(origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => m_position + offset,
                    SeekOrigin.End => m_length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                });
                return next < 0 ? throw new IOException("Negative stream position.") : (m_position = next);
            }
        }
        public override void Flush()
        {
            lock (m_gate) {
                Check();
            }
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            lock (m_gate) {
                if (disposing && !m_disposed) {
                    m_disposed = true;
                    try { m_input.Dispose(); }
                    finally { Array.Clear(m_aesKey, 0, m_aesKey.Length); Array.Clear(m_macKey, 0, m_macKey.Length); if (m_cache != null) { Array.Clear(m_cache, 0, m_cache.Length); } m_cache = null; }
                }
            }
            base.Dispose(disposing);
        }
    }
}

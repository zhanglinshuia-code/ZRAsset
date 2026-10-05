using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    public interface IResourceManifestCodec
    {
        string Id { get; }
        byte[] Encode(byte[] input);
        byte[] Decode(byte[] input, int expectedSize);
    }

    public sealed class GzipResourceManifestCodec: IResourceManifestCodec
    {
        public string Id { get { return "gzip"; } }

        public byte[] Encode(byte[] input)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, true)) {
                gzip.Write(input, 0, input.Length);
            }

            return output.ToArray();
        }

        public byte[] Decode(byte[] input, int expectedSize)
        {
            if (expectedSize < 0 || expectedSize > ResourceManifestEnvelope.MaximumBytes) {
                throw new InvalidDataException("清单解压尺寸无效。");
            }

            using var source = new MemoryStream(input, false);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            var result = new byte[expectedSize];
            ResourceEncryption.ReadExactly(gzip, result, result.Length);
            return gzip.ReadByte() != -1 ? throw new InvalidDataException("清单解压数据超过声明尺寸。") : result;
        }
    }

    /// <summary>二进制清单 → 可选压缩 → 可选分块认证加密。网络身份仍由发布签名验证。</summary>
    public static class ResourceManifestEnvelope
    {
        public const int MaximumBytes = 64 * 1024 * 1024;
        private static readonly IResourceManifestCodec s_gzip = new GzipResourceManifestCodec();

        [Serializable]
        private sealed class Envelope
        {
            public string ManifestEncoding;
            public int Format;
            public int DecodedSize;
            public string DecodedSha256;
            public int PayloadSize;
            public string PayloadSha256;
            public string KeyId;
            public string Data;
        }

        public static string Encode(ResourceManifest manifest, IResourceManifestCodec codec = null, string keyId = null, IResourceKeyProvider keys = null)
        {
            return EncodeBytes(ResourceManifestBinary.Serialize(manifest), codec, keyId, keys);
        }

        /// <summary>供联合发布目录等清单文档复用同一有界编码及认证流水线。</summary>
        public static string EncodeBytes(byte[] decoded, IResourceManifestCodec codec = null, string keyId = null, IResourceKeyProvider keys = null)
        {
            if (decoded == null || decoded.Length == 0 || decoded.Length > MaximumBytes) {
                throw new InvalidDataException("清单原始数据长度无效。");
            }

            codec ??= s_gzip;
            if (!DownloadStorage.IsSafeSegment(codec.Id)) {
                throw new ArgumentException("清单编码名称无效。");
            }

            var payload = codec.Encode(decoded);
            if (payload == null || payload.Length == 0 || payload.Length > MaximumBytes) {
                throw new InvalidDataException("编码清单超出上限。");
            }

            var envelope = new Envelope
            {
                ManifestEncoding = codec.Id,
                Format = 1,
                DecodedSize = decoded.Length,
                DecodedSha256 = Hash(decoded),
                PayloadSize = payload.Length,
                PayloadSha256 = Hash(payload),
                KeyId = keyId
            };
            if (!string.IsNullOrEmpty(keyId)) {
                if (!ResourceEncryption.IsValidKeyId(keyId) || keys == null) {
                    throw new ArgumentException("缺少清单加密密钥。");
                }

                var key = keys.GetKey(keyId);
                try {
                    using var input = new MemoryStream(payload, false);
                    using var output = new MemoryStream();
                    ResourceEncryption.Encrypt(input, output, key, envelope.PayloadSha256);
                    payload = output.ToArray();
                }
                finally {
                    if (key != null) {
                        Array.Clear(key, 0, key.Length);
                    }
                }
            }
            envelope.Data = Convert.ToBase64String(payload);
            return JsonUtility.ToJson(envelope);
        }

        public static ResourceManifest Decode(string json, IResourceKeyProvider keys = null, IResourceManifestCodec codec = null)
        {
            return ResourceManifestBinary.Deserialize(DecodeBytes(json, keys, codec));
        }

        public static byte[] DecodeBytes(string json, IResourceKeyProvider keys = null, IResourceManifestCodec codec = null)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 96 * 1024 * 1024) {
                throw new InvalidDataException("编码清单长度无效。");
            }

            Envelope envelope = JsonUtility.FromJson<Envelope>(json);
            codec ??= s_gzip;
            ValidateEnvelope(envelope, codec);
            byte[] key = null, payload = null;
            try {
                key = GetDecryptionKey(envelope, keys);
                ResourceManifestWork.Drain(DecodePayloadSteps(envelope, key, value => payload = value));
            }
            finally {
                if (key != null) { Array.Clear(key, 0, key.Length); }
            }
            var decoded = codec.Decode(payload, envelope.DecodedSize);
            if (decoded == null || decoded.Length != envelope.DecodedSize) { throw new InvalidDataException("清单解码内容校验失败。"); }
            ResourceManifestWork.Drain(CheckHashSteps(decoded, envelope.DecodedSha256));
            return decoded;
        }

        /// <summary>默认 Gzip、认证解密和哈希在受限工作队列执行，WebGL 分块让帧。自定义 Codec 和密钥提供者仍在调用线程执行。</summary>
        public static ResourceOperationBase<byte[]> DecodeBytesAsync(string json, IResourceKeyProvider keys = null,
            IResourceManifestCodec codec = null, CancellationToken cancellationToken = default)
        {
            return DecodeAsync(json, keys, codec, cancellationToken, false);
        }

        internal static ResourceOperationBase<byte[]> TryDecodeBytesAsync(string json, IResourceKeyProvider keys,
            IResourceManifestCodec codec, CancellationToken token)
        {
            return DecodeAsync(json, keys, codec, token, true);
        }

        internal static async ResourceOperationBase<byte[]> DecodeAsync(string json, IResourceKeyProvider keys,
            IResourceManifestCodec codec, CancellationToken token, bool allowPlain, bool cooperative = false)
        {
            Envelope envelope = null;
            await ResourceManifestWork.RunAsync(Parse(), token, cooperative);
            if (envelope == null) { return null; }
            codec ??= s_gzip;
            ValidateEnvelope(envelope, codec);
            byte[] key = null, payload = null, decoded = null;
            try {
                // Never move user services onto a worker. The returned key is owned by this call.
                key = GetDecryptionKey(envelope, keys);
                await ResourceManifestWork.RunAsync(DecodePayloadSteps(envelope, key, value => payload = value), token, cooperative);
            }
            finally {
                if (key != null) { Array.Clear(key, 0, key.Length); }
            }
            if (codec is GzipResourceManifestCodec) {
                await ResourceManifestWork.RunAsync(DecodeGzipSteps(payload, envelope.DecodedSize, value => decoded = value), token, cooperative);
            }
            else {
                token.ThrowIfCancellationRequested();
                decoded = codec.Decode(payload, envelope.DecodedSize);
            }
            if (decoded == null || decoded.Length != envelope.DecodedSize) { throw new InvalidDataException("清单解码内容校验失败。"); }
            await ResourceManifestWork.RunAsync(CheckHashSteps(decoded, envelope.DecodedSha256), token, cooperative);
            return decoded;

            IEnumerable<int> Parse()
            {
                if (allowPlain && (json == null || json.IndexOf("\"ManifestEncoding\"", StringComparison.Ordinal) < 0)) { yield break; }
                if (string.IsNullOrEmpty(json) || json.Length > 96 * 1024 * 1024) { throw new InvalidDataException("编码清单长度无效。"); }
                envelope = JsonUtility.FromJson<Envelope>(json);
                if (allowPlain && string.IsNullOrEmpty(envelope?.ManifestEncoding)) { envelope = null; }
                else if (envelope == null) { throw new InvalidDataException("编码清单头部无效。"); }
                yield return 0;
            }
        }

        private static void ValidateEnvelope(Envelope envelope, IResourceManifestCodec codec)
        {
            if (envelope == null || envelope.Format != 1 || envelope.ManifestEncoding != codec.Id || envelope.DecodedSize <= 0 ||
                envelope.DecodedSize > MaximumBytes || envelope.PayloadSize <= 0 || envelope.PayloadSize > MaximumBytes ||
                !DownloadStorage.IsSha256(envelope.DecodedSha256) || !DownloadStorage.IsSha256(envelope.PayloadSha256)) {
                throw new InvalidDataException("编码清单头部无效或缺少对应 Codec。");
            }
        }

        private static byte[] GetDecryptionKey(Envelope envelope, IResourceKeyProvider keys)
        {
            if (string.IsNullOrEmpty(envelope.KeyId)) { return null; }
            if (!ResourceEncryption.IsValidKeyId(envelope.KeyId) || keys == null) { throw new InvalidDataException("缺少清单解密密钥。"); }
            var key = keys.GetKey(envelope.KeyId);
            try {
                ResourceEncryption.ValidateKey(key);
                return key;
            }
            catch {
                if (key != null) { Array.Clear(key, 0, key.Length); }
                throw;
            }
        }

        private static IEnumerable<int> DecodePayloadSteps(Envelope envelope, byte[] key, Action<byte[]> publish)
        {
            var size = checked((int)(key == null ? envelope.PayloadSize : ResourceEncryption.GetEncryptedSize(envelope.PayloadSize)));
            byte[] payload = null;
            foreach (var step in DecodeBase64Steps(envelope.Data, size, value => payload = value)) { yield return step; }
            if (key != null) {
                var info = new BundleInfo
                {
                    Size = size,
                    Encryption = ResourceEncryption.Algorithm,
                    EncryptionKeyId = envelope.KeyId,
                    UnencryptedSize = envelope.PayloadSize,
                    UnencryptedSha256 = envelope.PayloadSha256
                };
                using var source = new MemoryStream(payload, false);
                using var decrypted = new ResourceDecryptionStream(source, info, key);
                payload = new byte[envelope.PayloadSize];
                foreach (var step in ReadSteps(decrypted, payload)) { yield return step; }
            }
            foreach (var step in CheckHashSteps(payload, envelope.PayloadSha256)) { yield return step; }
            publish(payload);
        }

        private static IEnumerable<int> DecodeBase64Steps(string text, int size, Action<byte[]> publish)
        {
            if (string.IsNullOrEmpty(text) || text.Length > (((long)size + 2) / 3 * 4)) { throw new InvalidDataException("编码清单 Base64 长度无效。"); }
            var output = new byte[size];
            var chars = new char[16384];
            int count = 0, offset = 0;
            var padded = false;
            for (var i = 0; i < text.Length; i++) {
                var value = text[i];
                if (value == ' ' || value == '\r' || value == '\n' || value == '\t') { continue; }
                if (padded) { throw new InvalidDataException("编码清单 Base64 填充无效。"); }
                chars[count++] = value;
                if (count == chars.Length) {
                    offset += DecodeBase64Block(chars, count, output, offset);
                    padded = chars[count - 1] == '=';
                    count = 0;
                    yield return 0;
                }
            }
            if (count != 0) { offset += DecodeBase64Block(chars, count, output, offset); }
            if (offset != size) { throw new InvalidDataException("编码清单大小不匹配。"); }
            publish(output);
        }

        private static int DecodeBase64Block(char[] chars, int count, byte[] output, int offset)
        {
            return !Convert.TryFromBase64Chars(chars.AsSpan(0, count), output.AsSpan(offset), out var written)
                ? throw new InvalidDataException("编码清单 Base64 无效。")
                : written;
        }

        private static IEnumerable<int> DecodeGzipSteps(byte[] payload, int size, Action<byte[]> publish)
        {
            using var source = new MemoryStream(payload, false);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            var decoded = new byte[size];
            foreach (var step in ReadSteps(gzip, decoded)) { yield return step; }
            if (gzip.ReadByte() != -1) { throw new InvalidDataException("清单解压数据超过声明尺寸。"); }
            publish(decoded);
        }

        private static IEnumerable<int> ReadSteps(Stream stream, byte[] output)
        {
            for (var offset = 0; offset < output.Length;) {
                var read = stream.Read(output, offset, Math.Min(64 * 1024, output.Length - offset));
                if (read == 0) { throw new InvalidDataException("编码清单数据不完整。"); }
                offset += read;
                yield return 0;
            }
        }

        private static IEnumerable<int> CheckHashSteps(byte[] bytes, string expected)
        {
            using var sha = SHA256.Create();
            for (var offset = 0; offset < bytes.Length; offset += 64 * 1024) {
                var count = Math.Min(64 * 1024, bytes.Length - offset);
                sha.TransformBlock(bytes, offset, count, bytes, offset);
                yield return 0;
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if (!string.Equals(BitConverter.ToString(sha.Hash).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("清单解码内容校验失败。");
            }
        }

        internal static bool IsEnvelope(string json)
        {
            return json != null && json.IndexOf("\"ManifestEncoding\"", StringComparison.Ordinal) >= 0 &&
                !string.IsNullOrEmpty(JsonUtility.FromJson<Envelope>(json)?.ManifestEncoding);
        }

        /// <summary>读取密钥标识只用于选择密钥，不代表信任清单；仍须 Decode 校验完整内容。</summary>
        public static string ReadKeyId(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 96 * 1024 * 1024) {
                throw new InvalidDataException("编码清单长度无效。");
            }

            Envelope header = JsonUtility.FromJson<Envelope>(json);
            return header == null || header.Format != 1 || !DownloadStorage.IsSafeSegment(header.ManifestEncoding) ||
                (!string.IsNullOrEmpty(header.KeyId) && !ResourceEncryption.IsValidKeyId(header.KeyId))
                ? throw new InvalidDataException("编码清单头部无效。")
                : header.KeyId;
        }

        private static string Hash(byte[] data)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
    }
}

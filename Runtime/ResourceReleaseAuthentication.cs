using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>预置于客户端的发布公钥；KeyId 用于轮换，不从 CDN 自动增加信任。</summary>
    [Serializable]
    public sealed class ResourceReleasePublicKey
    {
        public string KeyId;
        public string ModulusBase64;
        public string ExponentBase64;

        public static ResourceReleasePublicKey FromRsa(string keyId, RSA rsa)
        {
            if (rsa == null) {
                throw new ArgumentNullException(nameof(rsa));
            }

            RSAParameters value = rsa.ExportParameters(false);
            return new ResourceReleasePublicKey
            {
                KeyId = keyId,
                ModulusBase64 = Convert.ToBase64String(value.Modulus),
                ExponentBase64 = Convert.ToBase64String(value.Exponent)
            };
        }
    }

    /// <summary>签名载荷：清单哈希继续绑定 Bundle 哈希；序号和有效期也在签名范围内。</summary>
    [Serializable]
    public sealed class ResourceSignedReleasePayload
    {
        public ResourceReleaseInfo Release;
        public string PlayerBuildId; // 代码发布必须绑定宿主；纯资源接口可省略。
        public long Sequence;
        public long IssuedUtcSeconds;
        public long ExpiresUtcSeconds;
    }

    /// <summary>网络格式固定为 v1/RSA-SHA256；签名覆盖 KeyId 与确切载荷字节，无需 JSON 规范化。</summary>
    [Serializable]
    public sealed class ResourceSignedReleaseEnvelope
    {
        public int FormatVersion = 1;
        public string Algorithm = "RSA-SHA256";
        public string KeyId;
        public string PayloadBase64;
        public string SignatureBase64;
    }

    /// <summary>
    /// 复制信任配置，之后修改输入公钥对象不会改变验证结果。
    /// MinimumSequence 是宿主可信配置的发布下限，不是自动持久化防重放计数器；有效期依赖可信设备时钟。
    /// </summary>
    public sealed class ResourceReleaseTrustOptions
    {
        private readonly Dictionary<string, RSAParameters> m_keys = new(StringComparer.Ordinal);
        public long MinimumSequence { get; }
        public bool AllowHttpLoopback { get; }
        public int ClockSkewSeconds { get; }
        public int MaximumLifetimeSeconds { get; }
        public int MaximumManifestBytes { get; }

        public ResourceReleaseTrustOptions(IEnumerable<ResourceReleasePublicKey> trustedKeys,
            long minimumSequence = 0, bool allowHttpLoopback = false, int clockSkewSeconds = 120,
            int maximumLifetimeSeconds = 7 * 24 * 60 * 60, int maximumManifestBytes = 8 * 1024 * 1024)
        {
            if (trustedKeys == null) {
                throw new ArgumentNullException(nameof(trustedKeys));
            }

            if (minimumSequence < 0 || clockSkewSeconds < 0 || clockSkewSeconds > 3600 ||
                maximumLifetimeSeconds <= 0 || maximumLifetimeSeconds > 366 * 24 * 60 * 60 ||
                maximumManifestBytes <= 0 || maximumManifestBytes > 64 * 1024 * 1024) {
                throw new ArgumentOutOfRangeException(nameof(minimumSequence), "发布信任策略的范围无效。");
            }

            foreach (ResourceReleasePublicKey key in trustedKeys) {
                if (key == null || !DownloadStorage.IsSafeSegment(key.KeyId)) {
                    throw new ArgumentException("公钥必须指定安全且唯一的 KeyId。", nameof(trustedKeys));
                }

                var modulus = ResourceReleaseAuthentication.DecodeBase64(key.ModulusBase64, 1024, "公钥模数");
                var exponent = ResourceReleaseAuthentication.DecodeBase64(key.ExponentBase64, 8, "公钥指数");
                ulong exponentValue = 0;
                foreach (var part in exponent) {
                    exponentValue = (exponentValue << 8) | part;
                }

                if (modulus.Length < 256 || modulus[0] < 128 || (modulus[modulus.Length - 1] & 1) == 0 ||
                    exponentValue < 3 || (exponentValue & 1) == 0 ||
                    !m_keys.TryAdd(key.KeyId, new RSAParameters { Modulus = modulus, Exponent = exponent })) {
                    throw new ArgumentException("RSA 公钥必须为 2048 至 8192 位、使用有效奇数指数，且 KeyId 不得重复。", nameof(trustedKeys));
                }

                using (var rsa = RSA.Create()) {
                    rsa.ImportParameters(m_keys[key.KeyId]);
                }
            }
            if (m_keys.Count == 0) {
                throw new ArgumentException("必须预置信任公钥。", nameof(trustedKeys));
            }

            MinimumSequence = minimumSequence;
            AllowHttpLoopback = allowHttpLoopback;
            ClockSkewSeconds = clockSkewSeconds;
            MaximumLifetimeSeconds = maximumLifetimeSeconds;
            MaximumManifestBytes = maximumManifestBytes;
        }

        internal RSA CreateVerifier(string keyId)
        {
            if (keyId == null || !m_keys.TryGetValue(keyId, out RSAParameters key)) {
                throw new InvalidDataException("发布签名使用了未受信任的 KeyId。");
            }

            var rsa = RSA.Create();
            try { rsa.ImportParameters(key); return rsa; }
            catch { rsa.Dispose(); throw; }
        }

        internal Uri ValidateSource(string url)
        {
            return !Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Fragment) || (uri.Scheme != Uri.UriSchemeHttps &&
                !(AllowHttpLoopback && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
                ? throw new InvalidDataException("签名发布地址必须使用 HTTPS；仅测试策略允许 loopback HTTP，且禁止用户信息和片段。")
                : uri;
        }
    }

    /// <summary>独立签名验证边界；验证成功之前不解析载荷里的版本或地址。</summary>
    public static class ResourceReleaseAuthentication
    {
        public const int MaximumEnvelopeBytes = 64 * 1024;
        public const int MaximumPayloadBytes = 16 * 1024;
        private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

        public static ResourceSignedReleasePayload Verify(string envelopeJson, ResourceReleaseTrustOptions trust,
            DateTimeOffset now)
        {
            try {
                return VerifyCore(envelopeJson, trust, now);
            }
            catch (Exception error) when (error is InvalidDataException || error is CryptographicException) {
                ResourceFailure.Annotate(error, ResourceErrorCode.Authentication, ResourceStage.CheckRelease);
                throw;
            }
        }

        private static ResourceSignedReleasePayload VerifyCore(string envelopeJson, ResourceReleaseTrustOptions trust,
            DateTimeOffset now)
        {
            if (trust == null) {
                throw new ArgumentNullException(nameof(trust));
            }

            if (string.IsNullOrEmpty(envelopeJson) || s_strictUtf8.GetByteCount(envelopeJson) > MaximumEnvelopeBytes) {
                throw new InvalidDataException("签名发布描述为空或超出长度限制。");
            }

            ResourceSignedReleaseEnvelope envelope;
            try { envelope = JsonUtility.FromJson<ResourceSignedReleaseEnvelope>(envelopeJson); }
            catch (ArgumentException) { throw new InvalidDataException("签名发布描述 JSON 无效。"); }
            if (envelope == null || envelope.FormatVersion != 1 || envelope.Algorithm != "RSA-SHA256" ||
                !DownloadStorage.IsSafeSegment(envelope.KeyId)) {
                throw new InvalidDataException("不支持的发布签名格式或算法。");
            }

            var payload = DecodeBase64(envelope.PayloadBase64, MaximumPayloadBytes, "签名载荷");
            var signature = DecodeBase64(envelope.SignatureBase64, 1024, "签名");
            using (RSA rsa = trust.CreateVerifier(envelope.KeyId)) {
                if (signature.Length != rsa.KeySize / 8 || !rsa.VerifyData(CreateSigningInput(envelope.KeyId, payload),
                    signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) {
                    throw new InvalidDataException("发布签名验证失败。");
                }
            }
            ResourceSignedReleasePayload result;
            try { result = JsonUtility.FromJson<ResourceSignedReleasePayload>(s_strictUtf8.GetString(payload)); }
            catch (Exception error) when (error is ArgumentException || error is DecoderFallbackException) { throw new InvalidDataException("已签名的发布载荷不是有效 UTF-8 JSON。"); }
            ValidatePayload(result, trust, now);
            return result;
        }

        /// <summary>固定协议域和 KeyId 防止同一签名被另一协议或 KeyId 重解释；发布端也必须使用此函数。</summary>
        public static byte[] CreateSigningInput(string keyId, byte[] payload)
        {
            if (!DownloadStorage.IsSafeSegment(keyId)) {
                throw new ArgumentException("KeyId 无效。", nameof(keyId));
            }

            if (payload == null || payload.Length == 0 || payload.Length > MaximumPayloadBytes) {
                throw new ArgumentException("签名载荷长度无效。", nameof(payload));
            }

            var prefix = Encoding.ASCII.GetBytes("ZRAsset.Release.v1\nRSA-SHA256\n" + keyId + "\n");
            var input = new byte[prefix.Length + payload.Length];
            Buffer.BlockCopy(prefix, 0, input, 0, prefix.Length);
            Buffer.BlockCopy(payload, 0, input, prefix.Length, payload.Length);
            return input;
        }

        internal static byte[] DecodeBase64(string value, int limit, string description)
        {
            if (string.IsNullOrEmpty(value) || value.Length > (limit + 2) / 3 * 4) {
                throw new InvalidDataException(description + " 长度无效。");
            }

            try {
                var bytes = Convert.FromBase64String(value);
                return bytes.Length == 0 || bytes.Length > limit ? throw new InvalidDataException(description + " 长度无效。") : bytes;
            }
            catch (FormatException) { throw new InvalidDataException(description + " 不是有效 Base64。"); }
        }

        private static void ValidatePayload(ResourceSignedReleasePayload payload, ResourceReleaseTrustOptions trust,
            DateTimeOffset now)
        {
            ResourceReleaseInfo release = payload?.Release;
            if (release == null || !DownloadStorage.IsSafeSegment(release.Version) ||
                !DownloadStorage.IsSafeSegment(release.BuildTarget) || !DownloadStorage.IsSha256(release.ManifestSha256) ||
                string.IsNullOrWhiteSpace(release.ManifestUrl) || release.ManifestUrl.Length > 4096) {
                throw new InvalidDataException("已签名的发布描述字段无效。");
            }

            var current = now.ToUnixTimeSeconds();
            if (payload.Sequence < trust.MinimumSequence || payload.Sequence < 0) {
                throw new InvalidDataException("发布序号低于客户端信任策略下限。");
            }

            if (payload.IssuedUtcSeconds <= 0 || payload.ExpiresUtcSeconds <= payload.IssuedUtcSeconds ||
                payload.ExpiresUtcSeconds - payload.IssuedUtcSeconds > trust.MaximumLifetimeSeconds ||
                payload.IssuedUtcSeconds > current + trust.ClockSkewSeconds ||
                payload.ExpiresUtcSeconds <= current - trust.ClockSkewSeconds) {
                throw new InvalidDataException("发布签名尚未生效、已经过期或有效期超出策略。");
            }
        }
    }
}

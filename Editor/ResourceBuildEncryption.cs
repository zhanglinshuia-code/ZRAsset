using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using UnityEngine;

namespace ZRAsset.Editor
{
    /// <summary>构建密钥注入点。只保存在当前 Editor 进程中，不序列化到资产、清单或构建报告。</summary>
    public static class ResourceBuildEncryption
    {
        public static IResourceKeyProvider KeyProvider { get; set; }
        private sealed class EnvironmentKeys: IResourceKeyProvider
        {
            public byte[] GetKey(string keyId)
            {
                var value = Environment.GetEnvironmentVariable("ZRASSET_KEY_" + keyId);
                if (string.IsNullOrWhiteSpace(value)) {
                    throw new InvalidOperationException("未提供构建密钥：" + keyId + "。请注入 KeyProvider 或设置对应的 ZRASSET_KEY_ 环境变量。");
                }

                try { return Convert.FromBase64String(value); }
                catch (FormatException) { throw new InvalidOperationException("构建密钥必须为 Base64 编码的 64 字节随机值。"); }
            }
        }
        internal static byte[] ResolveKey(string keyId, IResourceKeyProvider provider)
        {
            if (string.IsNullOrEmpty(keyId)) {
                return null;
            }

            var key = (provider ?? KeyProvider ?? new EnvironmentKeys()).GetKey(keyId);
            if (key == null || key.Length != 64) { if (key != null) { Array.Clear(key, 0, key.Length); } throw new InvalidOperationException("构建密钥必须包含 64 字节。"); }
            return key;
        }
        internal static void Encrypt(string stage, BundleInfo[] files, string keyId, byte[] key, bool reuseArtifacts = false)
        {
            if (key == null) {
                return;
            }

            foreach (BundleInfo file in files) {
                string path = Path.Combine(stage, file.Name), temporary = path + ".encrypting";
                string cache = null;
                if (reuseArtifacts) {
                    using var mac = new HMACSHA256(key);
                    var identity = BitConverter.ToString(mac.ComputeHash(Encoding.UTF8.GetBytes(
                        "ZRAsset-encrypted-cache-v1|" + keyId + "|" + file.Name + "|" + file.Sha256))).Replace("-", "").ToLowerInvariant();
                    cache = Path.GetFullPath(Path.Combine("Library", "ZRAssetEncryptedArtifacts", identity));
                    if (TryReuse(cache, path, file, keyId, key)) {
                        continue;
                    }
                }
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    ResourceEncryption.Encrypt(input, output, key, file.Sha256);
                }

                file.UnencryptedSize = file.Size; file.UnencryptedSha256 = file.Sha256;
                file.Encryption = ResourceEncryption.Algorithm; file.EncryptionKeyId = keyId;
                using (FileStream input = File.OpenRead(temporary))
                using (var sha = SHA256.Create()) {
                    file.Sha256 = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                }

                file.Size = new FileInfo(temporary).Length;
                File.Delete(path); File.Move(temporary, path);
                if (cache != null) {
                    Directory.CreateDirectory(Path.GetDirectoryName(cache));
                    File.Copy(path, cache + ".data", true);
                    File.WriteAllText(cache + ".json", JsonUtility.ToJson(file));
                }
            }
        }
        private static bool TryReuse(string cache, string destination, BundleInfo file, string keyId, byte[] key)
        {
            if (!File.Exists(cache + ".json") || !File.Exists(cache + ".data")) {
                return false;
            }

            try {
                BundleInfo stored = JsonUtility.FromJson<BundleInfo>(File.ReadAllText(cache + ".json"));
                if (stored == null || stored.EncryptionKeyId != keyId || stored.UnencryptedSize != file.Size ||
                    stored.UnencryptedSha256 != file.Sha256 || stored.Size != new FileInfo(cache + ".data").Length ||
                    stored.Sha256 != BundleBuilder.ComputeSha256(cache + ".data")) {
                    return false;
                }

                using var keys = new ResourceKeyRing(new Dictionary<string, byte[]> { [keyId] = key });
                using Stream decoded = new AesResourceDecryptionServices(keys).OpenRead(new ResourceFileLocation(cache + ".data"), stored);
                using var sha = SHA256.Create();
                if (BitConverter.ToString(sha.ComputeHash(decoded)).Replace("-", "").ToLowerInvariant() != file.Sha256) {
                    return false;
                }

                File.Copy(cache + ".data", destination, true);
                file.UnencryptedSize = file.Size; file.UnencryptedSha256 = file.Sha256;
                file.Size = stored.Size; file.Sha256 = stored.Sha256;
                file.Encryption = stored.Encryption; file.EncryptionKeyId = stored.EncryptionKeyId;
                return true;
            }
            catch (Exception error) when (error is IOException || error is CryptographicException || error is ArgumentException) { return false; }
        }
    }
}

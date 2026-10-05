using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using UnityEngine;

namespace ZRAsset.Editor
{
    /// <summary>
    /// 离线发布签名工具。私钥使用 RSAKeyValue XML，必须由外部密钥管理流程提供并放在 Unity 项目外。
    /// 工具不生成生产私钥，不把私钥复制进 Assets、构建目录或日志；发布物只含签名和公开字段。
    /// </summary>
    public static class ResourceReleaseSigner
    {
        /// <summary>签名内存中的发布载荷；调用方负责 RSA 对象和私钥的安全存储。</summary>
        public static string Sign(ResourceSignedReleasePayload payload, string keyId, RSA privateKey)
        {
            if (payload == null) {
                throw new ArgumentNullException(nameof(payload));
            }

            if (privateKey == null) {
                throw new ArgumentNullException(nameof(privateKey));
            }

            var bytes = new UTF8Encoding(false, true).GetBytes(JsonUtility.ToJson(payload));
            var input = ResourceReleaseAuthentication.CreateSigningInput(keyId, bytes);
            var envelope = new ResourceSignedReleaseEnvelope
            {
                KeyId = keyId,
                PayloadBase64 = Convert.ToBase64String(bytes),
                SignatureBase64 = Convert.ToBase64String(privateKey.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            };
            var json = JsonUtility.ToJson(envelope, true);
            // 发布前自验，阻止错平台字段、过期载荷、弱密钥和不支持的格式进入发布目录。
            ResourceReleaseAuthentication.Verify(json, new ResourceReleaseTrustOptions(
                new[] { ResourceReleasePublicKey.FromRsa(keyId, privateKey) }), DateTimeOffset.UtcNow);
            return json;
        }

        /// <summary>读取外部私钥签名，返回可预置进客户端的公钥描述。不会输出私钥内容或原始密钥异常。</summary>
        public static ResourceReleasePublicKey SignFile(string payloadPath, string privateKeyPath, string keyId,
            string outputPath)
        {
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var privatePath = Path.GetFullPath(privateKeyPath);
            if (IsWithin(privatePath, project)) {
                throw new InvalidOperationException("签名私钥必须保存在 Unity 项目目录之外。");
            }

            RejectLinks(privatePath);
            var output = ValidateOutput(outputPath);
            if (string.Equals(privatePath, output, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException("发布输出不能覆盖私钥文件。");
            }

            var input = Path.GetFullPath(payloadPath);
            if (new FileInfo(input).Length > ResourceReleaseAuthentication.MaximumPayloadBytes) {
                throw new InvalidDataException("发布载荷文件超出长度限制。");
            }

            ResourceSignedReleasePayload payload = JsonUtility.FromJson<ResourceSignedReleasePayload>(File.ReadAllText(input, new UTF8Encoding(false, true)));
            using (RSA rsa = ReadPrivateKey(privatePath)) {
                var signed = Sign(payload, keyId, rsa);
                WriteOutput(output, signed);
                return ResourceReleasePublicKey.FromRsa(keyId, rsa);
            }
        }

        /// <summary>
        /// CI: -executeMethod ZRAsset.Editor.ResourceReleaseSigner.SignFromCommandLine
        /// -zrRelease 载荷JSON -zrKeyId 公钥ID -zrOutput 签名发布JSON [-zrPublicKeyOutput 公钥JSON]。
        /// 私钥路径只从 ZRASSET_SIGNING_KEY_FILE 环境变量读取，不把私钥正文放进命令行参数。
        /// </summary>
        public static void SignFromCommandLine()
        {
            var privatePath = Environment.GetEnvironmentVariable("ZRASSET_SIGNING_KEY_FILE");
            if (string.IsNullOrWhiteSpace(privatePath)) {
                throw new InvalidOperationException("必须配置 ZRASSET_SIGNING_KEY_FILE 环境变量。");
            }

            var publicOutput = Argument("-zrPublicKeyOutput", false);
            if (publicOutput != null) {
                publicOutput = ValidateOutput(publicOutput);
                if (string.Equals(publicOutput, Path.GetFullPath(privatePath), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(publicOutput, Path.GetFullPath(Argument("-zrOutput")), StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidOperationException("公钥、签名和私钥输出路径不能重叠。");
                }
            }
            ResourceReleasePublicKey key = SignFile(Argument("-zrRelease"), privatePath, Argument("-zrKeyId"), Argument("-zrOutput"));
            if (publicOutput != null) {
                WriteOutput(publicOutput, JsonUtility.ToJson(key, true));
            }

            Debug.Log("ZRAsset 发布签名与自验完成。");
        }

        private static RSA ReadPrivateKey(string path)
        {
            var parts = new List<byte[]>();
            RSA rsa = null;
            try {
                using (FileStream stream = File.OpenRead(path)) {
                    if (stream.Length > 64 * 1024) {
                        throw new InvalidDataException();
                    }

                    var settings = new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = 64 * 1024
                    };
                    var document = new XmlDocument { XmlResolver = null };
                    using (var reader = XmlReader.Create(stream, settings)) {
                        document.Load(reader);
                    }

                    if (document.DocumentElement?.Name != "RSAKeyValue") {
                        throw new InvalidDataException();
                    }

                    var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    foreach (XmlNode node in document.DocumentElement.ChildNodes) {
                        if (node.NodeType == XmlNodeType.Whitespace || node.NodeType == XmlNodeType.Comment) {
                            continue;
                        }

                        if (node.NodeType != XmlNodeType.Element || !values.TryAdd(node.Name, Convert.FromBase64String(node.InnerText))) {
                            throw new InvalidDataException();
                        }

                        parts.Add(values[node.Name]);
                    }
                    if (values.Count != 8) {
                        throw new InvalidDataException();
                    }

                    var parameters = new RSAParameters
                    {
                        Modulus = values["Modulus"],
                        Exponent = values["Exponent"],
                        P = values["P"],
                        Q = values["Q"],
                        DP = values["DP"],
                        DQ = values["DQ"],
                        InverseQ = values["InverseQ"],
                        D = values["D"]
                    };
                    rsa = RSA.Create();
                    rsa.ImportParameters(parameters);
                    return rsa;
                }
            }
            catch (Exception) {
                rsa?.Dispose();
                // 避免底层 XML 解析错误把私钥片段带入构建日志。
                throw new InvalidDataException("无法读取签名私钥；请检查外部 RSAKeyValue XML 文件和访问权限。");
            }
            finally {
                foreach (var part in parts) {
                    Array.Clear(part, 0, part.Length);
                }
            }
        }

        private static string Argument(string name, bool required = true)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++) {
                if (args[i] == name && !args[i + 1].StartsWith("-", StringComparison.Ordinal)) {
                    return args[i + 1];
                }
            }

            return required ? throw new ArgumentException("缺少发布参数 " + name + "。") : null;
        }

        private static string ValidateOutput(string path)
        {
            var full = Path.GetFullPath(path);
            if (IsWithin(full, Path.GetFullPath(Application.dataPath))) {
                throw new InvalidOperationException("签名发布输出不能写入 Assets。");
            }

            RejectLinks(full);
            return full;
        }

        private static bool IsWithin(string path, string root)
        {
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        private static void RejectLinks(string path)
        {
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) {
                    throw new IOException("签名文件路径不能经过符号链接或重解析点。");
                }
            }
        }

        private static void WriteOutput(string output, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var temporary = output + "." + Guid.NewGuid().ToString("N") + ".incoming";
            try {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    var bytes = new UTF8Encoding(false).GetBytes(contents);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(output)) {
                    File.Replace(temporary, output, null);
                }
                else {
                    File.Move(temporary, output);
                }
            }
            finally {
                if (File.Exists(temporary)) {
                    File.Delete(temporary);
                }
            }
        }
    }
}

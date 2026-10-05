using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ZRAsset.HotUpdate
{
    /// <summary>代码文件也通过普通 TextAsset 使用凭证读取；名称是程序集身份，不是文件路径。</summary>
    [Serializable]
    public class HotUpdateBinary
    {
        public string Name;
        public string Address;
        public string Sha256;
        public long Size;
    }

    [Serializable]
    public sealed class HotUpdateAssembly: HotUpdateBinary
    {
        // 这里只列本次发布中的热更依赖，宿主/AOT 引用由 Player 构建保证。
        public string[] Dependencies = Array.Empty<string>();
    }

    /// <summary>与资源版本一起发布的代码清单；PlayerBuildId 必须来自宿主内置配置，不能从远端反向赋值。</summary>
    [Serializable]
    public sealed class HotUpdateManifest
    {
        public int FormatVersion = 1;
        public string BuildTarget;
        public string PlayerBuildId;
        public string ContentVersion;
        public string EntryAssembly;
        public string EntryType;
        public string EntryMethod = "Run";
        public HotUpdateBinary[] AotMetadata = Array.Empty<HotUpdateBinary>();
        public HotUpdateAssembly[] Assemblies = Array.Empty<HotUpdateAssembly>();

        public static HotUpdateManifest FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) {
                throw new InvalidDataException("热更新清单为空。");
            }

            HotUpdateManifest manifest = JsonUtility.FromJson<HotUpdateManifest>(json) ?? throw new InvalidDataException("热更新清单为空。");
            manifest.Validate();
            return manifest;
        }

        public void Validate()
        {
            if (FormatVersion != 1 || AotMetadata == null || Assemblies == null || Assemblies.Length == 0) {
                throw new InvalidDataException("热更新清单版本或文件列表无效。");
            }

            if (AotMetadata.Length + Assemblies.Length > 256) {
                throw new InvalidDataException("单次启动的代码文件数量超过 256。");
            }

            RequireText(BuildTarget, "构建平台");
            RequireText(PlayerBuildId, "宿主构建标识");
            RequireText(ContentVersion, "代码版本");
            RequireText(EntryType, "入口类型");
            RequireText(EntryMethod, "入口方法");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            foreach (HotUpdateBinary file in AotMetadata) {
                ValidateBinary(file, names, addresses);
            }

            foreach (HotUpdateAssembly file in Assemblies) {
                ValidateBinary(file, names, addresses);
            }

            var hot = new Dictionary<string, HotUpdateAssembly>(StringComparer.Ordinal);
            foreach (HotUpdateAssembly assembly in Assemblies) {
                hot.Add(assembly.Name, assembly);
            }

            if (string.IsNullOrEmpty(EntryAssembly) || !hot.ContainsKey(EntryAssembly)) {
                throw new InvalidDataException("入口程序集不在本次热更新清单中。");
            }

            foreach (HotUpdateAssembly assembly in Assemblies) {
                if (assembly.Dependencies == null) {
                    throw new InvalidDataException("依赖列表不能为 null。");
                }

                var distinct = new HashSet<string>(StringComparer.Ordinal);
                foreach (var dependency in assembly.Dependencies) {
                    if (string.IsNullOrEmpty(dependency) || !hot.ContainsKey(dependency) || !distinct.Add(dependency)) {
                        throw new InvalidDataException($"程序集 {assembly.Name} 存在缺失或重复的热更依赖：{dependency}。");
                    }
                }
            }
            Sort(hot); // 环形依赖也必须在首次执行任何代码前拒绝。
        }

        public HotUpdateAssembly[] GetLoadOrder()
        {
            Validate();
            var hot = new Dictionary<string, HotUpdateAssembly>(StringComparer.Ordinal);
            foreach (HotUpdateAssembly assembly in Assemblies) {
                hot.Add(assembly.Name, assembly);
            }

            return Sort(hot);
        }

        private HotUpdateAssembly[] Sort(Dictionary<string, HotUpdateAssembly> hot)
        {
            var done = new HashSet<string>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<HotUpdateAssembly>();
            foreach (HotUpdateAssembly assembly in Assemblies) {
                Visit(assembly.Name);
            }

            return ordered.ToArray();

            void Visit(string name)
            {
                if (done.Contains(name)) {
                    return;
                }

                if (!visiting.Add(name)) {
                    throw new InvalidDataException("热更程序集依赖形成环：" + name);
                }

                HotUpdateAssembly assembly = hot[name];
                foreach (var dependency in assembly.Dependencies) {
                    Visit(dependency);
                }

                visiting.Remove(name);
                done.Add(name);
                ordered.Add(assembly);
            }
        }

        private static void ValidateBinary(HotUpdateBinary file, HashSet<string> names, HashSet<string> addresses)
        {
            if (file == null || !IsAssemblyName(file.Name) || !names.Add(file.Name)) {
                throw new InvalidDataException("程序集名称无效或重复（包含大小写冲突）。");
            }

            RequireText(file.Address, "代码资源地址");
            if (file.Address.Contains("\\")) {
                throw new InvalidDataException("代码资源地址不能包含反斜杠。");
            }

            foreach (var part in file.Address.Split('/')) {
                if (part.Length == 0 || part == "." || part == "..") {
                    throw new InvalidDataException("代码资源地址不能包含空段或目录跳转。");
                }
            }

            if (!addresses.Add(file.Address)) {
                throw new InvalidDataException("代码资源地址重复：" + file.Address);
            }

            if (file.Size <= 0 || file.Sha256 == null || file.Sha256.Length != 64) {
                throw new InvalidDataException("代码文件必须提供正数大小与 SHA-256：" + file.Name);
            }

            foreach (var digit in file.Sha256) {
                if (!Uri.IsHexDigit(digit)) {
                    throw new InvalidDataException("代码文件 SHA-256 格式无效：" + file.Name);
                }
            }
        }

        private static bool IsAssemblyName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 200 || name == "." || name == ".." ||
                name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            foreach (var c in name) {
                if (!char.IsLetterOrDigit(c) && c != '.' && c != '_' && c != '-') {
                    return false;
                }
            }

            return true;
        }

        private static void RequireText(string text, string field)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 1024 || text != text.Trim()) {
                throw new InvalidDataException(field + "不能为空、包含首尾空白或超过 1024 字符。");
            }

            foreach (var c in text) {
                if (char.IsControl(c)) {
                    throw new InvalidDataException(field + "包含控制字符。");
                }
            }
        }
    }
}

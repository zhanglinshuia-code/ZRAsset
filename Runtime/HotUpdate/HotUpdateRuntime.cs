using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;

namespace ZRAsset.HotUpdate
{
    /// <summary>隔离可选的脚本运行时；实现不得保留 DLL 输入字节，也不得把加载后的程序集当作可回收资源。</summary>
    public interface IHotUpdateRuntime
    {
        string Name { get; }
        bool RequiresAotMetadata { get; }
        void ValidateEnvironment();
        bool IsAssemblyLoaded(string name);
        void LoadMetadata(string name, byte[] image);
        object LoadAssembly(string expectedName, byte[] image);
        ResourceOperationBase InvokeEntryAsync(object assembly, string typeName, string methodName, ResourceManager resources);
    }

    /// <summary>可选 HybridCLR 适配器。只有已安装 HybridCLR 原生运行时的 IL2CPP Player 才能使用。</summary>
    public sealed class HybridClrHotUpdateRuntime: IHotUpdateRuntime
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "ValidateEnvironment initializes this field in IL2CPP player builds.")]
        private MethodInfo m_loadMetadata = null;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "ValidateEnvironment initializes this field in IL2CPP player builds.")]
        private object m_consistentMode = null;
        public string Name
        {
            get
            {
                return "HybridCLR IL2CPP";
            }
        }

        public bool RequiresAotMetadata
        {
            get
            {
                return true;
            }
        }

        public void ValidateEnvironment()
        {
#if UNITY_EDITOR || !ENABLE_IL2CPP
            throw new PlatformNotSupportedException("HybridCLR 生产运行时只允许在 IL2CPP Player 中使用；Editor/Mono 请显式选择验证或模拟运行时。");
#else
            if (m_loadMetadata != null) {
                return;
            }

            var api = Type.GetType("HybridCLR.RuntimeApi, HybridCLR.Runtime", false);
            var mode = Type.GetType("HybridCLR.HomologousImageMode, HybridCLR.Runtime", false);
            if (api == null || mode == null || !mode.IsEnum) {
                throw new InvalidOperationException("未找到 HybridCLR.Runtime。请安装锁定版本的 HybridCLR 包、运行 Installer/Generate，并重新构建 IL2CPP Player。");
            }

            MethodInfo method = api.GetMethod("LoadMetadataForAOTAssembly", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(byte[]), mode }, null);
            if (method == null || (!method.ReturnType.IsEnum && method.ReturnType != typeof(int))) {
                throw new MissingMethodException("HybridCLR 的 LoadMetadataForAOTAssembly 签名与本适配器不兼容。");
            }

            m_consistentMode = Enum.Parse(mode, "Consistent", false);
            m_loadMetadata = method;
            // 能找到托管 API 不代表原生 Installer 已正确安装；真实调用仍须检查异常和返回码。
#endif
        }

        public bool IsAssemblyLoaded(string name)
        {
            return HotUpdateRuntimeUtility.FindAssembly(name) != null;
        }

        public void LoadMetadata(string name, byte[] image)
        {
            ValidateEnvironment();
            HotUpdateAssemblyIdentity.RequireName(image, name);
            var result = HotUpdateRuntimeUtility.Invoke(m_loadMetadata, null, new[] { (object)image, m_consistentMode });
            var code = Convert.ToInt32(result);
            // 包括“已补充过”在内，非零状态均不能证明本次传入的元数据与已加载版本一致。
            if (code != 0) {
                throw new InvalidOperationException($"HybridCLR 补充 {name} 元数据失败：{result} ({code})。当前进程不能回滚，请重启后检查 Player/AOT 版本是否配套。");
            }
        }

        public object LoadAssembly(string expectedName, byte[] image)
        {
            ValidateEnvironment();
            return HotUpdateRuntimeUtility.LoadNewAssembly(expectedName, image);
        }

        public ResourceOperationBase InvokeEntryAsync(object assembly, string typeName, string methodName, ResourceManager resources)
        {
            return HotUpdateRuntimeUtility.InvokeEntryAsync(assembly, typeName, methodName, resources);
        }
    }

    /// <summary>
    /// Editor/Mono 的真实 Assembly.Load 验证器。它验证 DLL 身份和入口，但无法验证 HybridCLR AOT 泛型、桥接及裁剪。
    /// RequiresAotMetadata=false 不表示接受损坏的元数据：显式传入 LoadMetadata 时仍校验 PE 身份，只省去原生补充。
    /// </summary>
    public sealed class ManagedHotUpdateRuntime: IHotUpdateRuntime
    {
        public string Name
        {
            get
            {
                return "Managed Assembly.Load (Editor/Mono validation)";
            }
        }

        public bool RequiresAotMetadata
        {
            get
            {
                return false;
            }
        }

        public void ValidateEnvironment()
        {
#if !UNITY_EDITOR && ENABLE_IL2CPP
            throw new PlatformNotSupportedException("ManagedHotUpdateRuntime 仅用于 Editor/Mono 验证；IL2CPP Player 必须使用 HybridCLR 运行时。");
#endif
        }
        public bool IsAssemblyLoaded(string name)
        {
            return HotUpdateRuntimeUtility.FindAssembly(name) != null;
        }

        public void LoadMetadata(string name, byte[] image)
        {
            ValidateEnvironment();
            HotUpdateAssemblyIdentity.RequireName(image, name);
        }
        public object LoadAssembly(string expectedName, byte[] image)
        {
            ValidateEnvironment();
            return HotUpdateRuntimeUtility.LoadNewAssembly(expectedName, image);
        }
        public ResourceOperationBase InvokeEntryAsync(object assembly, string typeName, string methodName, ResourceManager resources)
        {
            return HotUpdateRuntimeUtility.InvokeEntryAsync(assembly, typeName, methodName, resources);
        }
    }

    /// <summary>
    /// 仅在 Editor 内复用已经由 Unity 加载的代码，不重复 Assembly.Load。
    /// 下载 DLL 的身份会校验，但不能证明 Editor 中正在执行的代码与其内容一致，因此不能用于发布验收。
    /// </summary>
    public sealed class EditorSimulationHotUpdateRuntime: IHotUpdateRuntime
    {
        public string Name
        {
            get
            {
                return "Editor loaded-assembly simulation";
            }
        }

        public bool RequiresAotMetadata
        {
            get
            {
                return false;
            }
        }

        public void ValidateEnvironment()
        {
#if !UNITY_EDITOR
            throw new PlatformNotSupportedException("EditorSimulationHotUpdateRuntime 只允许在 Unity Editor 中使用。");
#endif
        }
        // 模拟模式允许协调器请求一个已加载模块；真正查找由 LoadAssembly 完成，永远不加载第二份代码。
        public bool IsAssemblyLoaded(string name) { ValidateEnvironment(); return false; }
        public void LoadMetadata(string name, byte[] image)
        {
            ValidateEnvironment();
            HotUpdateAssemblyIdentity.RequireName(image, name);
        }
        public object LoadAssembly(string expectedName, byte[] image)
        {
            ValidateEnvironment();
            HotUpdateAssemblyIdentity.RequireName(image, expectedName);
            return HotUpdateRuntimeUtility.FindAssembly(expectedName)
                ?? throw new InvalidOperationException($"Editor 尚未加载程序集 {expectedName}。模拟模式不会执行下载的 DLL。");
        }
        public ResourceOperationBase InvokeEntryAsync(object assembly, string typeName, string methodName, ResourceManager resources)
        {
            ValidateEnvironment();
            return HotUpdateRuntimeUtility.InvokeEntryAsync(assembly, typeName, methodName, resources);
        }
    }

    internal static class HotUpdateRuntimeUtility
    {
        internal static Assembly FindAssembly(string name)
        {
            HotUpdateAssemblyIdentity.ValidateExpectedName(name);
            Assembly match = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                if (!string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal)) {
                    continue;
                }

                if (match != null) {
                    throw new InvalidOperationException($"当前进程已包含多个同名程序集 {name}，无法可靠选择代码版本，请重启。");
                }

                match = assembly;
            }
            return match;
        }

        internal static Assembly LoadNewAssembly(string expectedName, byte[] image)
        {
            // Assembly.Load 可能执行模块初始化器，因此必须在调用之前解析身份，不能先加载后验证。
            HotUpdateAssemblyIdentity.RequireName(image, expectedName);
            if (FindAssembly(expectedName) != null) {
                throw new InvalidOperationException($"程序集 {expectedName} 已加载；本版本不支持同一进程内覆盖或重复加载，请重启。");
            }

            var assembly = Assembly.Load(image);
            return !string.Equals(assembly.GetName().Name, expectedName, StringComparison.Ordinal)
                ? throw new InvalidOperationException($"运行时返回的程序集身份不匹配：预期 {expectedName}，实际 {assembly.GetName().Name}。请重启。")
                : assembly;
        }

        internal static async ResourceOperationBase InvokeEntryAsync(object loaded, string typeName, string methodName, ResourceManager resources)
        {
            if (!(loaded is Assembly assembly)) {
                throw new ArgumentException("入口对象必须是已加载的 Assembly。", nameof(loaded));
            }

            if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName)) {
                throw new ArgumentException("热更新入口类型和方法名不能为空。");
            }

            Type type = assembly.GetType(typeName, true, false);
            if (!type.IsVisible || type.ContainsGenericParameters) {
                throw new InvalidOperationException("热更新入口必须位于 public、非开放泛型类型中。");
            }

            MethodInfo entry = null;
            foreach (MethodInfo candidate in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal)) {
                    continue;
                }

                if (entry != null) {
                    throw new AmbiguousMatchException($"入口 {typeName}.{methodName} 存在重载；请为入口使用唯一方法名。");
                }

                entry = candidate;
            }
            if (entry == null) {
                throw new MissingMethodException(typeName, methodName);
            }

            if (entry.IsGenericMethod || entry.ContainsGenericParameters ||
                (entry.ReturnType != typeof(void) && entry.ReturnType != typeof(ResourceOperationBase))) {
                throw new InvalidOperationException("入口必须是非泛型 public static 方法，返回类型只能为 void 或 ResourceOperationBase。");
            }

            if (entry.ReturnType == typeof(void) && entry.IsDefined(typeof(AsyncStateMachineAttribute), false)) {
                throw new InvalidOperationException("热更新入口不能使用 async void；请返回 ResourceOperationBase 以便等待完成并报告异常。");
            }

            ParameterInfo[] parameters = entry.GetParameters();
            if (parameters.Length > 1 || (parameters.Length == 1 && parameters[0].ParameterType != typeof(ResourceManager))) {
                throw new InvalidOperationException("热更新入口仅支持无参，或一个 ResourceManager 参数。");
            }

            if (parameters.Length == 1 && resources == null) {
                throw new ArgumentNullException(nameof(resources));
            }

            var result = Invoke(entry, null, parameters.Length == 0 ? null : new object[] { resources });
            if (entry.ReturnType == typeof(ResourceOperationBase)) {
                if (!(result is ResourceOperationBase task)) {
                    throw new InvalidOperationException("热更新入口返回了 null ResourceOperationBase，无法确认启动结果。");
                }

                await task;
            }
        }

        internal static object Invoke(MethodInfo method, object target, object[] arguments)
        {
            try { return method.Invoke(target, arguments); }
            catch (TargetInvocationException exception) when (exception.InnerException != null) {
                // 保留业务异常类型与堆栈，不让反射包装吞掉实际原因。
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>
    /// 只读解析 PE/CLI 的 Assembly 表名称，不执行 DLL，也不依赖 UnityEditor/Cecil。
    /// 这只是加载前的身份检查，不替代完整程序集验证、可信清单校验或 CLR 自身校验。
    /// </summary>
    public static class HotUpdateAssemblyIdentity
    {
        public static string ReadName(byte[] image)
        {
            if (image == null) {
                throw new ArgumentNullException(nameof(image));
            }

            try { return new Reader(image).ReadName(); }
            catch (BadImageFormatException) { throw; }
            catch (Exception exception) when (exception is OverflowException || exception is DecoderFallbackException) { throw new BadImageFormatException("DLL 的 PE/CLI 身份数据无效。", exception); }
        }

        public static void RequireName(byte[] image, string expectedName)
        {
            ValidateExpectedName(expectedName);
            var actual = ReadName(image);
            if (!string.Equals(actual, expectedName, StringComparison.Ordinal)) {
                throw new BadImageFormatException($"DLL 身份不符：清单声明 {expectedName}，文件实际为 {actual}。");
            }
        }

        internal static void ValidateExpectedName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOfAny(new[] { '/', '\\', ',', '\0', '\r', '\n' }) >= 0) {
                throw new ArgumentException("程序集名称必须是无路径、无扩展名的简单名称。", nameof(name));
            }
        }

        private sealed class Reader
        {
            private readonly byte[] m_bytes;
            private readonly uint[] m_rows = new uint[64];
            private int m_sections, m_sectionCount, m_stringIndexSize, m_guidIndexSize, m_blobIndexSize;
            internal Reader(byte[] bytes) { m_bytes = bytes; }
            private void Range(long start, long size)
            {
                if (start < 0 || size < 0 || start > m_bytes.Length || size > m_bytes.Length - start) {
                    throw new BadImageFormatException("DLL 数据被截断或偏移越界。");
                }
            }
            private ushort U16(int at) { Range(at, 2); return (ushort)(m_bytes[at] | (m_bytes[at + 1] << 8)); }
            private uint U32(int at) { Range(at, 4); return (uint)(m_bytes[at] | (m_bytes[at + 1] << 8) | (m_bytes[at + 2] << 16) | (m_bytes[at + 3] << 24)); }
            private int Int(uint value)
            {
                return value > int.MaxValue ? throw new BadImageFormatException("DLL 偏移过大。") : (int)value;
            }
            private int Index(int table)
            {
                return m_rows[table] < 65536 ? 2 : 4;
            }

            private int Coded(int bits, params int[] tables)
            { uint max = 0; foreach (var table in tables) { max = Math.Max(max, m_rows[table]); } return max < (1u << (16 - bits)) ? 2 : 4; }
            private int Rva(uint rva, uint length)
            {
                for (var i = 0; i < m_sectionCount; i++) {
                    var at = checked(m_sections + (i * 40));
                    uint start = U32(at + 12), size = U32(at + 16), raw = U32(at + 20);
                    if (rva < start || (ulong)rva - start + length > size) {
                        continue;
                    }

                    var offset = (long)raw + rva - start;
                    Range(offset, length);
                    return checked((int)offset);
                }
                throw new BadImageFormatException("DLL 的 CLI 数据不在有效 PE 文件节内。");
            }

            internal string ReadName()
            {
                if (U16(0) != 0x5a4d) {
                    throw new BadImageFormatException("输入不是 PE DLL。");
                }

                var pe = Int(U32(0x3c));
                if (U32(pe) != 0x00004550) {
                    throw new BadImageFormatException("PE 签名无效。");
                }

                m_sectionCount = U16(checked(pe + 6));
                int optionalSize = U16(checked(pe + 20)), optional = checked(pe + 24);
                Range(optional, optionalSize);
                var magic = U16(optional);
                var directory = magic == 0x10b ? 96 : magic == 0x20b ? 112 : throw new BadImageFormatException("不支持的 PE 格式。");
                if (optionalSize < directory + (15 * 8) || U32(checked(optional + directory - 4)) < 15) {
                    throw new BadImageFormatException("PE 缺少 CLI 数据目录。");
                }

                m_sections = checked(optional + optionalSize);
                Range(m_sections, (long)m_sectionCount * 40);
                var cli = Rva(U32(checked(optional + directory + (14 * 8))), 16);
                var metadataSize = U32(cli + 12);
                var metadata = Rva(U32(cli + 8), metadataSize);
                if (metadataSize < 20 || U32(metadata) != 0x424a5342) {
                    throw new BadImageFormatException("CLI 元数据签名无效。");
                }

                var versionLength = Int(U32(metadata + 12));
                var streamHeader = checked(metadata + 16 + versionLength);
                Range(streamHeader, 4);
                if ((long)streamHeader + 4 > (long)metadata + metadataSize) {
                    throw new BadImageFormatException("CLI 版本字段超出元数据范围。");
                }

                int streamCount = U16(streamHeader + 2), cursor = streamHeader + 4;
                int tables = -1, strings = -1, tableLength = 0, stringsLength = 0;
                var metadataEnd = (long)metadata + metadataSize;
                for (var i = 0; i < streamCount; i++) {
                    Range(cursor, 8);
                    uint offset = U32(cursor), size = U32(cursor + 4);
                    if ((ulong)offset + size > metadataSize) {
                        throw new BadImageFormatException("CLI 流超出元数据范围。");
                    }

                    int nameStart = cursor + 8, nameEnd = nameStart;
                    while (nameEnd < metadataEnd && nameEnd - nameStart < 32 && m_bytes[nameEnd] != 0) {
                        nameEnd++;
                    }

                    if (nameEnd >= metadataEnd || nameEnd - nameStart >= 32) {
                        throw new BadImageFormatException("CLI 流名称无效。");
                    }

                    var name = Encoding.ASCII.GetString(m_bytes, nameStart, nameEnd - nameStart);
                    cursor = checked(nameStart + ((nameEnd - nameStart + 4) & ~3));
                    if (name == "#~" || name == "#-") { if (tables >= 0) { throw new BadImageFormatException("CLI 表流重复。"); } tables = checked(metadata + Int(offset)); tableLength = Int(size); }
                    if (name == "#Strings") { if (strings >= 0) { throw new BadImageFormatException("CLI 字符串流重复。"); } strings = checked(metadata + Int(offset)); stringsLength = Int(size); }
                }
                if (tables < 0 || strings < 0 || tableLength < 24) {
                    throw new BadImageFormatException("DLL 缺少 CLI 表或字符串。");
                }

                var heaps = m_bytes[tables + 6];
                m_stringIndexSize = (heaps & 1) == 0 ? 2 : 4;
                m_guidIndexSize = (heaps & 2) == 0 ? 2 : 4;
                m_blobIndexSize = (heaps & 4) == 0 ? 2 : 4;
                var valid = U32(tables + 8) | ((ulong)U32(tables + 12) << 32);
                cursor = tables + 24;
                for (var i = 0; i < 64; i++) {
                    if ((valid & (1ul << i)) != 0) { if ((long)cursor + 4 > (long)tables + tableLength) { throw new BadImageFormatException("CLI 行数表截断。"); } m_rows[i] = U32(cursor); cursor += 4; }
                }

                if ((heaps & 0x40) != 0) {
                    cursor = checked(cursor + 4);
                }

                if (m_rows[32] != 1) {
                    throw new BadImageFormatException("DLL 必须且只能包含一条 Assembly 身份记录。");
                }

                long assemblyAt = cursor;
                for (var i = 0; i < 32; i++) {
                    assemblyAt = checked(assemblyAt + ((long)m_rows[i] * RowSize(i)));
                }

                var assemblyEnd = assemblyAt + 16 + m_blobIndexSize + (2 * m_stringIndexSize);
                if (assemblyEnd > (long)tables + tableLength) {
                    throw new BadImageFormatException("Assembly 表被截断。");
                }

                var nameIndexAt = checked((int)assemblyAt + 16 + m_blobIndexSize);
                var nameIndex = m_stringIndexSize == 2 ? U16(nameIndexAt) : U32(nameIndexAt);
                if (nameIndex == 0 || nameIndex >= stringsLength) {
                    throw new BadImageFormatException("Assembly 名称索引无效。");
                }

                int begin = checked(strings + Int(nameIndex)), end = begin;
                while (end < (long)strings + stringsLength && m_bytes[end] != 0) {
                    end++;
                }

                if (end == (long)strings + stringsLength) {
                    throw new BadImageFormatException("Assembly 名称未终止。");
                }

                var result = new UTF8Encoding(false, true).GetString(m_bytes, begin, end - begin);
                return string.IsNullOrWhiteSpace(result) ? throw new BadImageFormatException("Assembly 名称为空。") : result;
            }

            private int RowSize(int table)
            {
                int s = m_stringIndexSize, b = m_blobIndexSize;
                switch (table) {
                    case 0: return 2 + s + (3 * m_guidIndexSize);
                    case 1: return Coded(2, 0, 26, 35, 1) + (2 * s);
                    case 2: return 4 + (2 * s) + Coded(2, 2, 1, 27) + Index(4) + Index(6);
                    case 3: return Index(4);
                    case 4: return 2 + s + b;
                    case 5: return Index(6);
                    case 6: return 8 + s + b + Index(8);
                    case 7: return Index(8);
                    case 8: return 4 + s;
                    case 9: return Index(2) + Coded(2, 2, 1, 27);
                    case 10: return Coded(3, 2, 1, 26, 6, 27) + s + b;
                    case 11: return 2 + Coded(2, 4, 8, 23) + b;
                    case 12: return Coded(5, 6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43) + Coded(3, 6, 10) + b;
                    case 13: return Coded(1, 4, 8) + b;
                    case 14: return 2 + Coded(2, 2, 6, 32) + b;
                    case 15: return 6 + Index(2);
                    case 16: return 4 + Index(4);
                    case 17: return b;
                    case 18: return Index(2) + Index(20);
                    case 19: return Index(20);
                    case 20: return 2 + s + Coded(2, 2, 1, 27);
                    case 21: return Index(2) + Index(23);
                    case 22: return Index(23);
                    case 23: return 2 + s + b;
                    case 24: return 2 + Index(6) + Coded(1, 20, 23);
                    case 25: return Index(2) + (2 * Coded(1, 6, 10));
                    case 26: return s;
                    case 27: return b;
                    case 28: return 2 + Coded(1, 4, 6) + s + Index(26);
                    case 29: return 4 + Index(4);
                    case 30: return 8;
                    case 31: return 4;
                    default: throw new BadImageFormatException("不支持的 CLI 表。");
                }
            }
        }
    }
}

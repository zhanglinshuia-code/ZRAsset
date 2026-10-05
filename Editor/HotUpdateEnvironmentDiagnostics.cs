using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using Debug = UnityEngine.Debug;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace ZRAsset.Editor
{
    /// <summary>只读显示可选 HybridCLR 接入条件，不安装包、不修改构建设置或 Unity 工具链。</summary>
    public static class HotUpdateEnvironmentDiagnostics
    {
        /// <summary>静态就绪性数据；绿色检查项仍不能替代最终 Player 的实际运行验证。</summary>
        public sealed class Readiness
        {
            public string PackageVersion, LockRevision, NativeVersion, Backend;
            public bool ManifestPinned, NativePresent, PluginEnabled, UsesGlobalIl2Cpp;
            public string[] HotAssemblies = Array.Empty<string>();
            public string[] MissingRequirements = Array.Empty<string>();
            public bool StaticRequirementsMet
            {
                get
                {
                    return MissingRequirements.Length == 0;
                }
            }
        }

        public static Readiness Inspect()
        {
            var result = new Readiness();
            var missing = new List<string>();
            var root = Directory.GetParent(Application.dataPath).FullName;
            PackageInfo package = null;
            foreach (PackageInfo item in PackageInfo.GetAllRegisteredPackages()) {
                if (item.name == "com.code-philosophy.hybridclr") { package = item; break; }
            }

            result.PackageVersion = package?.version;
            if (package == null) {
                missing.Add("未安装 HybridCLR 包。");
            }

            var manifestPath = Path.Combine(root, "Packages", "manifest.json");
            if (File.Exists(manifestPath)) {
                Match match = Regex.Match(File.ReadAllText(manifestPath), "\"com\\.code-philosophy\\.hybridclr\"\\s*:\\s*\"([^\"]+)\"");
                if (match.Success) {
                    var source = match.Groups[1].Value;
                    result.ManifestPinned = Regex.IsMatch(source, "#([0-9a-fA-F]{40}|v?\\d+\\.\\d+\\.\\d+([+-][A-Za-z0-9.-]+)?)$") ||
                        Regex.IsMatch(source, "^\\d+\\.\\d+\\.\\d+([+-].+)?$");
                }
            }
            var lockPath = Path.Combine(root, "Packages", "packages-lock.json");
            if (File.Exists(lockPath) && package != null && package.source == UnityEditor.PackageManager.PackageSource.Git) {
                Match match = Regex.Match(File.ReadAllText(lockPath), "\"com\\.code-philosophy\\.hybridclr\"\\s*:\\s*\\{[\\s\\S]*?\"hash\"\\s*:\\s*\"([^\"]+)\"");
                if (match.Success) {
                    result.LockRevision = match.Groups[1].Value;
                }
            }
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            result.Backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.FromBuildTargetGroup(group)).ToString();
            if (result.Backend != "IL2CPP") {
                missing.Add("当前构建目标尚未使用 IL2CPP。");
            }

            if (!BuildPipeline.IsBuildTargetSupported(group, target)) {
                missing.Add("当前目标的 Unity 构建模块不可用。");
            }

            var native = Path.Combine(root, "HybridCLRData", "LocalIl2CppData-" + Application.platform, "il2cpp", "libil2cpp", "hybridclr");
            result.NativePresent = Directory.Exists(native);
            var versionFile = Path.Combine(native, "generated", "libil2cpp-version.txt");
            if (File.Exists(versionFile)) {
                result.NativeVersion = File.ReadAllText(versionFile).Trim().Trim('\uFEFF');
            }

            Type type = FindOptionalType("HybridCLR.Editor.Settings.HybridCLRSettings");
            var settingsPath = Path.Combine(root, "ProjectSettings", "HybridCLRSettings.asset");
            if (type != null && File.Exists(settingsPath)) {
                try {
                    var settings = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    result.PluginEnabled = type.GetField("enable")?.GetValue(settings) is true;
                    result.UsesGlobalIl2Cpp = type.GetField("useGlobalIl2cpp")?.GetValue(settings) is true;
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    if (type.GetField("hotUpdateAssemblyDefinitions")?.GetValue(settings) is IEnumerable definitions) {
                        foreach (var value in definitions) {
                            if (value is UnityEngine.Object asset && asset != null) {
                                var path = AssetDatabase.GetAssetPath(asset);
                                if (!File.Exists(path)) {
                                    continue;
                                }

                                Match match = Regex.Match(File.ReadAllText(path), "\"name\"\\s*:\\s*\"([^\"]+)\"");
                                if (match.Success) {
                                    names.Add(match.Groups[1].Value);
                                }
                            }
                        }
                    }

                    if (type.GetField("hotUpdateAssemblies")?.GetValue(settings) is string[] direct) {
                        foreach (var name in direct) {
                            if (!string.IsNullOrWhiteSpace(name)) {
                                names.Add(name);
                            }
                        }
                    }

                    result.HotAssemblies = new List<string>(names).ToArray();
                    Array.Sort(result.HotAssemblies, StringComparer.Ordinal);
                }
                catch (Exception exception) { missing.Add("无法读取 HybridCLR 设置：" + exception.GetBaseException().Message); }
            }
            if (!result.PluginEnabled) {
                missing.Add("HybridCLR 未启用或缺少项目设置。");
            }

            if (result.UsesGlobalIl2Cpp) {
                missing.Add("当前选择全局 IL2CPP，本工具不验证或修改全局安装；请由构建环境确认。");
            }
            else {
                if (!result.NativePresent) {
                    missing.Add("项目内 HybridCLR 原生运行时尚未安装。");
                }

                if (result.NativeVersion != result.PackageVersion || string.IsNullOrEmpty(result.NativeVersion)) {
                    missing.Add("项目内原生版本标记与 UPM 包版本不一致或无法读取。");
                }
            }
            if (result.HotAssemblies.Length == 0) {
                missing.Add("尚未登记业务热更程序集；请选择已有业务 asmdef。");
            }

            result.MissingRequirements = missing.ToArray();
            return result;
        }

        [MenuItem("Tools/ZRAsset/热更新/检查 HybridCLR 环境")]
        public static void PrintReport()
        {
            Debug.Log(GetReport());
        }

        /// <summary>可在构建前调用；报告中的“存在”只是静态检查，不代表 IL2CPP Player 已验证。</summary>
        public static string GetReport()
        {
            var result = new StringBuilder("ZRAsset V9 HybridCLR 环境与接入诊断\n");
            Readiness readiness = Inspect();
            result.AppendLine(readiness.StaticRequirementsMet ? "静态要求通过；仍需 Generate/All 和真实 Player 验证。" : "尚未具备全部静态启动条件：");
            foreach (var item in readiness.MissingRequirements) {
                result.AppendLine("- " + item);
            }

            result.AppendLine($"包版本: {readiness.PackageVersion ?? "未发现"}；lock 提交: {readiness.LockRevision ?? "无 git 提交记录"}");
            result.AppendLine($"manifest 固定版本/提交: {readiness.ManifestPinned}；原生安装版本标记: {readiness.NativeVersion ?? "缺少"}");
            if (!readiness.ManifestPinned) {
                result.AppendLine("当前 manifest 未指定版本/提交；lock 当前解析结果不等于下次重新解析仍使用同一版本。");
            }

            result.AppendLine("已登记热更程序集: " + (readiness.HotAssemblies.Length == 0 ? "无" : string.Join(", ", readiness.HotAssemblies)));
            result.AppendLine($"HybridCLR enabled: {readiness.PluginEnabled}；使用全局 IL2CPP: {readiness.UsesGlobalIl2Cpp}");
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            result.AppendLine($"Unity: {Application.unityVersion}");
            result.AppendLine($"Active target: {target}");
            result.AppendLine($"Scripting backend: {PlayerSettings.GetScriptingBackend(NamedBuildTarget.FromBuildTargetGroup(group))}");
            result.AppendLine($"Build support: {BuildPipeline.IsBuildTargetSupported(group, target)}");
            Type api = FindType("HybridCLR.RuntimeApi");
            result.AppendLine($"HybridCLR RuntimeApi: {(api == null ? "未发现" : api.Assembly.GetName().Name)}");
            PackageInfo package = null;
            foreach (PackageInfo candidate in PackageInfo.GetAllRegisteredPackages()) {
                if (candidate.name == "com.code-philosophy.hybridclr") { package = candidate; break; }
            }

            result.AppendLine($"HybridCLR package: {(package == null ? "未安装" : package.version + " @ " + package.resolvedPath)}");

            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var localIl2cpp = Path.Combine(projectRoot, "HybridCLRData", "LocalIl2CppData-" + Application.platform, "il2cpp");
            AppendPath(result, "HybridCLR local IL2CPP", localIl2cpp);
            AppendPath(result, "HybridCLR native source", Path.Combine(localIl2cpp, "libil2cpp", "hybridclr"));
            AppendPath(result, "HybridCLR generated bridge", Path.Combine(localIl2cpp, "libil2cpp", "hybridclr", "generated"));
            // 尊重插件内配置的自定义目录；没有插件时显示约定路径以便用户准备构建产物。
            Type settings = FindType("HybridCLR.Editor.SettingsUtil");
            AppendPath(result, "Hot DLL output", ResolveOutput(settings, "GetHotUpdateDllsOutputDirByTarget", target,
                Path.Combine(projectRoot, "HybridCLRData", "HotUpdateDlls", target.ToString()), projectRoot));
            AppendPath(result, "Trimmed AOT archive", ResolveOutput(settings, "GetAssembliesPostIl2CppStripDir", target,
                Path.Combine(projectRoot, "HybridCLRData", "AssembliesPostIl2CppStrip", target.ToString()), projectRoot));
            result.AppendLine("AOT 归档必须与已发布 Player 配套；目录存在不证明版本匹配。");

            var windowsSupport = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines", "windowsstandalonesupport", "Variations");
            var windowsIl2cpp = false;
            if (Directory.Exists(windowsSupport)) {
                foreach (var path in Directory.GetDirectories(windowsSupport)) {
                    if (Path.GetFileName(path).IndexOf("il2cpp", StringComparison.OrdinalIgnoreCase) >= 0) { windowsIl2cpp = true; break; }
                }
            }

            result.AppendLine($"Windows IL2CPP module: {windowsIl2cpp}");
            if (Application.platform == RuntimePlatform.WindowsEditor) {
                result.AppendLine("Windows C++ toolchain: " + DescribeWindowsToolchain());
            }
            else {
                result.AppendLine("Native toolchain: 请在目标平台构建机检查 Xcode/Clang/SDK；本诊断不执行安装。");
            }

            result.AppendLine("需要完成 Installer、Generate/All 和真实 IL2CPP Player 验证；Editor/Mono 通过仅证明托管加载流程。");
            return result.ToString();
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                Type type = assembly.GetType(fullName, false);
                if (type != null) {
                    return type;
                }
            }
            return null;
        }

        internal static Type FindOptionalType(string fullName)
        {
            return FindType(fullName);
        }

        private static string ResolveOutput(Type settings, string methodName, BuildTarget target, string fallback, string projectRoot)
        {
            MethodInfo method = settings?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(BuildTarget) }, null);
            if (method == null) {
                return fallback;
            }

            try {
                var value = method.Invoke(null, new object[] { target }) as string;
                return string.IsNullOrEmpty(value) ? fallback : Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(projectRoot, value));
            }
            catch (Exception exception) {
                // 诊断插件版本不兼容时仍展示其余字段，不修改配置以“修复”问题。
                return fallback + " (插件路径查询失败: " + exception.GetBaseException().Message + ")";
            }
        }

        private static void AppendPath(StringBuilder report, string label, string path)
        {
            report.AppendLine($"{label}: {path} [{(Directory.Exists(path) || File.Exists(path) ? "存在" : "缺少")}]");
        }

        private static string DescribeWindowsToolchain()
        {
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vswhere)) {
                return "未找到 vswhere；无法确认 Visual C++ Build Tools";
            }

            try {
                // vswhere 只查询安装清单，隐藏窗口且禁止 shell，固定参数不接受项目内容。
                using var process = Process.Start(new ProcessStartInfo(vswhere,
                    "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true });
                return process == null ? "vswhere 无法启动" : ReadToolchain(process);
            }
            catch (Exception exception) { return "查询失败: " + exception.Message; }
        }

        private static string ReadToolchain(Process process)
        {
            Task<string> read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000)) {
                return "vswhere 查询超时，工具链状态未知";
            }

            var path = read.GetAwaiter().GetResult().Trim();
            return process.ExitCode != 0
                ? "vswhere 返回错误 " + process.ExitCode
                : string.IsNullOrEmpty(path) ? "未发现含 x86/x64 C++ 工具的 Visual Studio 安装" : path + " (发现 C++ 工具；SDK 链接能力仍需实际构建)";
        }
    }
}

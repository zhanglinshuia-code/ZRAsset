using System;
using UnityEditor;

namespace ZRAsset.Editor
{
    /// <summary>可选 Editor 后端注册。核心程序集不引用 SBP；未安装时 Built-in 仍可独立构建。</summary>
    public static class ResourceBuildBackends
    {
        private static Func<ResourceBuildPlan, string, BuildTarget, BundleInfo[]> s_sbp;

        public static bool IsSbpAvailable
        {
            get
            {
                return s_sbp != null;
            }
        }

        public static string SbpVersion { get; private set; }

        public static void RegisterSbp(string version, Func<ResourceBuildPlan, string, BuildTarget, BundleInfo[]> build)
        {
            s_sbp = build ?? throw new ArgumentNullException(nameof(build));
            SbpVersion = version;
        }

        internal static BundleInfo[] BuildSbp(ResourceBuildPlan plan, string stage, BuildTarget target)
        {
            return s_sbp == null
                ? throw new InvalidOperationException("SBP 后端未安装。请安装 com.unity.scriptablebuildpipeline 2.6.1 或兼容版本，或选择 BuiltIn。")
                : s_sbp(plan, stage, target);
        }
    }
}

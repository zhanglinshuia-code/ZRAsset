using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

namespace ZRAsset.Editor
{
    [InitializeOnLoad]
    internal static class SbpBuildBackend
    {
        static SbpBuildBackend()
        {
            ResourceBuildBackends.RegisterSbp(UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ContentPipeline).Assembly)?.version, Build);
        }

        private static BundleInfo[] Build(ResourceBuildPlan plan, string stage, BuildTarget target)
        {
            var parameters = new BundleBuildParameters(target, BuildPipeline.GetBuildTargetGroup(target), stage)
            {
                BundleCompression = plan.Report.Compression switch
                {
                    ResourceBundleCompression.Lz4 => BuildCompression.LZ4,
                    ResourceBundleCompression.Uncompressed => BuildCompression.Uncompressed,
                    _ => BuildCompression.LZMA
                },
                UseCache = !plan.Report.ForceRebuild,
                CacheServerHost = plan.Report.CacheServerHost,
                CacheServerPort = plan.Report.CacheServerPort,
                TempOutputFolder = Path.Combine(Path.GetDirectoryName(stage), "sbp-temp"),
                ScriptOutputFolder = Path.GetFullPath(Path.Combine("Library", "ZRAssetSbpScripts", target.ToString())),
                AppendHash = false,
                DisableVisibleSubAssetRepresentations = false,
                WriteLinkXML = plan.Report.GenerateLinkXml
            };
            if (plan.Report.StripUnityVersion) {
                parameters.ContentBuildFlags |= UnityEditor.Build.Content.ContentBuildFlags.StripUnityVersion;
            }

            if (plan.Report.DisableWriteTypeTree) {
                parameters.ContentBuildFlags |= UnityEditor.Build.Content.ContentBuildFlags.DisableWriteTypeTree;
            }

            plan.Report.SbpCacheEnabled = parameters.UseCache;
            ReturnCode code = ContentPipeline.BuildAssetBundles(parameters, new BundleBuildContent(plan.Builds), out IBundleBuildResults result);
            if (code == ReturnCode.Canceled) {
                throw new OperationCanceledException("SBP 构建已取消，旧发布产物保持不变。");
            }

            if (code < ReturnCode.Success || result == null) {
                throw new InvalidOperationException("SBP 构建失败：" + code + "。请检查未保存场景、平台模块和 Console。");
            }

            if (parameters.WriteLinkXML) {
                if (!File.Exists(Path.Combine(stage, "link.xml"))) {
                    throw new InvalidDataException("可编程构建管线未生成所需的 link.xml。");
                }

                plan.Report.LinkXmlFile = "link.xml";
            }
            const string logName = "sbp-buildlog.json";
            var log = Path.Combine(stage, "buildlogtep.json");
            if (File.Exists(log)) { File.Move(log, Path.Combine(stage, logName)); plan.Report.NativeBuildLog = logName; }
            return result.BundleInfos.OrderBy(p => p.Key, StringComparer.Ordinal).Select(pair =>
            {
                if (!string.Equals(Path.GetFullPath(pair.Value.FileName), Path.GetFullPath(Path.Combine(stage, pair.Key)), StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("SBP 返回了预期目录之外的文件：" + pair.Key);
                }
                // SBP 2.6 返回完整依赖闭包；保留真实结果，不能替换为源资源预测。
                return new BundleInfo
                {
                    Name = pair.Key,
                    Crc = pair.Value.Crc,
                    Hash = pair.Value.Hash.ToString(),
                    Dependencies = (pair.Value.Dependencies ?? Array.Empty<string>()).OrderBy(n => n, StringComparer.Ordinal).ToArray()
                };
            }).ToArray();
        }

    }
}

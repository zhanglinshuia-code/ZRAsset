using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
using UnityEngine;

namespace ZRAsset.Editor
{
    /// <summary>Connects deployed SBP type metadata to the Player's managed-code linker.</summary>
    public static class ResourceLinker
    {
        [Serializable]
        private sealed class Registration
        {
            public string BuildDirectory;
            public string BuildTarget;
            public string ManifestHash;
            public string LinkXmlHash;
        }

        /// <summary>Register remote-only content after building it, before building its host Player.</summary>
        public static void RegisterBuild(string buildDirectory, BuildTarget target)
        {
            var directory = Path.GetFullPath(buildDirectory);
            var manifest = ResourceManifest.FromJson(File.ReadAllText(Path.Combine(directory, "manifest.json")));
            if (manifest.BuildTarget != target.ToString()) {
                throw new InvalidDataException("防裁剪登记平台与资源目标平台不一致。");
            }

            ReadLinkXml(Path.Combine(directory, "link.xml"));
            var registration = new Registration
            {
                BuildDirectory = directory,
                BuildTarget = target.ToString(),
                ManifestHash = BundleBuilder.ComputeSha256(Path.Combine(directory, "manifest.json")),
                LinkXmlHash = BundleBuilder.ComputeSha256(Path.Combine(directory, "link.xml"))
            };
            var registrationRoot = Path.Combine("Library", "ZRAssetLinker", target.ToString());
            Directory.CreateDirectory(registrationRoot);
            var packageName = string.IsNullOrEmpty(manifest.PackageName) ? "default" : manifest.PackageName;
            File.WriteAllText(Path.Combine(registrationRoot, packageName + ".json"), JsonUtility.ToJson(registration));
        }

        /// <summary>Remove a remote-only package that no longer belongs to this Player.</summary>
        public static void UnregisterBuild(string packageName, BuildTarget target)
        {
            if (!string.IsNullOrEmpty(packageName)) {
                ResourcePackageIdentity.ValidateName(packageName);
            }

            var path = Path.Combine("Library", "ZRAssetLinker", target.ToString(),
                (string.IsNullOrEmpty(packageName) ? "default" : packageName) + ".json");
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }

        internal static XElement ReadLinkXml(string path)
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16 * 1024 * 1024
            });
            XElement root = XDocument.Load(reader).Root;
            return root == null || root.Name != "linker" || root.Elements().Any(element => element.Name != "assembly")
                ? throw new InvalidDataException("资源防裁剪文档无效：" + path)
                : root;
        }

        public static string GeneratePlayerLinkXml(BuildTarget target, string streamingAssetsRoot = null)
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            var deployedRoot = streamingAssetsRoot ?? Path.Combine(Application.streamingAssetsPath, "ZRAsset");
            if (Directory.Exists(deployedRoot)) {
                foreach (var path in Directory.EnumerateFiles(deployedRoot, "link.xml", SearchOption.AllDirectories)) {
                    var manifestPath = Path.Combine(Path.GetDirectoryName(path), "manifest.json");
                    var manifest = ResourceManifest.FromJson(File.ReadAllText(manifestPath));
                    if (manifest.BuildTarget != target.ToString()) {
                        throw new BuildFailedException("已部署资源的目标平台不一致：" + manifestPath);
                    }

                    paths.Add(Path.GetFullPath(path));
                }
            }
            var registrationRoot = Path.Combine("Library", "ZRAssetLinker", target.ToString());
            if (Directory.Exists(registrationRoot)) {
                foreach (var path in Directory.EnumerateFiles(registrationRoot, "*.json")) {
                    Registration registration = JsonUtility.FromJson<Registration>(File.ReadAllText(path));
                    if (registration == null || registration.BuildTarget != target.ToString() || string.IsNullOrEmpty(registration.BuildDirectory)) {
                        throw new BuildFailedException("资源防裁剪登记无效：" + path);
                    }

                    var manifestPath = Path.Combine(registration.BuildDirectory, "manifest.json");
                    var linkPath = Path.Combine(registration.BuildDirectory, "link.xml");
                    if (!File.Exists(manifestPath) || !File.Exists(linkPath) ||
                        BundleBuilder.ComputeSha256(manifestPath) != registration.ManifestHash ||
                        BundleBuilder.ComputeSha256(linkPath) != registration.LinkXmlHash) {
                        throw new BuildFailedException("资源防裁剪登记已过期，请重新构建并登记资源包：" + registration.BuildDirectory);
                    }

                    paths.Add(linkPath);
                }
            }
            if (paths.Count == 0) {
                return null;
            }

            var root = new XElement("linker");
            var declarations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths) {
                foreach (XElement assembly in ReadLinkXml(path).Elements()) {
                    if (declarations.Add(assembly.ToString(SaveOptions.DisableFormatting))) {
                        root.Add(new XElement(assembly));
                    }
                }
            }
            var output = Path.GetFullPath(Path.Combine("Library", "ZRAssetLinker", target.ToString(), "merged.xml"));
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            new XDocument(root).Save(output);
            return output;
        }
    }

    internal sealed class ResourceLinkerProcessor: IUnityLinkerProcessor
    {
        public int callbackOrder
        {
            get
            {
                return 0;
            }
        }

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            return ResourceLinker.GeneratePlayerLinkXml(report.summary.platform);
        }
    }
}

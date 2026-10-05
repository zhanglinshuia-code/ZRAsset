using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace ZRAsset.Editor
{
    public enum ResourceBuildStage { PlanReady, FilesBuilt, FilesEncrypted, ManifestReady, QualityPassed, BeforePublish }

    public interface IResourceBuildTask
    {
        string Name { get; }
        ResourceBuildStage Stage { get; }
        void Execute(ResourceBuildContext context);
    }

    /// <summary>可存入构建配置的扩展。任务在隔离目录中运行；抛错或取消会阻止发布。</summary>
    public abstract class ResourceBuildExtension: ScriptableObject, IResourceBuildTask
    {
        public abstract string Name { get; }
        public abstract ResourceBuildStage Stage { get; }
        public abstract void Execute(ResourceBuildContext context);
    }

    /// <summary>本次构建独立的上下文；按 Stage、注册顺序运行，同一实例不允许重入构建。</summary>
    public sealed class ResourceBuildContext
    {
        private readonly Dictionary<string, object> m_items = new(StringComparer.Ordinal);
        private readonly HashSet<string> m_artifacts = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> m_completed = new();
        private readonly IResourceBuildTask[] m_tasks;
        public ResourceBuildPlan Plan { get; }
        public ResourceManifest Manifest { get; internal set; }
        public ResourceBuildManifestView ReadOnlyManifest { get; private set; }
        public string StagingDirectory { get; }
        public BuildTarget Target { get; }
        public CancellationToken CancellationToken { get; }
        public IDictionary<string, object> Items { get { return m_items; } }
        internal IEnumerable<string> Artifacts { get { return m_artifacts; } }
        internal bool HasTasks { get { return m_tasks.Length > 0; } }

        internal ResourceBuildContext(ResourceBuildPlan plan, string stage, BuildTarget target,
            IEnumerable<IResourceBuildTask> tasks, CancellationToken token)
        {
            Plan = plan; StagingDirectory = stage; Target = target; CancellationToken = token;
            m_tasks = (tasks ?? Array.Empty<IResourceBuildTask>()).ToArray();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (IResourceBuildTask task in m_tasks) {
                if (task == null || string.IsNullOrWhiteSpace(task.Name) || !names.Add(task.Name) || !Enum.IsDefined(typeof(ResourceBuildStage), task.Stage)) {
                    throw new ArgumentException("构建任务的名称必须非空且唯一，Stage 必须有效。", nameof(tasks));
                }
            }
        }

        /// <summary>登记需要发布的附加产物；仅支持独立文件名，不能覆盖框架产物或 Bundle。</summary>
        public void AddArtifact(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                fileName == "." || fileName == ".." || fileName.StartsWith("manifest.", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("build-report.json", StringComparison.OrdinalIgnoreCase) || fileName.Equals("link.xml", StringComparison.OrdinalIgnoreCase) ||
                Plan.Report.Bundles.Any(b => b.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))) {
                throw new ArgumentException("附加产物名称无效或与框架产物冲突。", nameof(fileName));
            }
            m_artifacts.Add(fileName);
        }

        internal void Run(ResourceBuildStage stage)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (HasTasks && stage >= ResourceBuildStage.QualityPassed) { ReadOnlyManifest ??= new ResourceBuildManifestView(Manifest); }
            foreach (IResourceBuildTask task in m_tasks) {
                if (task.Stage != stage) { continue; }
                task.Execute(this);
                CancellationToken.ThrowIfCancellationRequested();
                m_completed.Add(stage + ":" + task.Name);
            }
            EnsureFrozenUnchanged();
            Plan.Report.ExtensionSteps = m_completed.ToArray();
        }

        internal void EnsureFrozenUnchanged()
        {
            if (ReadOnlyManifest != null && !ReadOnlyManifest.Matches(Manifest)) {
                throw new InvalidOperationException("质量校验后不能修改清单；请在 ManifestReady 阶段修改并重新校验。");
            }
        }

        internal void ValidateArtifacts()
        {
            // Report fields are also exposed to tasks; reject invalid secondary outputs before publishing any file.
            foreach (var name in new[] { Plan.Report.NativeBuildLog, Plan.Report.LinkXmlFile }) {
                if (string.IsNullOrEmpty(name)) { continue; }
                if (name != Path.GetFileName(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name == "." || name == "..") {
                    throw new InvalidDataException("构建报告中的产物名称无效：" + name);
                }
                var file = new FileInfo(Path.Combine(StagingDirectory, name));
                if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) {
                    throw new InvalidDataException("构建报告中的产物缺失或是链接：" + name);
                }
            }
            foreach (var name in m_artifacts) {
                var file = new FileInfo(Path.Combine(StagingDirectory, name));
                if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) {
                    throw new InvalidDataException("附加产物缺失或是链接：" + name);
                }
                if (Manifest.Bundles.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(Plan.Report.NativeBuildLog, name, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("附加产物与构建输出冲突：" + name);
                }
            }
        }

        internal void ValidateFiles()
        {
            if (!HasTasks) { return; }
            Manifest.Validate();
            foreach (BundleInfo bundle in Manifest.Bundles) {
                CancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(StagingDirectory, bundle.Name);
                var file = new FileInfo(path);
                if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length != bundle.Size ||
                    !string.Equals(RawFileBuildPipeline.Hash(path), bundle.Sha256, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("扩展修改后的文件与清单不一致：" + bundle.Name);
                }
            }
        }
    }
}

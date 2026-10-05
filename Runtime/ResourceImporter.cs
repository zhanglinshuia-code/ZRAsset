using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ZRAsset
{
    /// <summary>外部已下载的完整容器。SourcePath 是本地路径，BundleName 必须存在于当前清单。</summary>
    public readonly struct ResourceImportFile
    {
        public string BundleName { get; }
        public string SourcePath { get; }
        public ResourceImportFile(string bundleName, string sourcePath)
        { BundleName = bundleName; SourcePath = sourcePath; }
    }

    /// <summary>可选导入能力。实现必须验证大小和内容哈希，不移动或修改调用方的源文件。</summary>
    public interface IResourceFileImporter
    {
        ResourceOperationBase<ResourceFileLocation> ImportAsync(BundleInfo info, string sourcePath,
            CancellationToken cancellationToken = default);
    }

    /// <summary>显式启动的批量导入；按文件提交，失败时保留已完成的正确文件，取消后排空在途操作。</summary>
    public sealed class ResourceImporter: ResourceFileBatchOperation
    {
        internal ResourceImporter(object owner, IEnumerable<BundleInfo> files,
            Func<BundleInfo, CancellationToken, ResourceOperationBase<ResourceFileLocation>> import, int concurrency)
            : base(owner, files, import, concurrency) { }
        public ResourceImporter StartImport() { StartFiles(); return this; }
    }

    public sealed partial class ResourceManager
    {
        /// <summary>仅导入显式列出的容器，不自动下载依赖；构造阶段验证清单映射，不读取文件内容。</summary>
        public ResourceImporter CreateImporter(IEnumerable<ResourceImportFile> files, int maxConcurrentFiles = 3)
        {
            CheckThread();
            if (m_closing) {
                throw new ObjectDisposedException(nameof(ResourceManager));
            }

            if (files == null) {
                throw new ArgumentNullException(nameof(files));
            }

            if (FileSystem is not IResourceFileImporter importer) {
                throw new NotSupportedException("当前文件系统不支持外部文件导入。");
            }

            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            var selected = new List<BundleInfo>();
            foreach (ResourceImportFile file in files) {
                if (file.BundleName == null || !m_catalog.TryGetValue(file.BundleName, out BundleInfo info)) {
                    throw new ArgumentException("导入的 Bundle 不在当前清单：" + file.BundleName, nameof(files));
                }

                if (string.IsNullOrWhiteSpace(file.SourcePath) || !Path.IsPathRooted(file.SourcePath)) {
                    throw new ArgumentException("导入源必须为本地绝对路径。", nameof(files));
                }

                var path = Path.GetFullPath(file.SourcePath);
                if (sources.TryGetValue(file.BundleName, out var previous)) {
                    if (!DownloadStorage.PathComparer.Equals(previous, path)) {
                        throw new ArgumentException("同一 Bundle 指定了不同导入源：" + file.BundleName, nameof(files));
                    }

                    continue;
                }
                BundleDownloadQueue.ValidateInfo(info);
                sources.Add(file.BundleName, path); selected.Add(info);
            }
            return new ResourceImporter(this, selected, (info, token) =>
            {
                CheckThread();
                return m_closing
                    ? throw new ObjectDisposedException(nameof(ResourceManager))
                    : TrackContentOperation(() => importer.ImportAsync(info, sources[info.Name], token));
            }, maxConcurrentFiles);
        }
    }
}

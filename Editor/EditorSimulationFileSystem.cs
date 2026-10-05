using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;

namespace ZRAsset.Editor
{
    public sealed class EditorSimulationOptions
    {
        public string CacheRoot { get; }
        public long BytesPerSecond { get; }
        public int MinimumFrames { get; }
        public bool VirtualWebGL { get; }
        public Func<string, int, bool> FailRequest { get; }
        public EditorSimulationOptions(string cacheRoot = null, long bytesPerSecond = 1024 * 1024, int minimumFrames = 2,
            bool virtualWebGL = false, Func<string, int, bool> failRequest = null)
        {
            if (bytesPerSecond <= 0 || minimumFrames < 1 || minimumFrames > 600) {
                throw new ArgumentOutOfRangeException();
            }

            CacheRoot = Path.GetFullPath(cacheRoot ?? "Library/ZRAssetSimulation"); BytesPerSecond = bytesPerSecond;
            MinimumFrames = minimumFrames; VirtualWebGL = virtualWebGL; FailRequest = failRequest;
        }
    }
    public sealed class EditorSimulationFileSystem: ResourceFileSystem, ISynchronousResourceFileSystem, IResourceFileInspector
    {
        private readonly EditorSimulationOptions m_options;
        private readonly Dictionary<string, string> m_paths;
        private readonly Dictionary<string, long> m_sizes;
        private readonly Dictionary<string, int> m_attempts = new();
        private bool m_running;
        public override ResourceFileCapabilities FileCapabilities
        {
            get
            {
                return m_options.VirtualWebGL ? ResourceFileCapabilities.None :
            ResourceFileCapabilities.SynchronousRead | ResourceFileCapabilities.RandomAccess | ResourceFileCapabilities.Persistent;
            }
        }

        public override bool SupportsDownloads
        {
            get
            {
                return true;
            }
        }

        public override bool SupportsPersistentCache
        {
            get
            {
                return true;
            }
        }

        internal EditorSimulationFileSystem(ResourceBuildPlan plan, EditorSimulationOptions options)
        {
            m_options = options;
            m_paths = plan.Report.Bundles.ToDictionary(b => b.Name, b => Path.Combine(options.CacheRoot,
                plan.Report.PackageName ?? "legacy", b.Name + "." + UnityEngine.Hash128.Compute(string.Join("|",
                    b.IncludedAssets.Select(p => p + ":" + AssetDatabase.GetAssetDependencyHash(p)))).ToString() + ".sim"));
            m_sizes = plan.Report.Bundles.ToDictionary(b => b.Name, b => b.SourceBytes);
        }
        private string PathFor(BundleInfo info)
        {
            return m_paths.TryGetValue(info.Name, out var path) ? path : throw new FileNotFoundException(info.Name);
        }

        protected override async ResourceOperationBase<ResourceFileLocation> ResolveCoreAsync(BundleInfo info, DownloadPriority priority, CancellationToken token)
        {
            var path = PathFor(info);
            while (m_running) { token.ThrowIfCancellationRequested(); await ResourceOperationBase.Yield(); }
            m_running = true;
            try {
                if (!File.Exists(path)) {
                    m_attempts.TryGetValue(info.Name, out var attempt); m_attempts[info.Name] = ++attempt;
                    for (var i = 0; i < m_options.MinimumFrames; i++) { token.ThrowIfCancellationRequested(); await ResourceOperationBase.Yield(); }
                    await ResourceOperationBase.Delay(TimeSpan.FromSeconds((double)m_sizes[info.Name] / m_options.BytesPerSecond), token);
                    if (m_options.FailRequest?.Invoke(info.Name, attempt) == true) {
                        throw new IOException("模拟下载失败：" + info.Name);
                    }

                    token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "ZRAsset simulation v1");
                }
                return new ResourceFileLocation(path);
            }
            finally { m_running = false; }
        }
        public ResourceFileLocation Resolve(BundleInfo info)
        {
            CheckAvailable();
            if (m_options.VirtualWebGL) {
                throw new NotSupportedException("虚拟 WebGL 模式禁用同步加载。");
            }

            var path = PathFor(info);
            return !File.Exists(path) ? throw new InvalidOperationException("请先完成模拟下载。") : new ResourceFileLocation(path);
        }
        public ResourceOperationBase<ResourcePreparationBundle> InspectAsync(BundleInfo info, CancellationToken cancellationToken = default)
        {
            CheckAvailable(); cancellationToken.ThrowIfCancellationRequested(); var path = PathFor(info);
            return ResourceOperationBase.FromResult(new ResourcePreparationBundle(info.Name, m_sizes[info.Name],
                File.Exists(path) ? ResourcePreparationSource.TargetCache : ResourcePreparationSource.Download, File.Exists(path) ? path : null));
        }
        public void ClearCache()
        {
            CheckAvailable();
            if (m_running) {
                throw new InvalidOperationException("模拟下载仍在进行。");
            }

            try {
                foreach (var path in m_paths.Values) {
                    if (File.Exists(path)) {
                        File.Delete(path);
                    }
                }
            }
            finally { m_running = false; }
        }
    }
}

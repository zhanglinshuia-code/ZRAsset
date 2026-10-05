using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>经本次文件校验确认的来源；Download 表示仍缺少完整文件，不代表最终网络流量。</summary>
    public enum ResourcePreparationSource { TargetCache, BuiltIn, CrossVersionCache, Download }

    public enum ResourcePreparationPhase { Planning, Preparing, Activating }

    /// <summary>按完成的 Bundle 统计；包括本地检查和复用，不等同于网络传输字节。</summary>
    public readonly struct ResourcePreparationProgress
    {
        public ResourcePreparationPhase Phase { get; }
        public int CompletedBundles { get; }
        public int TotalBundles { get; }
        public long CompletedBytes { get; }
        public long TotalBytes { get; }
        internal ResourcePreparationProgress(ResourcePreparationPhase phase, int completed, int total, long bytes, long totalBytes)
        { Phase = phase; CompletedBundles = completed; TotalBundles = total; CompletedBytes = bytes; TotalBytes = totalBytes; }
    }

    /// <summary>单个包的只读准备结果；SourcePath 仅用于展示来源，不能当作稍后仍有效的加载授权。</summary>
    public sealed class ResourcePreparationBundle
    {
        public string Name { get; }
        public long Bytes { get; }
        public ResourcePreparationSource Source { get; }
        public string SourcePath { get; }
        public ResourcePreparationBundle(string name, long bytes, ResourcePreparationSource source, string sourcePath = null)
        {
            if (!ResourceManifest.IsSafeBundleName(name)) {
                throw new ArgumentException("Invalid bundle name.", nameof(name));
            }

            if (bytes < 0) {
                throw new ArgumentOutOfRangeException(nameof(bytes));
            }

            if (!Enum.IsDefined(typeof(ResourcePreparationSource), source)) {
                throw new ArgumentOutOfRangeException(nameof(source));
            }

            Name = name; Bytes = bytes; Source = source; SourcePath = sourcePath;
        }
    }

    /// <summary>
    /// 准备计划保留私有清单快照，并只暴露副本和只读结果。
    /// 文件会在计划之后变化，因此 PrepareAsync 从不信任本对象替代新的校验。
    /// </summary>
    public sealed class ResourcePreparationPlan
    {
        private readonly ResourceManifest m_manifestSnapshot;
        public string Version { get; }
        public string BuildTarget { get; }
        public ResourceManifest Manifest
        {
            get
            {
                return m_manifestSnapshot.CopyUnchecked();
            }
        }

        public IReadOnlyList<ResourcePreparationBundle> Bundles { get; }
        public ResourceSelectionResult Selection { get; }
        public long TotalBytes { get; }
        public long TargetCacheBytes { get; }
        public long BuiltInBytes { get; }
        public long ReuseBytes { get; }
        /// <summary>当前仍缺少的完整文件字节数；断点续传、服务端响应和重试会改变实际网络传输量。</summary>
        public long DownloadBytes { get; }

        /// <summary>Returns an independent snapshot without a synchronous copy of a large manifest.</summary>
        public ResourceOperationBase<ResourceManifest> GetManifestAsync(CancellationToken cancellationToken = default)
        {
            return m_manifestSnapshot.CopySnapshotAsync(cancellationToken);
        }

        // Both callers hand over private validated snapshots. Share immutable state rather than cloning it again.
        internal static async ResourceOperationBase<ResourcePreparationPlan> CreateAsync(string version, ResourceManifest validated,
            List<ResourcePreparationBundle> bundles, ResourceSelectionResult selection, CancellationToken token)
        {
            ResourcePreparationPlan result = null;
            await ResourceManifestWork.RunAsync(Build(), token);
            return result;
            IEnumerable<int> Build()
            {
                var entries = new ResourcePreparationBundle[bundles.Count];
                long total = 0, cache = 0, builtin = 0, reuse = 0, download = 0;
                for (var i = 0; i < entries.Length; i++) {
                    ResourcePreparationBundle bundle = entries[i] = bundles[i];
                    total = checked(total + bundle.Bytes);
                    switch (bundle.Source) {
                        case ResourcePreparationSource.TargetCache: cache = checked(cache + bundle.Bytes); break;
                        case ResourcePreparationSource.BuiltIn: builtin = checked(builtin + bundle.Bytes); break;
                        case ResourcePreparationSource.CrossVersionCache: reuse = checked(reuse + bundle.Bytes); break;
                        case ResourcePreparationSource.Download: download = checked(download + bundle.Bytes); break;
                    }
                    yield return 0;
                }
                result = new ResourcePreparationPlan(version, validated, entries, selection, total, cache, builtin, reuse, download);
            }
        }

        private ResourcePreparationPlan(string version, ResourceManifest manifest, ResourcePreparationBundle[] bundles,
            ResourceSelectionResult selection, long total, long cache, long builtin, long reuse, long download)
        {
            Version = version; BuildTarget = manifest.BuildTarget; m_manifestSnapshot = manifest;
            Selection = selection; Bundles = Array.AsReadOnly(bundles);
            TotalBytes = total; TargetCacheBytes = cache; BuiltInBytes = builtin; ReuseBytes = reuse; DownloadBytes = download;
        }

    }

    public sealed partial class ResourceVersionManager
    {
        public event Action<ResourcePreparationProgress> PreparationProgressChanged;
        private Action<long> TrackPreparation(ResourcePreparationPhase phase, ResourceManifest manifest)
        {
            return TrackPreparation(phase, manifest.Bundles);
        }

        private Action<long> TrackPreparation(ResourcePreparationPhase phase, BundleInfo[] bundles)
        {
            long total = 0, bytes = 0; var completed = 0;
            foreach (BundleInfo bundle in bundles) {
                total = checked(total + bundle.Size);
            }

            Emit();
            return size => { bytes += size; completed++; Emit(); };
            void Emit()
            {
                if (PreparationProgressChanged == null) {
                    return;
                }

                var progress = new ResourcePreparationProgress(phase, completed, bundles.Length, bytes, total);
                foreach (Action<ResourcePreparationProgress> callback in PreparationProgressChanged.GetInvocationList()) {
                    try { callback(progress); } catch (Exception error) { Debug.LogException(error); }
                }
            }
        }
        /// <summary>
        /// 只校验目标缓存、首包和已登记的其他版本内容，生成实际缺失量；不复制、不下载、不发布清单或指针。
        /// 使用与真实准备相同的租约和版本锁；首次调用可能建立零字节基础锁文件。
        /// </summary>
        public ResourceOperationBase<ResourcePreparationPlan> PlanPrepareAsync(string version, ResourceManifest target,
            CancellationToken cancellationToken = default)
        {
            return PlanSelectionAsync(version, target, ResourceSelection.All, cancellationToken);
        }

        public ResourceOperationBase<ResourcePreparationPlan> PlanSelectionAsync(string version, ResourceManifest target,
            ResourceSelection selection, CancellationToken cancellationToken = default)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            return PlanSelectionAsyncScheduled(version, target, selection, cancellationToken);
        }

        private async ResourceOperationBase<ResourcePreparationPlan> PlanSelectionAsyncScheduled(string version, ResourceManifest target,
            ResourceSelection selection, CancellationToken cancellationToken = default, bool takeOwnership = false)
        {
            CheckThread();
            cancellationToken.ThrowIfCancellationRequested();
            await EnterAsync(cancellationToken, false);
            try {
                ValidateVersion(version);
                ResourceManifest snapshot = takeOwnership ? await ValidateOwnedTargetAsync(target, version, cancellationToken) :
                    await ValidateTargetAsync(target, version, cancellationToken);
                ResourceCatalog index = await ResourceCatalog.CreateOwnedAsync(snapshot, token: cancellationToken);
                ResourceSelectionResult selected = await index.SelectAsync(selection, cancellationToken);
                BundleInfo[] selectedBundles = selected.OwnedBundles;
                string path = ManifestPath(version), json = await SerializeAsync(snapshot, cancellationToken);
                if (File.Exists(path) && !await SameManifestAsync(await ResourceFileReader.ReadTextAsync(path, cancellationToken), json, cancellationToken)) {
                    throw new InvalidOperationException("同一版本号已经保存了不同清单；请使用新的版本号。");
                }

                CachedResourceFileSystem cache = await CreateVersionCacheAsync(version, cancellationToken);
                try {
                    Action<long> completed = TrackPreparation(ResourcePreparationPhase.Planning, selectedBundles);
                    ResourcePreparationBundle[] bundles = await ResourceWorkBatch.MapAsync(selectedBundles, m_options.MaxConcurrentDownloads, async (bundle, token) =>
                    {
                        ResourcePreparationBundle inspected = await cache.InspectAsync(bundle, token);
                        completed(bundle.Size);
                        return inspected;
                    }, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    return await ResourcePreparationPlan.CreateAsync(version, snapshot, new List<ResourcePreparationBundle>(bundles), selected, cancellationToken);
                }
                finally { await cache.DisposeAsync(); }
            }
            finally { Exit(); }
        }

        /// <summary>从签名候选的私有原始快照生成计划；长时间校验结束后再次确认签名有效期。</summary>
        public ResourceOperationBase<ResourcePreparationPlan> PlanPrepareSignedReleaseAsync(ResourceSignedUpdateCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            return PlanSignedSelectionAsync(candidate, ResourceSelection.All, cancellationToken);
        }

        public ResourceOperationBase<ResourcePreparationPlan> PlanSignedSelectionAsync(ResourceSignedUpdateCandidate candidate,
            ResourceSelection selection, CancellationToken cancellationToken = default)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            return PlanSignedSelectionAsyncScheduled(candidate, selection, cancellationToken);
        }

        private async ResourceOperationBase<ResourcePreparationPlan> PlanSignedSelectionAsyncScheduled(ResourceSignedUpdateCandidate candidate,
            ResourceSelection selection, CancellationToken cancellationToken = default)
        {
            CheckThread();
            if (candidate == null) {
                throw new ArgumentNullException(nameof(candidate));
            }

            cancellationToken.ThrowIfCancellationRequested();
            ResourceSignedReleasePayload payload = ResourceReleaseAuthentication.Verify(candidate.EnvelopeJson, candidate.Trust, DateTimeOffset.UtcNow);
            if (candidate.Target != m_buildTarget || payload.Release.BuildTarget != m_buildTarget || payload.Release.Version != candidate.Version ||
                !string.Equals(await HashAsync(candidate.ManifestJson, cancellationToken), payload.Release.ManifestSha256, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("签名候选版本与当前管理器或原始清单不一致。");
            }

            ResourcePreparationPlan plan = await PlanSelectionAsyncScheduled(candidate.Version, candidate.OwnedManifest, selection, cancellationToken, true);
            ResourceReleaseAuthentication.Verify(candidate.EnvelopeJson, candidate.Trust, DateTimeOffset.UtcNow);
            return plan;
        }
    }
}

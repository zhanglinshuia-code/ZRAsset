using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    internal sealed class RawFileProvider
    {
        internal bool Revoked;
        internal ResourceLoadPriority LoadPriority;
        internal AssetInfo Asset;
        internal int References;
        internal double ReleasedAt;
        internal ResourceOperationBase<RawFileSource> Operation;
    }
    public sealed partial class ResourceManager
    {
        // 保留已有二进制的加载入口；新重载通过 priority 参数选择实际等待顺序。
        public RawFileHandle LoadRawFileAsync(string address, CancellationToken cancellationToken)
        {
            return new(this, AcquireRaw(address, false, cancellationToken), cancellationToken);
        }

        private readonly Dictionary<string, RawFileProvider> m_rawFiles = new(StringComparer.Ordinal);
        private readonly RawFileContainerCache m_rawContainers = new();
        private readonly List<RawFileProvider> m_expiredRawFiles = new();
        public RawFileHandle LoadRawFileAsync(string address, CancellationToken cancellationToken = default, int priority = 0)
        {
            return new(this, AcquireRaw(address, false, cancellationToken, priority), cancellationToken);
        }

        public RawFileHandle LoadRawFileSync(string address)
        {
            return new(this, AcquireRaw(address, true, default), default);
        }

        private RawFileProvider AcquireRaw(string address, bool synchronous, CancellationToken token, int priority = 0)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            CheckThread(); if (m_closing) {
                throw new ObjectDisposedException(nameof(ResourceManager));
            }

            token.ThrowIfCancellationRequested();
            if (address == null || !m_locations.TryGetValue(address, out AssetInfo asset)) {
                throw new KeyNotFoundException("Unknown raw file address: " + address);
            }

            if (asset.Kind != ResourceKind.RawFile) {
                throw new InvalidOperationException("该地址不是原始文件。");
            }

            if (m_rawFiles.TryGetValue(asset.AssetPath, out RawFileProvider existing)) {
                if (synchronous) {
                    RequireCompleted(existing.Operation);
                }

                existing.References++;
                m_unusedRawFiles.Remove(existing);
                existing.LoadPriority.Raise(priority);
                ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.ProviderReuse, PackageName, PackageVersion, asset.Address));
                return existing;
            }
            if (m_backend is not IRawFileBackend raw) {
                throw new NotSupportedException("当前后端不支持原始文件。");
            }

            var completion = new OperationCompletionSource<RawFileSource>();
            var provider = new RawFileProvider { LoadPriority = new ResourceLoadPriority(priority), Asset = asset, References = 1, Operation = completion.Operation };
            m_rawFiles.Add(asset.AssetPath, provider);
            var started = ResourceTelemetry.StartTimer();
            if (synchronous) {
                try {
                    completion.SetResult(m_assetLoads.RunSync(() => RawFileSource.Open(raw.ResolveRawFile(ResourceManifest.CopyBundle(m_catalog[asset.BundleName]), ResourceManifest.CopyAsset(asset)), m_rawContainers)));
                    Record(null);
                }
                catch (Exception error) { Record(error); completion.SetException(error); ReleaseRaw(provider); throw; }
            }
            else {
                _ = CompleteAsync();
            }

            return provider;
            async ResourceOperationBase CompleteAsync()
            {
                try {
                    RawFileSource source = await m_assetLoads.RunAsync<RawFileSource>(async () =>
                        await RawFileSource.OpenAsync(await raw.ResolveRawFileAsync(ResourceManifest.CopyBundle(m_catalog[asset.BundleName]), ResourceManifest.CopyAsset(asset)), m_rawContainers), provider.LoadPriority);
                    completion.SetResult(source);
                    Record(null);
                }
                catch (Exception error) { Record(error); completion.SetException(error); }
            }

            void Record(Exception error)
            {
                ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.RawFileLoad, PackageName, PackageVersion,
                    asset.Address, ResourceFailure.FromException(error, ResourceStage.Load), ResourceTelemetry.Elapsed(started)));
            }
        }
        internal void ReleaseRaw(RawFileProvider provider)
        {
            CheckThread(); if (provider.Revoked) {
                return;
            }

            if (--provider.References == 0) {
                provider.ReleasedAt = Time.realtimeSinceStartupAsDouble;
                m_unusedRawFiles.Add(provider);
            }
        }
        private bool CollectRawFiles(bool force, double now, string assetPath)
        {
            var removed = false;
            try {
                foreach (RawFileProvider provider in m_unusedRawFiles) {
                    if ((assetPath == null || provider.Asset.AssetPath == assetPath) && provider.References == 0 &&
                        provider.Operation.IsDone && (force || provider.Operation.IsFaulted || now - provider.ReleasedAt >= m_unloadDelay)) {
                        m_expiredRawFiles.Add(provider);
                    }
                }

                foreach (RawFileProvider provider in m_expiredRawFiles) {
                    if (provider.References != 0 || !m_rawFiles.TryGetValue(provider.Asset.AssetPath, out RawFileProvider current) ||
                        !ReferenceEquals(current, provider)) {
                        continue;
                    }

                    m_rawFiles.Remove(provider.Asset.AssetPath);
                    m_unusedRawFiles.Remove(provider);
                    try {
                        if (provider.Operation.Status == OperationStatus.Succeeded) {
                            provider.Operation.Result.Dispose();
                        }
                    }
                    catch {
                        if (!m_rawFiles.ContainsKey(provider.Asset.AssetPath)) {
                            m_rawFiles.Add(provider.Asset.AssetPath, provider);
                            m_unusedRawFiles.Add(provider);
                        }

                        throw;
                    }
                    removed = true;
                }
            }
            finally {
                m_expiredRawFiles.Clear();
            }
            return removed;
        }
    }
}

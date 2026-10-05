using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZRAsset
{
    public sealed partial class ResourceManager
    {
        // 保留已有二进制的加载入口；新重载通过 priority 参数选择实际等待顺序。
        public AssetHandle<Object> LoadAssetAsync(string address, Type type, CancellationToken cancellationToken) { return new(this, AcquireAsset(address, type, ResourceAssetLoadKind.MainAsset, false, cancellationToken), cancellationToken); }
        public AssetCollectionHandle<Object> LoadSubAssetsAsync(string address, Type type, CancellationToken cancellationToken) { return new(this, AcquireAsset(address, type, ResourceAssetLoadKind.SubAssets, false, cancellationToken), cancellationToken); }
        public AssetCollectionHandle<Object> LoadAllAssetsAsync(string address, Type type, CancellationToken cancellationToken) { return new(this, AcquireAsset(address, type, ResourceAssetLoadKind.AllAssets, false, cancellationToken), cancellationToken); }
        public AssetHandle<T> LoadAssetAsync<T>(string address, CancellationToken cancellationToken) where T : Object { return new(this, AcquireAsset(address, typeof(T), ResourceAssetLoadKind.MainAsset, false, cancellationToken), cancellationToken); }
        public AssetCollectionHandle<T> LoadSubAssetsAsync<T>(string address, CancellationToken cancellationToken) where T : Object { return new(this, AcquireAsset(address, typeof(T), ResourceAssetLoadKind.SubAssets, false, cancellationToken), cancellationToken); }
        public AssetCollectionHandle<T> LoadAllAssetsAsync<T>(string address, CancellationToken cancellationToken) where T : Object { return new(this, AcquireAsset(address, typeof(T), ResourceAssetLoadKind.AllAssets, false, cancellationToken), cancellationToken); }
        public AssetHandle<Object> LoadAssetAsync(string address, Type type, CancellationToken cancellationToken = default, int priority = 0) { return new(this, AcquireAsset(address, type, ResourceAssetLoadKind.MainAsset, false, cancellationToken, priority), cancellationToken); }
        public AssetHandle<Object> LoadAssetSync(string address, Type type) { return new(this, AcquireAsset(address, type, ResourceAssetLoadKind.MainAsset, true, default), default); }
        public AssetCollectionHandle<Object> LoadSubAssetsAsync(string address, Type type, CancellationToken cancellationToken = default, int priority = 0) { return new(this, AcquireAsset(address, type, ResourceAssetLoadKind.SubAssets, false, cancellationToken, priority), cancellationToken); }
        public AssetCollectionHandle<Object> LoadSubAssetsSync(string address, Type type) { return new(this, AcquireAsset(address, type, ResourceAssetLoadKind.SubAssets, true, default), default); }
        public AssetCollectionHandle<Object> LoadAllAssetsAsync(string address, Type type, CancellationToken cancellationToken = default, int priority = 0) { return new(this, AcquireAsset(address, type, ResourceAssetLoadKind.AllAssets, false, cancellationToken, priority), cancellationToken); }
        public AssetCollectionHandle<Object> LoadAllAssetsSync(string address, Type type) { return new(this, AcquireAsset(address, type, ResourceAssetLoadKind.AllAssets, true, default), default); }
        public AssetHandle<T> LoadAssetAsync<T>(string address, CancellationToken cancellationToken = default, int priority = 0) where T : Object { return new(this, AcquireAsset(address, typeof(T), ResourceAssetLoadKind.MainAsset, false, cancellationToken, priority), cancellationToken); }
        public AssetHandle<T> LoadAssetSync<T>(string address) where T : Object { return new(this, AcquireAsset(address, typeof(T), ResourceAssetLoadKind.MainAsset, true, default), default); }
        public AssetCollectionHandle<T> LoadSubAssetsAsync<T>(string address, CancellationToken cancellationToken = default, int priority = 0) where T : Object { return new(this, AcquireAsset(address, typeof(T), ResourceAssetLoadKind.SubAssets, false, cancellationToken, priority), cancellationToken); }
        public AssetCollectionHandle<T> LoadSubAssetsSync<T>(string address) where T : Object { return new(this, AcquireAsset(address, typeof(T), ResourceAssetLoadKind.SubAssets, true, default), default); }
        public AssetCollectionHandle<T> LoadAllAssetsAsync<T>(string address, CancellationToken cancellationToken = default, int priority = 0) where T : Object { return new(this, AcquireAsset(address, typeof(T), ResourceAssetLoadKind.AllAssets, false, cancellationToken, priority), cancellationToken); }
        public AssetCollectionHandle<T> LoadAllAssetsSync<T>(string address) where T : Object { return new(this, AcquireAsset(address, typeof(T), ResourceAssetLoadKind.AllAssets, true, default), default); }
        private AssetProvider AcquireAsset(string address, Type type, ResourceAssetLoadKind kind, bool synchronous, CancellationToken token, int priority = 0)
        {
            using ResourceScheduling.Scope scheduling = ResourceScheduling.Enter(SchedulingPackage);
            CheckThread();
            if (m_closing) {
                throw new ObjectDisposedException(nameof(ResourceManager));
            }
            token.ThrowIfCancellationRequested();
            if (type == null) {
                throw new ArgumentNullException(nameof(type));
            }
            if (!typeof(Object).IsAssignableFrom(type) || type.ContainsGenericParameters) {
                throw new ArgumentException("资源类型必须是封闭的 UnityEngine.Object 派生类型。", nameof(type));
            }
            if (address == null || !m_locations.TryGetValue(address, out AssetInfo info)) {
                throw new KeyNotFoundException("Unknown resource address: " + address);
            }
            if (info.Kind != ResourceKind.Asset) {
                throw new InvalidOperationException("场景使用 LoadSceneAsync；原始文件使用 LoadRawFileAsync/Sync。");
            }            // 全包按 Bundle 身份去重；主资源/子资源按路径去重，三种加载范围不能相互冒充。
            (string, Type type, ResourceAssetLoadKind kind) key = (kind == ResourceAssetLoadKind.AllAssets ? info.BundleName : info.AssetPath, type, kind);
            if (m_assetCache.TryGetValue(key, out AssetProvider existing)) {
                if (synchronous) { CompleteAssetSynchronously(existing, info, type, kind); }
                existing.References++;
                m_unusedAssets.Remove(existing);
                existing.LoadPriority.Raise(priority);
                foreach (BundleProvider dependency in existing.Bundles) {
                    dependency.LoadPriority.Raise(priority);
                }
                ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.ProviderReuse, PackageName, PackageVersion, info.Address));
                return existing;
            }
            if (synchronous && (!(m_backend is ISynchronousResourceBackend sync) || !sync.SupportsSynchronousLoading)) {
                throw new NotSupportedException("当前后端不支持同步加载。");
            }
            if (kind != ResourceAssetLoadKind.MainAsset) {
                if (synchronous && (!(m_backend is ISynchronousResourceCollectionBackend syncCollections) || !syncCollections.SupportsSynchronousCollections)) {
                    throw new NotSupportedException("当前后端不支持同步资源集合加载。");
                }
                if (!synchronous && (!(m_backend is IResourceCollectionBackend collections) || !collections.SupportsAssetCollections)) {
                    throw new NotSupportedException("当前后端不支持资源集合加载。");
                }
            }
            var closure = kind == ResourceAssetLoadKind.AllAssets
                ? m_dependencyGraph.GetBundleClosure(info.BundleName)
                : m_dependencyGraph.GetAssetClosure(info);
            // 在取得新引用之前接管可同步完成的本地请求；网络请求不阻塞主线程。
            if (synchronous) {
                foreach (var name in closure) {
                    if (m_bundleCache.TryGetValue(name, out BundleProvider bundle)) { CompleteBundleSynchronously(bundle); }
                }
            }
            var completion = new OperationCompletionSource<object>();
            var provider = new AssetProvider
            {
                Completion = completion,
                LoadPriority = new ResourceLoadPriority(priority),
                Key = key,
                OwningBundleName = info.BundleName,
                Bundles = Array.Empty<BundleProvider>(),
                References = 1,
                State = ProviderState.LoadingBundles,
                Operation = completion.Operation
            };
            m_assetCache.Add(key, provider);
            var held = new List<BundleProvider>();
            var started = synchronous ? ResourceTelemetry.StartTimer() : 0;
            try {
                foreach (var name in closure) {
                    held.Add(AcquireBundle(name, synchronous, priority));
                }
                provider.Bundles = held.ToArray();
                if (synchronous) {
                    foreach (BundleProvider dependency in provider.Bundles) {
                        RequireCompleted(dependency.Operation);
                    }
                    provider.State = ProviderState.LoadingAsset;
                    var bundle = provider.Bundles.First(b => b.Info.Name == info.BundleName).Operation.Result;
                    var value = m_assetLoads.RunSync(() => LoadValue(bundle, info, type, kind));
                    provider.State = ProviderState.Succeeded;
                    completion.SetResult(ValidateValue(value, type, kind));
                    ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.AssetLoad,
                        PackageName, PackageVersion, info.Address, durationMilliseconds: ResourceTelemetry.Elapsed(started)));
                }
                else {
                    _ = CompleteAssetAsync(provider, info, type, kind, completion);
                }
            }
            catch (Exception error) {
                provider.Bundles = held.ToArray();
                provider.State = ProviderState.Failed;
                completion.TrySetException(error);
                ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.AssetLoad,
                    PackageName, PackageVersion, info.Address, ResourceFailure.FromException(error, ResourceStage.Load), ResourceTelemetry.Elapsed(started)));
                if (synchronous) { Release(provider); throw; }
            }
            return provider;
        }

        private static void RequireCompleted(ResourceOperationBase operation)
        {
            if (!operation.IsDone) {
                throw new InvalidOperationException("同一资源或依赖仍在异步加载，请先等待该操作完成，再调用同步接口。");
            }
            operation.GetAwaiter().GetResult();
        }

        private BundleProvider AcquireBundle(string name, bool synchronous, int priority = 0)
        {
            if (m_bundleCache.TryGetValue(name, out BundleProvider existing)) {
                existing.References++;
                m_unusedBundles.Remove(existing);
                existing.LoadPriority.Raise(priority);
                return existing;
            }
            var completion = new OperationCompletionSource<object>();
            var provider = new BundleProvider { Completion = completion, LoadPriority = new ResourceLoadPriority(priority), Info = m_catalog[name], References = 1, Operation = completion.Operation };
            m_bundleCache.Add(name, provider);
            if (synchronous) {
                try {
                    var bundle = m_bundleLoads.RunSync(() => ((ISynchronousResourceBackend)m_backend).LoadBundle(ResourceManifest.CopyBundle(provider.Info)));
                    completion.TrySetResult(bundle ?? throw new InvalidOperationException("Backend returned a null bundle: " + name));
                }
                catch (Exception error) { completion.TrySetException(error); }
            }
            else {
                _ = CompleteBundleAsync(provider, completion);
            }
            return provider;
        }

        private async ResourceOperationBase CompleteBundleAsync(BundleProvider provider, OperationCompletionSource<object> completion)
        {
            try {
                ResourceOperationBase<object> LoadOnce()
                {
                    return m_bundleLoads.RunAsync(() =>
                    {
                        if (provider.Operation.IsDone) { return provider.Operation; }
                        provider.IssuingRequest = true;
                        try { return provider.Request = m_backend.LoadBundleAsync(ResourceManifest.CopyBundle(provider.Info)); }
                        finally { provider.IssuingRequest = false; }
                    }, provider.LoadPriority);
                }
                var bundle = (FileSystem?.OwnsRetries ?? false) ? await LoadOnce() : await m_retryPolicy.ExecuteAsync(LoadOnce);
                completion.TrySetResult(bundle ?? throw new InvalidOperationException("Backend returned a null bundle: " + provider.Info.Name));
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally { provider.Request = null; }
        }

        private async ResourceOperationBase CompleteAssetAsync(AssetProvider provider, AssetInfo info, Type type, ResourceAssetLoadKind kind,
            OperationCompletionSource<object> completion)
        {
            var started = ResourceTelemetry.StartTimer();
            try {
                await ResourceOperationBase.WhenAll(provider.Bundles.Select(b => b.Operation));
                if (provider.Operation.IsDone) { return; }
                provider.State = ProviderState.LoadingAsset;
                var bundle = provider.Bundles.First(b => b.Info.Name == info.BundleName).Operation.Result;
                var value = await m_retryPolicy.ExecuteAsync(() => m_assetLoads.RunAsync(() =>
                {
                    if (provider.Operation.IsDone) { return provider.Operation; }
                    provider.IssuingRequest = true;
                    try { return provider.Request = LoadValueAsync(bundle, info, type, kind); }
                    finally { provider.IssuingRequest = false; }
                }, provider.LoadPriority));
                if (provider.Operation.IsDone) { return; }
                value = ValidateValue(value, type, kind);
                provider.State = ProviderState.Succeeded;
                completion.SetResult(value);
                ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.AssetLoad,
                    PackageName, PackageVersion, info.Address, durationMilliseconds: ResourceTelemetry.Elapsed(started)));
            }
            catch (Exception error) {
                if (provider.Operation.IsDone) { return; }
                provider.State = ProviderState.Failed;
                completion.TrySetException(error);
                ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.AssetLoad,
                    PackageName, PackageVersion, info.Address, ResourceFailure.FromException(error, ResourceStage.Load), ResourceTelemetry.Elapsed(started)));
            }
            finally {
                provider.Request = null;
                if (provider.References == 0) { provider.ReleasedAt = Time.realtimeSinceStartupAsDouble; }
            }
        }

        private ResourceOperationBase<object> LoadValueAsync(object bundle, AssetInfo info, Type type, ResourceAssetLoadKind kind)
        {
            if (kind == ResourceAssetLoadKind.MainAsset) {
                return OperationSystem.Start(new MappedResourceOperation<Object, object>(m_backend.LoadAssetAsync(bundle, info.AssetPath, type), value => value));
            }
            var collections = (IResourceCollectionBackend)m_backend;
            ResourceOperationBase<Object[]> request = kind == ResourceAssetLoadKind.SubAssets ? collections.LoadSubAssetsAsync(bundle, info.AssetPath, type) :
                collections.LoadAllAssetsAsync(bundle, type);
            return OperationSystem.Start(new MappedResourceOperation<Object[], object>(request, value => value));
        }

        private static void CompleteRequestSynchronously(ResourceOperationBase request)
        {
            if (!request.IsDone && request is ISynchronousResourceOperation synchronous) { synchronous.TryCompleteSynchronously(); }
            RequireCompleted(request);
        }

        private void CompleteBundleSynchronously(BundleProvider provider)
        {
            if (provider.IssuingRequest) { throw new InvalidOperationException("不能从后端加载回调重入同步加载同一 Bundle。"); }
            if (!provider.Operation.IsDone && provider.Request != null) {
                CompleteRequestSynchronously(provider.Request);
                provider.Completion.TrySetResult(provider.Request.Result);
            }
            else if (!provider.Operation.IsDone && m_backend is ISynchronousResourceBackend synchronous && synchronous.SupportsSynchronousLoading) {
                // 接管尚未取得许可的共享请求；完成后原排队回调只读取结果，不会重复打开。
                provider.IssuingRequest = true;
                try {
                    var value = m_bundleLoads.RunSync(() => synchronous.LoadBundle(ResourceManifest.CopyBundle(provider.Info)), allowBorrow: true) ?? throw new InvalidOperationException("Backend returned a null bundle.");
                    provider.Completion.TrySetResult(value);
                }
                finally { provider.IssuingRequest = false; }
            }
            RequireCompleted(provider.Operation);
            provider.Request = null;
        }

        private void CompleteAssetSynchronously(AssetProvider provider, AssetInfo info, Type type, ResourceAssetLoadKind kind)
        {
            if (provider.Operation.IsDone) { RequireCompleted(provider.Operation); return; }
            if (provider.IssuingRequest) { throw new InvalidOperationException("不能从后端加载回调重入同步加载同一资源。"); }
            var started = ResourceTelemetry.StartTimer();
            foreach (BundleProvider dependency in provider.Bundles) { CompleteBundleSynchronously(dependency); }
            object value;
            if (provider.Request != null) {
                CompleteRequestSynchronously(provider.Request);
                value = provider.Request.Result;
            }
            else {
                if (m_backend is not ISynchronousResourceBackend synchronous || !synchronous.SupportsSynchronousLoading) {
                    throw new NotSupportedException("当前后端不支持同步接管。");
                }
                var bundle = provider.Bundles.First(item => item.Info.Name == info.BundleName).Operation.Result;
                provider.IssuingRequest = true;
                try { value = m_assetLoads.RunSync(() => LoadValue(bundle, info, type, kind), allowBorrow: true); }
                finally { provider.IssuingRequest = false; }
            }
            value = ValidateValue(value, type, kind);
            provider.State = ProviderState.Succeeded;
            provider.Completion.TrySetResult(value);
            provider.Request = null;
            ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.AssetLoad, PackageName, PackageVersion,
                info.Address, durationMilliseconds: ResourceTelemetry.Elapsed(started)));
        }

        private object LoadValue(object bundle, AssetInfo info, Type type, ResourceAssetLoadKind kind)
        {
            if (kind == ResourceAssetLoadKind.MainAsset) {
                return ((ISynchronousResourceBackend)m_backend).LoadAsset(bundle, info.AssetPath, type);
            }
            var collections = (ISynchronousResourceCollectionBackend)m_backend;
            return kind == ResourceAssetLoadKind.SubAssets ? collections.LoadSubAssets(bundle, info.AssetPath, type) : collections.LoadAllAssets(bundle, type);
        }

        private static object ValidateValue(object value, Type type, ResourceAssetLoadKind kind)
        {
            if (kind == ResourceAssetLoadKind.MainAsset) {
                return !(value is Object asset) || asset == null || !type.IsInstanceOfType(asset)
                    ? throw new InvalidOperationException("Backend returned an invalid asset.")
                    : (object)asset;
            }
            if (!(value is Object[] result) || result.Any(item => item == null || !type.IsInstanceOfType(item))) {
                throw new InvalidOperationException("Backend returned an invalid asset collection.");
            }
            return result.ToArray(); // 后端持有的可变数组不得修改 Provider 的共享结果。
        }
    }
}

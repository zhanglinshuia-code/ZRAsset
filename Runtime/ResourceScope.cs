using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    [Serializable]
    public sealed class ResourceScopeDiagnostic
    {
        public string Name;
        public bool Closing;
        public ResourceScopeEntryDiagnostic[] Entries;
    }

    [Serializable]
    public sealed class ResourceScopeEntryDiagnostic
    {
        public string Address;
        public string Kind;
        public string CreationStack;
    }

    /// <summary>按业务所有者管理独立句柄。关闭会等待实例销毁和场景卸载；失败条目保留并允许重试。</summary>
    public sealed partial class ResourceScope
    {
        private sealed class Entry
        {
            internal ResourceOperationBase Releasing;
            public object Handle { get; }
            public string Address { get; }
            public string Stack { get; }
            public CancellationTokenSource Cancellation { get; }

            public Entry(object handle, string address, string stack, CancellationTokenSource cancellation)
            {
                Handle = handle;
                Address = address;
                Stack = stack;
                Cancellation = cancellation;
            }
        }

        private readonly ResourceManager m_manager;
        private readonly LinkedList<Entry> m_entries = new();
        private readonly Dictionary<object, LinkedListNode<Entry>> m_owned = new();
        private readonly CancellationTokenSource m_cancellation;
        private readonly bool m_captureStacks;
        private bool m_closing;

        public string Name { get; }
        public bool IsClosed { get; private set; }
        public int Count { get { m_manager.CheckThread(); return m_entries.Count; } }
        public ResourceOperationBase Disposal { get; private set; }

        internal ResourceScope(ResourceManager manager, string name, bool captureStacks, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(name)) {
                throw new ArgumentException("作用域必须指定业务名称。", nameof(name));
            }
            m_manager = manager;
            Name = name;
            m_cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_captureStacks = captureStacks;
#else
            m_captureStacks = false;
#endif
        }

        public AssetHandle<T> LoadAssetAsync<T>(string address) where T : UnityEngine.Object { return LoadAssetAsync<T>(address, 0); }
        public AssetCollectionHandle<T> LoadSubAssetsAsync<T>(string address) where T : UnityEngine.Object { return LoadSubAssetsAsync<T>(address, 0); }
        public AssetCollectionHandle<T> LoadAllAssetsAsync<T>(string address) where T : UnityEngine.Object { return LoadAllAssetsAsync<T>(address, 0); }
        public RawFileHandle LoadRawFileAsync(string address) { return LoadRawFileAsync(address, 0); }
        public InstanceHandle InstantiateAsync(string address, Transform parent, bool worldPositionStays) { return InstantiateAsync(address, parent, worldPositionStays, 0); }
        public AssetHandle<T> LoadAssetAsync<T>(string address, int priority = 0) where T : UnityEngine.Object
        {
            CheckOpen();
            return TrackAsync(token => m_manager.LoadAssetAsync<T>(address, token, priority), address);
        }

        public AssetHandle<T> LoadAssetSync<T>(string address) where T : UnityEngine.Object
        {
            CheckOpen();
            return Track(m_manager.LoadAssetSync<T>(address), address);
        }

        public AssetCollectionHandle<T> LoadSubAssetsAsync<T>(string address, int priority = 0) where T : UnityEngine.Object
        {
            CheckOpen();
            return TrackAsync(token => m_manager.LoadSubAssetsAsync<T>(address, token, priority), address);
        }

        public AssetCollectionHandle<T> LoadAllAssetsAsync<T>(string address, int priority = 0) where T : UnityEngine.Object
        {
            CheckOpen();
            return TrackAsync(token => m_manager.LoadAllAssetsAsync<T>(address, token, priority), address);
        }

        public AssetCollectionHandle<T> LoadSubAssetsSync<T>(string address) where T : UnityEngine.Object
        {
            CheckOpen();
            return Track(m_manager.LoadSubAssetsSync<T>(address), address);
        }

        public AssetCollectionHandle<T> LoadAllAssetsSync<T>(string address) where T : UnityEngine.Object
        {
            CheckOpen();
            return Track(m_manager.LoadAllAssetsSync<T>(address), address);
        }

        public RawFileHandle LoadRawFileAsync(string address, int priority = 0)
        {
            CheckOpen();
            return TrackAsync(token => m_manager.LoadRawFileAsync(address, token, priority), address);
        }

        public RawFileHandle LoadRawFileSync(string address)
        {
            CheckOpen();
            return Track(m_manager.LoadRawFileSync(address), address);
        }

        public InstanceHandle InstantiateAsync(string address, Transform parent = null, bool worldPositionStays = false, int priority = 0)
        {
            CheckOpen();
            return TrackAsync(token => m_manager.InstantiateAsync(address, parent, worldPositionStays, token, priority), address);
        }

        public InstanceHandle InstantiateAsync(string address, ResourceInstantiateOptions options, int priority = 0)
        {
            CheckOpen();
            return TrackAsync(token => m_manager.InstantiateAsync(address, options, token, priority), address);
        }

        public InstanceHandle InstantiateSync(string address, ResourceInstantiateOptions options = default)
        {
            CheckOpen();
            return Track(m_manager.InstantiateSync(address, options), address);
        }

        public SceneHandle LoadSceneAsync(string address, ResourceSceneLoadOptions options = null)
        {
            CheckOpen();
            return TrackAsync(token => options == null
                ? m_manager.LoadSceneAsync(address, cancellationToken: token)
                : m_manager.LoadSceneAsync(address, options, token), address);
        }

        /// <summary>转移所有权；之后调用方负责释放。关闭中的作用域不能再转移。</summary>
        public bool Detach(object handle)
        {
            CheckOpen();
            if (handle == null || !m_owned.TryGetValue(handle, out LinkedListNode<Entry> node)) { return false; }
            if (node.Value.Releasing != null) { throw new InvalidOperationException("条目正在释放，不能转移所有权。"); }
            Remove(node);
            return true;
        }

        /// <summary>提前释放本作用域拥有的条目。成功后立即移除跟踪及取消注册；失败保留条目供重试。</summary>
        public async ResourceOperationBase<bool> ReleaseAsync(object handle)
        {
            CheckOpen();
            if (handle == null || !m_owned.TryGetValue(handle, out LinkedListNode<Entry> node)) { return false; }
            await ReleaseEntry(node);
            return true;
        }

        private ResourceOperationBase ReleaseEntry(LinkedListNode<Entry> node)
        {
            Entry entry = node.Value;
            if (entry.Releasing == null || entry.Releasing.IsFaulted || entry.Releasing.IsCanceled) {
                var completion = new OperationCompletionSource<bool>();
                entry.Releasing = completion.Operation;
                _ = ReleaseEntryAsync(node, completion);
            }
            return entry.Releasing;
        }

        private async ResourceOperationBase ReleaseEntryAsync(LinkedListNode<Entry> node, OperationCompletionSource<bool> completion)
        {
            try {
                var handle = node.Value.Handle;
                if (handle is InstanceHandle instance) { await instance.DestroyAsync(); }
                else if (handle is SceneHandle scene) { await scene.UnloadAsync(); }
                else { ((IDisposable)handle).Dispose(); }
                Remove(node);
                completion.TrySetResult(true);
            }
            catch (Exception error) { completion.TrySetException(error); }
        }

        private void Remove(LinkedListNode<Entry> node)
        {
            node.Value.Cancellation?.Dispose();
            m_owned.Remove(node.Value.Handle);
            m_entries.Remove(node);
        }

        public ResourceScopeDiagnostic CaptureDiagnostics()
        {
            m_manager.CheckThread();
            var entries = new ResourceScopeEntryDiagnostic[m_entries.Count];
            var index = 0;
            foreach (Entry entry in m_entries) {
                entries[index++] = new ResourceScopeEntryDiagnostic
                {
                    Address = entry.Address,
                    Kind = entry.Handle.GetType().Name,
                    CreationStack = entry.Stack
                };
            }
            return new ResourceScopeDiagnostic { Name = Name, Closing = m_closing, Entries = entries };
        }

        /// <summary>必须 await/yield 完成。取消只影响本作用域的等待者，不取消外部共享加载。</summary>
        public ResourceOperationBase DisposeAsync()
        {
            m_manager.CheckThread();
            if (Disposal != null && (!Disposal.IsDone || IsClosed)) {
                return Disposal;
            }
            m_closing = true;
            Disposal = CloseAsync();
            return Disposal;
        }

        private async ResourceOperationBase CloseAsync()
        {
            await ResourceOperationBase.Yield();
            List<Exception> failures = null;
            try {
                m_cancellation.Cancel();
            }
            catch (Exception error) {
                failures = new List<Exception> { error };
            }
            var entries = new LinkedListNode<Entry>[m_entries.Count];
            var index = 0;
            for (LinkedListNode<Entry> current = m_entries.Last; current != null; current = current.Previous) { entries[index++] = current; }
            foreach (LinkedListNode<Entry> node in entries) {
                if (node.List == null) { continue; }
                try { await ReleaseEntry(node); }
                catch (Exception error) { (failures ??= new List<Exception>()).Add(error); }
            }
            if (m_entries.Count == 0) {
                IsClosed = true;
                m_cancellation.Dispose();
                m_manager.RemoveScope(this);
            }
            Exception failure = failures == null ? null : new AggregateException("资源作用域关闭发生错误。", failures);
            ResourceTelemetry.Record(new ResourceTelemetryEvent(ResourceTelemetryKind.ScopeRelease,
                m_manager.PackageName, m_manager.PackageVersion, Name, ResourceFailure.FromException(failure, ResourceStage.Release)));
            if (failure != null) {
                throw failure;
            }
        }

        private T TrackAsync<T>(Func<CancellationToken, T> acquire, string address, CancellationToken token = default) where T : class
        {
            CancellationTokenSource cancellation = token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(m_cancellation.Token, token)
                : CancellationTokenSource.CreateLinkedTokenSource(m_cancellation.Token);
            try { return Track(acquire(cancellation.Token), address, cancellation); }
            catch { cancellation.Dispose(); throw; }
        }

        private T Track<T>(T handle, string address, CancellationTokenSource cancellation = null) where T : class
        {
            LinkedListNode<Entry> node = m_entries.AddLast(new Entry(handle, address, m_captureStacks ? Environment.StackTrace : null, cancellation));
            m_owned.Add(handle, node);
            return handle;
        }

        private void CheckOpen()
        {
            m_manager.CheckThread();
            if (m_closing) {
                throw new ObjectDisposedException(nameof(ResourceScope));
            }
            m_cancellation.Token.ThrowIfCancellationRequested();
        }
    }

    public sealed partial class ResourceManager
    {
        private HashSet<ResourceScope> m_scopes;

        public ResourceScope CreateScope(string name, bool captureCreationStacks = false, CancellationToken cancellationToken = default)
        {
            CheckThread();
            if (m_closing) {
                throw new ObjectDisposedException(nameof(ResourceManager));
            }
            var scope = new ResourceScope(this, name, captureCreationStacks, cancellationToken);
            (m_scopes ??= new HashSet<ResourceScope>()).Add(scope);
            return scope;
        }

        public ResourceScopeDiagnostic[] CaptureScopeDiagnostics()
        {
            CheckThread();
            if (m_scopes == null || m_scopes.Count == 0) {
                return Array.Empty<ResourceScopeDiagnostic>();
            }
            var result = new ResourceScopeDiagnostic[m_scopes.Count];
            var index = 0;
            foreach (ResourceScope scope in m_scopes) {
                result[index++] = scope.CaptureDiagnostics();
            }
            return result;
        }

        internal void RemoveScope(ResourceScope scope)
        {
            m_scopes?.Remove(scope);
        }
    }
}

using System;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ZRAsset
{
    public enum InstanceState { Loading, Ready, Destroying, Released, Failed, Canceled }

    /// <summary>同时持有实例和 Prefab 使用凭证，直到 Unity 实际销毁实例后才释放资源。</summary>
    public sealed class InstanceHandle
    {
        private readonly ResourceManager m_owner;
        private readonly CancellationTokenSource m_cancellation;
        private AssetHandle<GameObject> m_asset;
        private GameObject m_instance;
        private ResourceOperationBase m_destroyTask;
        private bool m_releaseRequested;
        public ResourceOperationBase<GameObject> Operation { get; private set; }
        /// <summary>创建实例时使用的业务地址，供按需诊断定位持有者。</summary>
        public string Address { get; private set; }
        public InstanceState State { get; private set; } = InstanceState.Loading;
        public bool IsReleased { get; private set; }
        public Exception Error { get { return Operation.Exception?.GetBaseException(); } }
        public GameObject Instance
        {
            get
            {
                m_owner.CheckThread();
                return IsReleased || m_releaseRequested
                    ? throw new ObjectDisposedException(nameof(InstanceHandle))
                    : State != InstanceState.Ready ? throw new InvalidOperationException("Await instanceHandle.Operation first.") : m_instance;
            }
        }

        internal InstanceHandle(ResourceManager owner, CancellationToken token)
        {
            m_owner = owner;
            m_cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        }

        private static Transform s_inactiveParent;

        internal void Start(string address, Transform parent, bool worldPositionStays, int priority)
        {
            Start(address, new ResourceInstantiateOptions(parent, worldPositionStays), priority);
        }

        internal void Start(string address, ResourceInstantiateOptions options, int priority)
        {
            Address = address;
            var completion = new OperationCompletionSource<GameObject>();
            Operation = completion.Operation;
            _ = LoadAsync(address, options, priority, completion);
        }

        internal void StartSync(string address, ResourceInstantiateOptions options)
        {
            Address = address;
            var completion = new OperationCompletionSource<GameObject>();
            Operation = completion.Operation;
            try {
                m_asset = m_owner.LoadAssetSync<GameObject>(address);
                CreateInstance(m_asset.Asset, options);
                completion.SetResult(m_instance);
            }
            catch (Exception error) {
                Finish(InstanceState.Failed);
                completion.SetException(error);
                throw;
            }
        }

        private async ResourceOperationBase LoadAsync(string address, ResourceInstantiateOptions options, int priority,
            OperationCompletionSource<GameObject> completion)
        {
            var hadParent = options.Parent != null;
            try {
                m_asset = m_owner.LoadAssetAsync<GameObject>(address, m_cancellation.Token, priority);
                GameObject prefab = await m_asset.Operation;
                m_cancellation.Token.ThrowIfCancellationRequested();
                if (hadParent && options.Parent == null) { throw new InvalidOperationException("Instance parent was destroyed during loading."); }
                CreateInstance(prefab, options);
                completion.SetResult(m_instance);
            }
            catch (Exception error) {
                Finish(error is OperationCanceledException ? InstanceState.Canceled : InstanceState.Failed);
                completion.SetException(error);
            }
        }

        private void CreateInstance(GameObject prefab, ResourceInstantiateOptions options)
        {
            Transform parent = options.Parent;
            if (options.Active == false) {
                Scene targetScene = parent != null ? parent.gameObject.scene : SceneManager.GetActiveScene();
                if (s_inactiveParent == null) {
                    var staging = new GameObject("ZRAsset Inactive Instances") { hideFlags = HideFlags.HideAndDontSave };
                    staging.SetActive(false);
                    Object.DontDestroyOnLoad(staging);
                    s_inactiveParent = staging.transform;
                }
                m_instance = Object.Instantiate(prefab, s_inactiveParent, options.WorldPositionStays);
                m_instance.SetActive(false);
                m_instance.transform.SetParent(parent, options.WorldPositionStays);
                if (parent == null && targetScene.IsValid() && targetScene.isLoaded) {
                    SceneManager.MoveGameObjectToScene(m_instance, targetScene);
                }
            }
            else if (options.Position.HasValue || options.Rotation.HasValue) {
                Vector3 position = options.Position ?? (parent != null && !options.WorldPositionStays ? parent.TransformPoint(prefab.transform.localPosition) : prefab.transform.position);
                Quaternion rotation = options.Rotation ?? (parent != null && !options.WorldPositionStays ? parent.rotation * prefab.transform.localRotation : prefab.transform.rotation);
                m_instance = Object.Instantiate(prefab, position, rotation, parent);
            }
            else {
                m_instance = Object.Instantiate(prefab, parent, options.WorldPositionStays);
            }
            if (options.Position.HasValue) { m_instance.transform.position = options.Position.Value; }
            if (options.Rotation.HasValue) { m_instance.transform.rotation = options.Rotation.Value; }
            if (options.Active.HasValue) { m_instance.SetActive(options.Active.Value); }
            State = m_releaseRequested ? InstanceState.Destroying : InstanceState.Ready;
        }

        /// <summary>可重复调用：尚未创建时取消请求，已创建时等待帧末实际销毁，再释放资源引用。</summary>
        public ResourceOperationBase DestroyAsync()
        {
            m_owner.CheckThread();
            if (m_destroyTask != null) {
                return m_destroyTask;
            }
            if (IsReleased) {
                return ResourceOperationBase.CompletedOperation;
            }
            m_releaseRequested = true;
            State = InstanceState.Destroying;
            m_cancellation.Cancel();
            return m_destroyTask = DestroyCoreAsync();
        }

        private async ResourceOperationBase DestroyCoreAsync()
        {
            try { await Operation; }
            catch { /* 创建失败或被取消时，加载流程已负责释放底层 Handle。 */ }
            if (m_instance != null) {
                Object.Destroy(m_instance);
                while (m_instance != null) {
                    await ResourceOperationBase.Yield();
                }
            }
            Finish(InstanceState.Released);
        }

        // 回收前检查 Unity 对象是否已消失，也覆盖从未激活、不会触发 OnDestroy 的实例。
        internal void Poll()
        {
            if (!IsReleased && State == InstanceState.Ready && m_instance == null) {
                Finish(InstanceState.Released);
            }
        }

        private void Finish(InstanceState state)
        {
            if (IsReleased) {
                return;
            }
            IsReleased = true;
            State = state;
            m_asset?.Release();
            m_cancellation.Dispose();
            m_owner.Forget(this);
        }
    }
}

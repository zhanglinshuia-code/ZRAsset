using System;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>由后端显式实现的同步接管能力。不得轮询 PlayerLoop 或同步等待网络请求。</summary>
    public interface ISynchronousResourceOperation
    {
        bool TryCompleteSynchronously();
    }

    internal sealed class MappedResourceOperation<TSource, TResult>: ResourceOperationBase<TResult>, ISynchronousResourceOperation
    {
        private readonly ResourceOperationBase<TSource> m_source;
        private readonly Func<TSource, TResult> m_map;

        internal MappedResourceOperation(ResourceOperationBase<TSource> source, Func<TSource, TResult> map)
        {
            m_source = source;
            m_map = map;
        }

        protected override void OnUpdate()
        {
            Progress = m_source.Progress;
            if (m_source.IsDone) { Succeed(m_map(m_source.Result)); }
        }

        public bool TryCompleteSynchronously()
        {
            ResourcePackages.CheckThread();
            if (!m_source.IsDone && m_source is ISynchronousResourceOperation source) {
                source.TryCompleteSynchronously();
            }
            Tick();
            return IsDone;
        }
    }

    /// <summary>读取 Unity 原生请求结果会接管本地加载；正常异步路径只在 isDone 后读取。</summary>
    internal sealed class UnityResourceRequest<T>: ResourceOperationBase<T>, ISynchronousResourceOperation
    {
        private readonly AsyncOperation m_request;
        private readonly Func<T> m_result;
        private readonly Action m_cleanup;

        internal UnityResourceRequest(AsyncOperation request, Func<T> result, Action cleanup = null)
        {
            m_request = request;
            m_result = result;
            m_cleanup = cleanup;
        }

        protected override void OnUpdate()
        {
            Progress = m_request.progress;
            if (m_request.isDone) { Succeed(m_result()); }
        }

        public bool TryCompleteSynchronously()
        {
            ResourcePackages.CheckThread();
            if (IsDone) { return true; }
            UnitySceneLoadRequest.RequireUnblockedQueue();
            if (Application.platform == RuntimePlatform.WebGLPlayer) { return false; }
            try { Succeed(m_result()); }
            catch (Exception error) { Fail(error); }
            return true;
        }

        protected override void OnCleanup()
        {
            m_cleanup?.Invoke();
        }
    }

    internal sealed class BundleOpenOperation: ResourceOperationBase<object>, ISynchronousResourceOperation
    {
        private readonly ResourceOperationBase<ResourceFileLocation> m_file;
        private readonly BundleInfo m_info;
        private readonly IUnityResourceLoader m_loader;
        private readonly IResourceFileSystem m_files;
        private ResourceOperationBase<object> m_open;

        internal BundleOpenOperation(ResourceOperationBase<ResourceFileLocation> file, BundleInfo info, IUnityResourceLoader loader, IResourceFileSystem files)
        {
            m_file = file;
            m_info = info;
            m_loader = loader;
            m_files = files;
        }

        protected override void OnUpdate()
        {
            if (!m_file.IsDone) { Progress = m_file.Progress * 0.5f; return; }
            m_open ??= m_loader.LoadBundleAsync(m_file.Result, m_info);
            Progress = 0.5f + (m_open.Progress * 0.5f);
            if (m_open.IsDone) { Succeed(m_open.Result); }
        }

        public bool TryCompleteSynchronously()
        {
            ResourcePackages.CheckThread();
            if (!IsDone && m_open == null && !m_file.IsDone && m_files is ISynchronousResourceFileSystem files &&
                m_loader is ISynchronousUnityResourceLoader loader) {
                // 同步解析只能读取已准备文件；原异步校验仍由文件系统排空，且不会再打开第二个 Bundle。
                ResourceFileLocation file = files.Resolve(m_info);
                Succeed(loader.LoadBundle(file, m_info));
                return true;
            }
            Tick();
            if (m_open is ISynchronousResourceOperation open && !m_open.IsDone) { open.TryCompleteSynchronously(); }
            Tick();
            return IsDone;
        }
    }
}

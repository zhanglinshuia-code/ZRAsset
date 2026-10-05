using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ZRAsset
{
    public interface IResourcePersistence
    {
        ResourceOperationBase RestoreAsync();
        ResourceOperationBase FlushAsync();
    }

    /// <summary>在创建浏览器缓存和版本管理器前 InitializeAsync；所有发布操作等待持久化完成。</summary>
    public static class ResourcePersistence
    {
        private static IResourcePersistence s_backend;
        private static ResourceOperationBase s_initialization;
        private static readonly OperationSemaphore s_gate = new(1, 1);
        private static ResourceOperationBase s_queuedFlush;
        private static readonly HashSet<string> s_cacheRoots = new(StringComparer.Ordinal);
        internal static void RegisterCacheRoot(string root)
        {
            if (Application.platform != RuntimePlatform.WebGLPlayer) {
                return;
            }

            root = Path.GetFullPath(root);
            if (Application.platform == RuntimePlatform.WebGLPlayer) {
                var parent = Path.GetFullPath(Application.persistentDataPath).TrimEnd('/') + "/";
                if (!root.StartsWith(parent, StringComparison.Ordinal)) {
                    throw new ArgumentException("WebGL 缓存必须放在 persistentDataPath 的子目录。");
                }
            }
            s_cacheRoots.Add(root);
        }
        internal static string[] CacheRoots
        {
            get
            {
                return s_cacheRoots.OrderBy(p => p, StringComparer.Ordinal).ToArray();
            }
        }

        public static bool IsReady { get; private set; }
#if UNITY_EDITOR
        internal static async ResourceOperationBase ResetEditorSessionAsync()
        {
            if (s_initialization != null) { try { await s_initialization; } catch { } }
            if (s_queuedFlush != null) { try { await s_queuedFlush; } catch { } }
            await s_gate.WaitAsync();
            try {
                s_backend = null;
                s_initialization = null;
                s_queuedFlush = null;
                IsReady = false;
                s_cacheRoots.Clear();
            }
            finally { s_gate.Release(); }
        }
#endif
        public static ResourceOperationBase InitializeAsync(IResourcePersistence storage = null)
        {
            if (storage != null && s_backend != null && !ReferenceEquals(storage, s_backend)) {
                throw new InvalidOperationException("持久化后端已经选定，不能在运行中更换。");
            }

            if (s_initialization != null && (!s_initialization.IsDone || s_initialization.Status == OperationStatus.Succeeded)) {
                return s_initialization;
            }

            s_backend = storage ?? s_backend ?? (Application.platform == RuntimePlatform.WebGLPlayer ? new BrowserPersistence() : null);
            return s_initialization = InitializeCoreAsync();
        }
        private static async ResourceOperationBase InitializeCoreAsync()
        {
            if (s_backend != null) {
                await s_backend.RestoreAsync();
            }

            IsReady = true;
        }
        public static ResourceOperationBase FlushAsync()
        {
            return s_backend == null
                ? ResourceOperationBase.CompletedOperation
                : !IsReady
                ? throw new InvalidOperationException("必须先完成 ResourcePersistence.InitializeAsync。")
                : (s_queuedFlush ??= FlushBatchAsync());
        }
        private static async ResourceOperationBase FlushBatchAsync()
        {
            // 同一批提交共享一次落盘；扫描开始后的新请求进入下一批，不能提前宣告持久化成功。
            await ResourceOperationBase.Yield();
            await s_gate.WaitAsync();
            s_queuedFlush = null;
            try { await s_backend.FlushAsync(); }
            finally { s_gate.Release(); }
        }
        private sealed class BrowserPersistence: IResourcePersistence
        {
            public ResourceOperationBase RestoreAsync()
            {
                return OperationSystem.Start(new BrowserSync(true));
            }

            public ResourceOperationBase FlushAsync()
            {
                return OperationSystem.Start(new BrowserSync(false));
            }
        }
        private sealed class BrowserSync: ResourceOperationBase
        {
            private readonly bool m_restore;
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "The WebGL player bridge updates this state in conditional code.")] private int m_id;
            internal BrowserSync(bool restore) { m_restore = restore; }
            protected override void OnUpdate()
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (m_id == 0) {
                    m_id = ZRAssetSyncBegin(m_restore ? 1 : 0, Application.persistentDataPath);
                }

                var state = ZRAssetSyncPoll(m_id);
                if (state < 0) {
                    throw new IOException("浏览器持久化失败；请检查存储权限、配额、Web Locks 支持及其它占用此游戏存储的标签页。");
                }

                if (state > 0) {
                    Succeed();
                }
#else
                throw new PlatformNotSupportedException("浏览器存储桥接只能在 WebGL Player 使用。");
#endif
            }
        }
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern int ZRAssetSyncBegin(int restore, string root);
        [DllImport("__Internal")] private static extern int ZRAssetSyncPoll(int id);
#endif
    }
}

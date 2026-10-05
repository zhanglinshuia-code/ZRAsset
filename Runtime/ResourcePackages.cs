using System;
using System.Collections.Generic;
using System.Linq;

namespace ZRAsset
{
    /// <summary>主线程上的命名包目录。销毁成功后才能重新创建同名包，不自动清空磁盘缓存。</summary>
    public static class ResourcePackages
    {
        private static readonly Dictionary<string, ResourcePackage> s_packages = new(StringComparer.Ordinal);
#if UNITY_EDITOR
        private static System.Threading.CancellationTokenSource s_playSessionCancellation = new();
        internal static System.Threading.CancellationToken PlaySessionToken
        {
            get
            {
                return UnityEngine.Application.isPlaying ? s_playSessionCancellation.Token : default;
            }
        }

        internal static void BeginEditorPlaySession()
        {
            if (!s_playSessionCancellation.IsCancellationRequested) {
                return;
            }

            s_playSessionCancellation.Dispose();
            s_playSessionCancellation = new System.Threading.CancellationTokenSource();
        }
        internal static ResourceOperationBase SessionShutdown { get; private set; }
        internal static bool SessionClosing
        {
            get
            {
                return SessionShutdown != null && SessionShutdown.Status != OperationStatus.Succeeded;
            }
        }

        internal static ResourceOperationBase ShutdownSessionAsync()
        {
            CheckThread();
            if (SessionShutdown != null && !SessionShutdown.IsDone) {
                return SessionShutdown;
            }

            s_playSessionCancellation.Cancel();
            return SessionShutdown = ShutdownSessionCoreAsync();
        }

        private static async ResourceOperationBase ShutdownSessionCoreAsync()
        {
            // Retain objects until their asynchronous shutdown has released requests and cache leases.
            var operations = new List<ResourceOperationBase>();
            foreach (ResourcePackage package in s_packages.Values.ToArray()) {
                if (package.EditorPlaySession) {
                    operations.Add(package.ShutdownEditorSessionAsync());
                }
            }

            foreach (ResourceManager manager in ResourceDiagnostics.GetPlaySessionManagers()) {
                if (manager.EditorPlaySession && manager.SchedulingPackage == null) {
                    operations.Add(manager.ShutdownEditorSessionAsync());
                }
            }

            await ResourceOperationBase.WhenAll(operations);
            await ResourcePersistence.ResetEditorSessionAsync();
            ResourceTelemetry.Configure(null);
            // Keep the scheduler alive: deferred callbacks/finally blocks must run, never discard their queue.
            await ResourceOperationBase.Yield();
        }
#endif

        internal static void CheckThread()
        {
            if (!OperationSystem.IsMainThread) {
                throw new InvalidOperationException("Package API 必须在 Unity 主线程、操作系统初始化后调用。");
            }
        }

        public static ResourcePackage CreatePackage(string name)
        {
            CheckThread();
#if UNITY_EDITOR
            if (SessionClosing) {
                throw new InvalidOperationException("上一 Play Mode 会话仍在清理资源，请等待清理完成。");
            }
#endif
            ResourcePackageIdentity.ValidateName(name);
            if (s_packages.ContainsKey(name)) {
                throw new InvalidOperationException("Package 已注册：" + name);
            }

            var package = new ResourcePackage(name);
            s_packages.Add(name, package);
            return package;
        }

        public static bool TryGetPackage(string name, out ResourcePackage package)
        {
            CheckThread();
            ResourcePackageIdentity.ValidateName(name);
            return s_packages.TryGetValue(name, out package);
        }

        public static ResourcePackage GetPackage(string name)
        {
            return TryGetPackage(name, out ResourcePackage package)
            ? package : throw new KeyNotFoundException("Package 未注册：" + name);
        }

        public static string[] GetPackageNames()
        {
            CheckThread();
            return s_packages.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        public static ResourceOperationBase DestroyPackageAsync(string name)
        {
            return GetPackage(name).DisposeAsync();
        }

        internal static void Remove(ResourcePackage package)
        {
            if (s_packages.TryGetValue(package.Name, out ResourcePackage current) && ReferenceEquals(current, package)) {
                s_packages.Remove(package.Name);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    /// <summary>可导出的纯数据快照；不保留管理器、使用凭证或 Unity 对象。</summary>
    [Serializable]
    public sealed class ResourceDiagnosticSnapshot
    {
        public int FormatVersion = 1;
        public long ManagerId;
        public string Name, BuildTarget, CapturedAtUtc, State;
        public string PackageName, PackageVersion;
        public ResourceDiagnosticSummary Summary;
        public ResourceAssetDiagnostic[] Assets;
        public ResourceBundleDiagnostic[] Bundles;
        public ResourceInstanceDiagnostic[] Instances;
        public ResourceSceneDiagnostic[] Scenes;
        public ResourceDownloadDiagnostic[] Downloads;
        public ResourceRawFileDiagnostic[] RawFiles;
        public ResourceScopeDiagnostic[] Scopes;

        /// <summary>仅序列化已采集的数据，不会再次访问管理器或触发资源回收。</summary>
        public string ToJson(bool prettyPrint = true)
        {
            return JsonUtility.ToJson(this, prettyPrint);
        }
    }

    [Serializable]
    public sealed class ResourceDiagnosticSummary
    {
        public int Assets, Bundles, Handles, LoadingAssets, Instances, Scenes;
        public int ActiveBundleLoads, QueuedBundleLoads, ActiveAssetLoads, QueuedAssetLoads;
        public int ActiveDownloads, QueuedDownloads;
        public int RawFiles, RawReferences;
    }

    [Serializable]
    public sealed class ResourceRawFileDiagnostic
    {
        public string AssetPath, Container, FileType, State, Error;
        public string Encryption, EncryptionKeyId;
        public string[] Addresses;
        public long Offset, Length;
        public int References;
    }

    [Serializable]
    public sealed class ResourceAssetDiagnostic
    {
        public string AssetPath, AssetType, State, Error, LoadKind, SourceBundle;
        public string[] Addresses, BundleNames;
        public int References;
        public bool Loading, CanCollect;
        public double SecondsUntilCollection;
    }

    [Serializable]
    public sealed class ResourceBundleDiagnostic
    {
        public string Name, State, Error;
        public string Encryption, EncryptionKeyId;
        public long ContentBytes;
        public string[] Dependencies;
        public int References;
        /// <summary>清单中的文件大小，不代表 Unity 内存占用。</summary>
        public long FileBytes;
    }

    [Serializable]
    public sealed class ResourceInstanceDiagnostic
    {
        public string Address, State, Name, Error;
        /// <summary>仅记录采集时的数值 ID，不保存 GameObject 引用。</summary>
        public int InstanceId;
    }

    [Serializable]
    public sealed class ResourceSceneDiagnostic
    {
        public string AssetPath, State, Error;
        public string[] Addresses, BundleNames;
    }

    [Serializable]
    public sealed class ResourceDownloadDiagnostic
    {
        public string BundleName, State, Error;
        public long ReceivedBytes, TotalBytes;
        public int Attempt;
    }

    /// <summary>窗口列表只保存身份和名称，不能通过列表意外延长管理器寿命。</summary>
    public readonly struct ResourceDiagnosticManager
    {
        public readonly long Id;
        public readonly string Name;
        public ResourceDiagnosticManager(long id, string name) { Id = id; Name = name; }
    }

    /// <summary>按需查询的弱引用目录；注册和注销不进入每帧轮询路径。</summary>
    public static class ResourceDiagnostics
    {
        private static readonly object s_gate = new();
        private static readonly Dictionary<long, WeakReference<ResourceManager>> s_managers = new();
#if UNITY_EDITOR
        // A Play session owns managers until disposal, even when business code loses its last reference.
        // Diagnostics alone stay weak; this separate set guarantees teardown of native resources/leases.
        private static readonly HashSet<ResourceManager> s_playSessionManagers = new();
#endif
        private static long s_nextId;

        internal static long Register(ResourceManager manager)
        {
            var id = Interlocked.Increment(ref s_nextId);
            lock (s_gate) {
                RemoveDeadEntries();
                s_managers.Add(id, new WeakReference<ResourceManager>(manager));
#if UNITY_EDITOR
                if (manager.EditorPlaySession) {
                    s_playSessionManagers.Add(manager);
                }
#endif
            }
            return id;
        }

        internal static void Unregister(long id)
        {
            lock (s_gate) {
#if UNITY_EDITOR
                if (s_managers.TryGetValue(id, out WeakReference<ResourceManager> weak) && weak.TryGetTarget(out ResourceManager manager)) {
                    s_playSessionManagers.Remove(manager);
                }
#endif
                s_managers.Remove(id);
            }
        }
#if UNITY_EDITOR
        internal static ResourceManager[] GetPlaySessionManagers()
        {
            lock (s_gate) {
                return s_playSessionManagers.ToArray();
            }
        }
#endif

        /// <summary>返回独立的目录数据；已关闭或已被 GC 回收的管理器不会出现。</summary>
        public static ResourceDiagnosticManager[] GetManagers()
        {
            lock (s_gate) {
                RemoveDeadEntries();
                var result = new List<ResourceDiagnosticManager>(s_managers.Count);
                foreach (KeyValuePair<long, WeakReference<ResourceManager>> entry in s_managers) {
                    if (entry.Value.TryGetTarget(out ResourceManager manager)) {
                        result.Add(new ResourceDiagnosticManager(entry.Key, manager.DiagnosticName));
                    }
                }

                result.Sort((a, b) => a.Id.CompareTo(b.Id));
                return result.ToArray();
            }
        }

        /// <summary>须在被采集管理器的 Unity 主线程调用；对象已消失时返回 false。</summary>
        public static bool TryCapture(long id, out ResourceDiagnosticSnapshot snapshot)
        {
            ResourceManager manager;
            lock (s_gate) {
                if (!s_managers.TryGetValue(id, out WeakReference<ResourceManager> weak) || !weak.TryGetTarget(out manager)) {
                    s_managers.Remove(id);
                    snapshot = null;
                    return false;
                }
            }
            snapshot = manager.CaptureDiagnostics();
            return true;
        }

        private static void RemoveDeadEntries()
        {
            List<long> dead = null;
            foreach (KeyValuePair<long, WeakReference<ResourceManager>> entry in s_managers) {
                if (!entry.Value.TryGetTarget(out _)) {
                    (dead ??= new List<long>()).Add(entry.Key);
                }
            }

            if (dead != null) {
                foreach (var id in dead) {
                    s_managers.Remove(id);
                }
            }
        }
    }

    public sealed partial class ResourceManager
    {
        private string m_diagnosticName, m_diagnosticBuildTarget;
        private bool m_diagnosticsDisposed;

        public long DiagnosticId { get; private set; }

        /// <summary>业务可设置可读名称，例如“大厅”或“战斗”；名称不会改变资源身份。</summary>
        public string DiagnosticName
        {
            get
            {
                return m_diagnosticName;
            }

            set
            {
                CheckThread();
                m_diagnosticName = string.IsNullOrWhiteSpace(value) ? "资源管理器 " + DiagnosticId : value;
            }
        }

        private void RegisterDiagnostics(string buildTarget)
        {
            m_diagnosticBuildTarget = buildTarget;
            DiagnosticId = ResourceDiagnostics.Register(this);
            DiagnosticName = null;
        }

        private void UnregisterDiagnostics()
        {
            m_diagnosticsDisposed = true;
            ResourceDiagnostics.Unregister(DiagnosticId);
        }

        /// <summary>
        /// 主线程按需采集，适合手动检查或低频采样，会分配 DTO 和数组。
        /// 只读取当前记录，不轮询生命周期、不加载资源、不增加引用，也不强制回收。
        /// </summary>
        public ResourceDiagnosticSnapshot CaptureDiagnostics()
        {
            CheckThread();
            var now = Time.realtimeSinceStartupAsDouble;
            int handles = 0, loading = 0;
            var assetItems = new List<ResourceAssetDiagnostic>(m_assetCache.Count);
            foreach (AssetProvider provider in m_assetCache.Values) {
                handles += provider.References;
                var isLoading = provider.Operation == null || !provider.Operation.IsDone;
                if (isLoading) {
                    loading++;
                }

                var remaining = provider.References == 0 && !isLoading
                    ? Math.Max(0, m_unloadDelay - (now - provider.ReleasedAt)) : 0;
                assetItems.Add(new ResourceAssetDiagnostic
                {
                    AssetPath = provider.Key.Item3 == ResourceAssetLoadKind.AllAssets ? null : provider.Key.Item1,
                    SourceBundle = provider.Key.Item3 == ResourceAssetLoadKind.AllAssets ? provider.Key.Item1 : null,
                    LoadKind = provider.Key.Item3.ToString(),
                    AssetType = provider.Key.Item2.FullName,
                    Addresses = provider.Key.Item3 == ResourceAssetLoadKind.AllAssets ? m_assets.Values
                        .Where(a => a.Kind == ResourceKind.Asset && a.BundleName == provider.Key.Item1).Select(a => a.Address).OrderBy(a => a, StringComparer.Ordinal).ToArray() :
                        DiagnosticAddresses(provider.Key.Item1, ResourceKind.Asset),
                    BundleNames = provider.Bundles.Select(b => b.Info.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                    References = provider.References,
                    State = provider.State.ToString(),
                    Error = DiagnosticError(provider.Operation),
                    Loading = isLoading,
                    CanCollect = provider.References == 0 && !isLoading && (provider.State == ProviderState.Failed || remaining == 0),
                    SecondsUntilCollection = provider.State == ProviderState.Failed ? 0 : remaining
                });
            }
            assetItems.Sort((a, b) =>
            {
                var path = string.Compare(a.AssetPath ?? a.SourceBundle, b.AssetPath ?? b.SourceBundle, StringComparison.Ordinal);
                if (path != 0) {
                    return path;
                }

                var type = string.Compare(a.AssetType, b.AssetType, StringComparison.Ordinal);
                return type != 0 ? type : string.Compare(a.LoadKind, b.LoadKind, StringComparison.Ordinal);
            });
            ResourceBundleDiagnostic[] bundleItems = m_bundleCache.Values.Select(provider => new ResourceBundleDiagnostic
            {
                Name = provider.Info.Name,
                Encryption = provider.Info.Encryption,
                EncryptionKeyId = provider.Info.EncryptionKeyId,
                ContentBytes = provider.Info.ContentSize,
                References = provider.References,
                FileBytes = provider.Info.Size,
                Dependencies = provider.Info.Dependencies.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                State = DiagnosticTaskState(provider.Operation),
                Error = DiagnosticError(provider.Operation)
            }).OrderBy(b => b.Name, StringComparer.Ordinal).ToArray();
            var instanceItems = new List<ResourceInstanceDiagnostic>(m_instances.Count);
            foreach (InstanceHandle handle in m_instances) {
                GameObject instance = handle.Operation?.Status == OperationStatus.Succeeded ? handle.Operation.Result : null;
                instanceItems.Add(new ResourceInstanceDiagnostic
                {
                    Address = handle.Address,
                    State = handle.State.ToString(),
                    Name = instance ? instance.name : null,
                    InstanceId = instance != null ? instance.GetInstanceID() : 0,
                    Error = DiagnosticError(handle.Operation)
                });
            }
            instanceItems.Sort((a, b) =>
            {
                var address = string.Compare(a.Address, b.Address, StringComparison.Ordinal);
                return address != 0 ? address : a.InstanceId.CompareTo(b.InstanceId);
            });
            var sceneItems = new List<ResourceSceneDiagnostic>(m_scenes.Count);
            foreach (SceneHandle handle in m_scenes.Values) {
                var closure = new HashSet<string>(StringComparer.Ordinal);
                foreach (AssetInfo info in m_assets.Values) {
                    if (info.Kind == ResourceKind.Scene && info.AssetPath == handle.AssetPath) {
                        closure.UnionWith(m_dependencyGraph.GetAssetClosure(info));
                    }
                }

                sceneItems.Add(new ResourceSceneDiagnostic
                {
                    AssetPath = handle.AssetPath,
                    Addresses = DiagnosticAddresses(handle.AssetPath, ResourceKind.Scene),
                    BundleNames = closure.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                    State = handle.State.ToString(),
                    Error = handle.Error == null ? DiagnosticError(handle.Operation) : DiagnosticException(handle.Error)
                });
            }
            sceneItems.Sort((a, b) => string.Compare(a.AssetPath, b.AssetPath, StringComparison.Ordinal));
            IReadOnlyList<BundleDownloadProgress> downloads = DownloadQueue?.GetProgress();
            return new ResourceDiagnosticSnapshot
            {
                ManagerId = DiagnosticId,
                Name = m_diagnosticName,
                BuildTarget = m_diagnosticBuildTarget,
                PackageName = PackageName,
                PackageVersion = PackageVersion,
                CapturedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Scopes = CaptureScopeDiagnostics(),
                State = m_diagnosticsDisposed ? "Disposed" : m_closing ? "Closing" : "Active",
                Summary = new ResourceDiagnosticSummary
                {
                    Assets = m_assetCache.Count,
                    Bundles = m_bundleCache.Count,
                    Handles = handles,
                    LoadingAssets = loading,
                    Instances = m_instances.Count,
                    Scenes = m_scenes.Count,
                    RawFiles = m_rawFiles.Count,
                    RawReferences = m_rawFiles.Values.Sum(file => file.References),
                    ActiveBundleLoads = m_bundleLoads.Active,
                    QueuedBundleLoads = m_bundleLoads.Queued,
                    ActiveAssetLoads = m_assetLoads.Active,
                    QueuedAssetLoads = m_assetLoads.Queued,
                    ActiveDownloads = DownloadQueue?.ActiveDownloads ?? 0,
                    QueuedDownloads = DownloadQueue?.QueuedDownloads ?? 0
                },
                Assets = assetItems.ToArray(),
                Bundles = bundleItems,
                Instances = instanceItems.ToArray(),
                Scenes = sceneItems.ToArray(),
                RawFiles = m_rawFiles.Values.OrderBy(file => file.Asset.AssetPath, StringComparer.Ordinal).Select(file => new ResourceRawFileDiagnostic
                {
                    AssetPath = file.Asset.AssetPath,
                    Container = file.Asset.BundleName,
                    FileType = file.Asset.FileType.ToString(),
                    Encryption = m_catalog[file.Asset.BundleName].Encryption,
                    EncryptionKeyId = m_catalog[file.Asset.BundleName].EncryptionKeyId,
                    Addresses = DiagnosticAddresses(file.Asset.AssetPath, ResourceKind.RawFile),
                    References = file.References,
                    Offset = file.Asset.FileOffset,
                    Length = file.Asset.FileSize,
                    State = DiagnosticTaskState(file.Operation),
                    Error = DiagnosticError(file.Operation)
                }).ToArray(),
                Downloads = downloads == null ? Array.Empty<ResourceDownloadDiagnostic>() : downloads.Select(d => new ResourceDownloadDiagnostic
                {
                    BundleName = d.BundleName,
                    State = d.State.ToString(),
                    Error = d.Error,
                    ReceivedBytes = d.ReceivedBytes,
                    TotalBytes = d.TotalBytes,
                    Attempt = d.Attempt
                }).OrderBy(d => d.BundleName, StringComparer.Ordinal).ToArray()
            };
        }

        private string[] DiagnosticAddresses(string path, ResourceKind kind)
        {
            return m_assets.Values
            .Where(a => a.Kind == kind && string.Equals(a.AssetPath, path, StringComparison.Ordinal))
            .Select(a => a.Address).OrderBy(a => a, StringComparer.Ordinal).ToArray();
        }

        private static string DiagnosticTaskState(ResourceOperationBase task)
        {
            return task == null || !task.IsDone
            ? "Loading" : task.IsCanceled ? "Canceled" : task.IsFaulted ? "Failed" : "Succeeded";
        }

        private static string DiagnosticError(ResourceOperationBase task)
        {
            return task?.Exception == null ? null : DiagnosticException(task.Exception.GetBaseException());
        }

        private static string DiagnosticException(Exception error)
        {
            return error.GetType().FullName + ": " + error.Message;
        }
    }
}

using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System;
using UnityEngine;

namespace ZRAsset
{
    public enum MiniGameHost { Wechat, Douyin, Alipay, QuickGame, Kuaishou }
    /// <summary>使用宿主文件管理器保存 MEMFS 快照；必须在创建任何 Package 前初始化。</summary>
    public sealed class MiniGamePersistence: IResourcePersistence
    {
        [Serializable] private sealed class PersistenceRoots { public string[] Roots; }
        private readonly MiniGameHost m_host;
        private readonly string m_namespaceName, m_platformRoot;
        public MiniGamePersistence(MiniGameHost host, string namespaceName = "zrasset", string platformRoot = null)
        {
            if (!Enum.IsDefined(typeof(MiniGameHost), host) || string.IsNullOrEmpty(namespaceName) || namespaceName.Length > 64 ||
                namespaceName.Any(c => !((char.IsLetterOrDigit(c) && c < 128) || c == '_' || c == '-'))) {
                throw new ArgumentException("小游戏宿主或存储命名空间无效。");
            }

            m_host = host; m_namespaceName = namespaceName; m_platformRoot = platformRoot ?? "";
        }
        public ResourceOperationBase RestoreAsync()
        {
            return OperationSystem.Start(new Sync(this, true));
        }

        public ResourceOperationBase FlushAsync()
        {
            return OperationSystem.Start(new Sync(this, false));
        }

        private sealed class Sync: ResourceOperationBase
        {
            private readonly MiniGamePersistence m_owner; private readonly bool m_restore; [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "The WebGL player bridge updates this state in conditional code.")] private int m_id;
            internal Sync(MiniGamePersistence owner, bool restore) { m_owner = owner; m_restore = restore; }
            protected override void OnUpdate()
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (m_id == 0) {
                    m_id = ZRAssetMiniSync((int)m_owner.m_host, m_owner.m_namespaceName, m_owner.m_platformRoot, Application.persistentDataPath, m_restore ? 1 : 0,
                    JsonUtility.ToJson(new PersistenceRoots { Roots = ResourcePersistence.CacheRoots }));
                }

                var state = ZRAssetMiniPoll(m_id);
                if (state < 0) {
                    throw new System.IO.IOException("小游戏持久化失败，请检查宿主文件 API、存储路径和配额。");
                }

                if (state > 0) {
                    Succeed();
                }
#else
                throw new PlatformNotSupportedException("小游戏桥接只在转换后的 WebGL Player 中运行。");
#endif
            }
        }
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern int ZRAssetMiniSync(int host, string space, string sdkRoot, string root, int restore, string roots);
        [DllImport("__Internal")] private static extern int ZRAssetMiniPoll(int id);
#endif
    }
    /// <summary>SDK downloadFile 适配；SDK 不提供可验证 Range 响应时安全地整文件重下。</summary>
    public sealed class MiniGameDownloadTransport: IDownloadTransport
    {
        private readonly MiniGameHost m_host;
        public bool HasReliableProgress { get; }
        public int MaximumFallbackCopyBytes { get; }
        /// <summary>只有经 SDK 验证的字节进度才能启用停滞看门狗；无分块文件 API 时限制整文件复制大小。</summary>
        public MiniGameDownloadTransport(MiniGameHost host, bool hasReliableProgress = false, int maximumFallbackCopyBytes = 8 * 1024 * 1024)
        {
            if (!Enum.IsDefined(typeof(MiniGameHost), host)) {
                throw new ArgumentOutOfRangeException(nameof(host));
            }

            if (maximumFallbackCopyBytes < 0 || maximumFallbackCopyBytes > 32 * 1024 * 1024) {
                throw new ArgumentOutOfRangeException(nameof(maximumFallbackCopyBytes));
            }

            m_host = host;
            HasReliableProgress = hasReliableProgress;
            MaximumFallbackCopyBytes = maximumFallbackCopyBytes;
        }
        public ResourceOperationBase<DownloadTransportResponse> SendAsync(DownloadTransportRequest request, Action<long> progress, CancellationToken token)
        {
            return OperationSystem.Start(new Transfer(this, request, progress, token));
        }

        [Serializable] private sealed class Header { public string Key, Value; }
        [Serializable] private sealed class Headers { public Header[] Items; }
        [Serializable] private sealed class Details { public int StatusCode; public string Error, RetryAfter; }
        internal static bool ShouldAbort(bool canceled, double totalSeconds, int timeoutSeconds, bool reliableProgress,
            bool copying, double idleSeconds, double watchdogSeconds)
        {
            return canceled || totalSeconds > timeoutSeconds ||
            (reliableProgress && !copying && watchdogSeconds > 0 && idleSeconds > watchdogSeconds);
        }

        private sealed class Transfer: ResourceOperationBase<DownloadTransportResponse>
        {
            private readonly MiniGameDownloadTransport m_owner; private readonly DownloadTransportRequest m_request; private readonly Action<long> m_progress;
            private readonly CancellationToken m_token; [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "The WebGL player bridge updates this state in conditional code.")] private int m_id; [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "The WebGL player bridge updates this state in conditional code.")] private bool m_canceled, m_stalled;
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "The WebGL player bridge updates this state in conditional code.")] private double m_received; private readonly System.Diagnostics.Stopwatch m_clock = new(), m_watchdog = new();
            internal Transfer(MiniGameDownloadTransport owner, DownloadTransportRequest request, Action<long> progress, CancellationToken token)
            { m_owner = owner; m_request = request; m_progress = progress; m_token = token; }
            protected override void OnUpdate()
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (m_id == 0) {
                    m_token.ThrowIfCancellationRequested();
                    if (m_request.NetworkPolicy.ConfigureRequest != null || m_request.NetworkPolicy.ConfigureRequestWithContext != null) {
                        throw new NotSupportedException("小游戏 SDK 不使用 UnityWebRequest 配置回调。");
                    }

                    var headers = JsonUtility.ToJson(new Headers { Items = m_request.NetworkPolicy.Headers.Where(_ => m_request.NetworkPolicy.AllowsCredentials(m_request.Url, m_request.OriginalUrl)).Select(p => new Header { Key = p.Key, Value = p.Value }).ToArray() });
                    m_id = ZRAssetMiniDownload((int)m_owner.m_host, m_request.Url, m_request.OutputPath, m_request.MaximumBytes, headers, m_owner.MaximumFallbackCopyBytes);
                    m_clock.Start(); m_watchdog.Start();
                }
                var bytes = ZRAssetMiniBytes(m_id);
                if (bytes != m_received) { m_received = bytes; m_watchdog.Restart(); }
                m_progress?.Invoke((long)m_received);
                if (ShouldAbort(m_token.IsCancellationRequested, m_clock.Elapsed.TotalSeconds, m_request.TimeoutSeconds,
                    m_owner.HasReliableProgress, ZRAssetMiniCopying(m_id) != 0, m_watchdog.Elapsed.TotalSeconds, m_request.NetworkPolicy.WatchdogTimeoutSeconds)) { m_canceled = m_token.IsCancellationRequested; m_stalled = !m_canceled; ZRAssetMiniAbort(m_id); }
                if (ZRAssetMiniState(m_id) == 0) {
                    return;
                }

                var buffer = new byte[2048];
                ZRAssetMiniDetails(m_id, buffer, buffer.Length);
                var length = Array.IndexOf(buffer, (byte)0);
                Details details = JsonUtility.FromJson<Details>(Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length));
                var state = ZRAssetMiniDownloadPoll(m_id);
                m_id = 0;
                Succeed(new DownloadTransportResponse
                {
                    StatusCode = details?.StatusCode ?? 0,
                    Cancelled = m_canceled,
                    RetryAfter = details?.RetryAfter,
                    ExceededLimit = state == -2,
                    Error = state == 1 ? null : m_stalled ? "小游戏下载超时。" : details?.Error ?? "小游戏 SDK 下载失败。"
                });
#else
                throw new PlatformNotSupportedException("小游戏下载桥接只在转换后的 WebGL Player 中运行。");
#endif
            }
            protected override void OnCleanup()
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (m_id != 0) { ZRAssetMiniAbort(m_id); ZRAssetMiniDownloadPoll(m_id); m_id = 0; }
#endif
            }
        }
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern int ZRAssetMiniDownload(int host, string url, string output, double maximum, string headers, int fallbackMaximum);
        [DllImport("__Internal")] private static extern double ZRAssetMiniBytes(int id);
        [DllImport("__Internal")] private static extern int ZRAssetMiniState(int id);
        [DllImport("__Internal")] private static extern int ZRAssetMiniCopying(int id);
        [DllImport("__Internal")] private static extern void ZRAssetMiniDetails(int id, [Out] byte[] buffer, int capacity);
        [DllImport("__Internal")] private static extern void ZRAssetMiniAbort(int id);
        [DllImport("__Internal")] private static extern int ZRAssetMiniDownloadPoll(int id);
#endif
    }
}

using System;
using System.Diagnostics;

namespace ZRAsset
{
    public enum ResourceTelemetryKind { AssetLoad, Download, Startup, Recovery, ScopeRelease, RawFileLoad, SceneLoad, ProviderReuse, ManagedException, CacheHit, CacheMiss }

    /// <summary>不包含 URL、请求头、密钥或异常消息；地址字段只应使用项目逻辑标识。</summary>
    public readonly struct ResourceTelemetryEvent
    {
        public ResourceTelemetryKind Kind { get; }
        public string PackageName { get; }
        public string PackageVersion { get; }
        public string Address { get; }
        public ResourceFailure Failure { get; }
        public double DurationMilliseconds { get; }
        public long Bytes { get; }
        public int Attempt { get; }

        public ResourceTelemetryEvent(ResourceTelemetryKind kind, string packageName = null, string packageVersion = null,
            string address = null, ResourceFailure failure = default, double durationMilliseconds = 0, long bytes = 0, int attempt = 0)
        {
            Kind = kind;
            PackageName = packageName;
            PackageVersion = packageVersion;
            Address = address;
            Failure = failure;
            DurationMilliseconds = durationMilliseconds;
            Bytes = bytes;
            Attempt = attempt;
        }
    }

    public interface IResourceTelemetrySink
    {
        void Emit(in ResourceTelemetryEvent value);
    }

    public interface IResourceTelemetryPump
    {
        void Update();
    }

    /// <summary>主线程有界环形队列。关闭时不分配；不在资源回调中执行上报器。</summary>
    public static class ResourceTelemetry
    {
        private static IResourceTelemetrySink s_sink;
        private static ResourceTelemetryEvent[] s_buffer;
        private static int s_head;
        private static int s_successSampleEvery;
        private static int s_sampleCounter;
        private static int s_maxPerUpdate;
        private static bool s_dispatching;

        public static bool Enabled { get { return s_sink != null; } }
        public static int PendingCount { get; private set; }
        public static long DroppedCount { get; private set; }
        public static long SinkFailureCount { get; private set; }

        public static void Configure(IResourceTelemetrySink sink, int capacity = 256, int successSampleEvery = 1, int maxPerUpdate = 32)
        {
            ResourcePackages.CheckThread();
            if (s_dispatching) {
                throw new InvalidOperationException("不能在遥测回调中重新配置上报器。");
            }
            if (capacity < 1 || capacity > 65536 || successSampleEvery < 1 || maxPerUpdate < 1 || maxPerUpdate > capacity) {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            s_sink = sink;
            s_buffer = sink == null ? null : new ResourceTelemetryEvent[capacity];
            s_head = 0;
            PendingCount = 0;
            s_sampleCounter = 0;
            s_successSampleEvery = successSampleEvery;
            s_maxPerUpdate = maxPerUpdate;
            DroppedCount = 0;
            SinkFailureCount = 0;
        }

        public static void Record(in ResourceTelemetryEvent value)
        {
            if (s_sink == null) {
                return;
            }
            ResourcePackages.CheckThread();
            if (value.Failure.Code == ResourceErrorCode.None) {
                if (++s_sampleCounter < s_successSampleEvery) {
                    return;
                }
                s_sampleCounter = 0;
            }
            if (PendingCount == s_buffer.Length) {
                // 丢弃最旧事件，保持固定空间；失败不采样，但仍受容量上限约束。
                s_buffer[s_head] = default;
                s_head = (s_head + 1) % s_buffer.Length;
                PendingCount--;
                DroppedCount++;
            }
            s_buffer[(s_head + PendingCount) % s_buffer.Length] = value;
            PendingCount++;
        }

        public static void Flush()
        {
            ResourcePackages.CheckThread();
            Dispatch();
        }

        internal static void Dispatch()
        {
            if (s_sink == null || s_dispatching) {
                return;
            }
            s_dispatching = true;
            try {
                var remaining = Math.Min(PendingCount, s_maxPerUpdate);
                while (remaining-- > 0 && PendingCount > 0) {
                    ResourceTelemetryEvent value = s_buffer[s_head];
                    s_buffer[s_head] = default;
                    s_head = (s_head + 1) % s_buffer.Length;
                    PendingCount--;
                    try {
                        s_sink.Emit(in value);
                    }
                    catch (Exception) {
                        SinkFailureCount++;
                    }
                }
                try {
                    if (s_sink is IResourceTelemetryPump pump) {
                        pump.Update();
                    }
                }
                catch (Exception) {
                    SinkFailureCount++;
                }
            }
            finally {
                s_dispatching = false;
            }
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            s_sink = null;
            s_buffer = null;
            s_head = 0;
            PendingCount = 0;
            s_dispatching = false;
            DroppedCount = 0;
            SinkFailureCount = 0;
        }

        internal static long StartTimer()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0;
        }

        internal static double Elapsed(long start)
        {
            return start == 0 ? 0 : (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
        }
    }
}

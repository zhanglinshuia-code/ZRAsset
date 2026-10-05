using System;
using System.Collections.Generic;
using System.IO;

namespace ZRAsset
{
    public sealed class ResourceInsufficientSpaceException: IOException
    {
        public long RequiredBytes { get; }
        public long AvailableBytes { get; }
        public ResourceInsufficientSpaceException(long required, long available, Exception inner = null)
            : base($"资源缓存空间不足：需要 {required} 字节，可用 {available} 字节。", inner)
        { RequiredBytes = required; AvailableBytes = available; }
    }

    /// <summary>空间预检与同进程写入预留。探针返回负数表示平台无法查询；实际写入仍受字节上限保护。</summary>
    public sealed class ResourceDiskPolicy
    {
        private static readonly Dictionary<string, List<Reservation>> s_reservations = new(DownloadStorage.PathComparer);
        private static readonly object s_gate = new();
        public long MinimumFreeBytes { get; }
        private readonly Func<string, long> m_probe;
        public ResourceDiskPolicy(long minimumFreeBytes = 32L * 1024 * 1024, Func<string, long> availableBytes = null)
        {
            if (minimumFreeBytes < 0) {
                throw new ArgumentOutOfRangeException(nameof(minimumFreeBytes));
            }

            MinimumFreeBytes = minimumFreeBytes; m_probe = availableBytes ?? Probe;
        }

        internal IDisposable Reserve(string directory, long bytes, params (string Path, long ExistingBytes)[] writes)
        {
            if (bytes < 0) {
                throw new ArgumentOutOfRangeException(nameof(bytes));
            }

            var tracked = new (string Path, long ExistingBytes)[writes?.Length ?? 0];
            var paths = new HashSet<string>(DownloadStorage.PathComparer);
            for (var index = 0; index < tracked.Length; index++) {
                if (writes[index].ExistingBytes < 0) {
                    throw new ArgumentOutOfRangeException(nameof(writes));
                }

                var path = DownloadStorage.ValidatePath(directory, writes[index].Path);
                if (!paths.Add(path)) {
                    throw new ArgumentException("不能重复统计同一个写入文件。", nameof(writes));
                }

                tracked[index] = (path, writes[index].ExistingBytes);
            }
            var key = Path.GetPathRoot(Path.GetFullPath(directory));
            lock (s_gate) {
                s_reservations.TryGetValue(key, out List<Reservation> active);
                long reserved = 0;
                // 先取已落盘文件长度，再查实时可用空间。写入增长已反映在 free 中，不能重复预留。
                // 无需逐块回调或相信网络进度；第三方传输和后台文件复制同样受此统计约束。
                if (active != null) {
                    foreach (Reservation item in active) {
                        reserved = checked(reserved + item.RemainingBytes());
                    }
                }

                var required = checked(bytes + reserved + MinimumFreeBytes);
                var free = m_probe(directory);
                if (free >= 0 && free < required) {
                    throw new ResourceInsufficientSpaceException(required, free);
                }

                var reservation = new Reservation(key, bytes, tracked);
                if (active == null) {
                    s_reservations[key] = active = new List<Reservation>();
                }

                active.Add(reservation);
                return reservation;
            }
        }
        private sealed class Reservation: IDisposable
        {
            private string m_key; private readonly long m_bytes;
            private readonly (string Path, long ExistingBytes)[] m_writes;
            internal Reservation(string key, long bytes, (string Path, long ExistingBytes)[] writes)
            { m_key = key; m_bytes = bytes; m_writes = writes; }
            internal long RemainingBytes()
            {
                long written = 0;
                try {
                    foreach ((string Path, long ExistingBytes) item in m_writes) {
                        DownloadStorage.RejectLinks(item.Path);
                        var file = new FileInfo(item.Path);
                        if (file.Exists) {
                            written += Math.Min(m_bytes - written, Math.Max(0, file.Length - item.ExistingBytes));
                        }
                    }
                    return m_bytes - written;
                }
                // 不可测量时保留全部额度；文件被删除/截短后，下次预检会重新增加剩余预留。
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return m_bytes; }
            }
            public void Dispose()
            {
                lock (s_gate) {
                    if (m_key == null) {
                        return;
                    }

                    List<Reservation> active = s_reservations[m_key];
                    active.Remove(this);
                    if (active.Count == 0) {
                        s_reservations.Remove(m_key);
                    }

                    m_key = null;
                }
            }
        }
        private static long Probe(string directory)
        {
            directory = Path.GetFullPath(directory);
            while (!Directory.Exists(directory)) {
                directory = Path.GetDirectoryName(directory);
                if (string.IsNullOrEmpty(directory)) {
                    throw new DirectoryNotFoundException("缓存目录没有可访问的父目录。");
                }
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            return -1; // 浏览器配额由 IndexedDB 发布结果确认。
#elif UNITY_ANDROID && !UNITY_EDITOR
            using var stat = new UnityEngine.AndroidJavaObject("android.os.StatFs", directory);
            return stat.Call<long>("getAvailableBytes");
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            // DriveInfo 构造函数在 Windows IL2CPP 调用不支持的 GetDriveFormat。
            // 查询实际目录及调用方可用量，以尊重挂载点和用户磁盘配额。
            if (!GetDiskFreeSpaceEx(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                out var available, out _, out _)) {
                var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (error == 5) {
                    throw new UnauthorizedAccessException("无法查询缓存目录的可用空间。");
                }

                throw new IOException("无法查询缓存目录的可用空间，Windows 错误码：" + error,
                    unchecked((int)(0x80070000u | (uint)error)));
            }
            return available > long.MaxValue ? long.MaxValue : (long)available;
#else
            try { return new DriveInfo(Path.GetPathRoot(directory)).AvailableFreeSpace; }
            catch (PlatformNotSupportedException) { return -1; }
#endif
        }
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
#endif
        internal static bool IsDiskFull(IOException error)
        {
            return (error.HResult & 0xffff) == 112 || (error.HResult & 0xffff) == 39 || (error.HResult & 0xffff) == 28;
        }
    }
}

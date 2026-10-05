using System;
using System.Collections.Generic;
using System.IO;

namespace ZRAsset
{
    /// <summary>补充浏览器虚拟文件系统可能忽略的共享锁；原生平台仍保留操作系统文件锁。</summary>
    internal sealed class ResourceFileLock: IDisposable
    {
        private static readonly Dictionary<string, int> s_owners = new(DownloadStorage.PathComparer);
        private readonly string m_path;
        private FileStream m_stream;
        private ResourceFileLock(string path, FileStream stream) { m_path = path; m_stream = stream; }
        internal static ResourceFileLock Open(string path, bool shared = false)
        {
            path = Path.GetFullPath(path);
            lock (s_owners) {
                s_owners.TryGetValue(path, out var count);
                if (count != 0 && (!shared || count < 0)) {
                    throw new IOException("缓存锁正在使用。", unchecked((int)0x80070020));
                }

                var stream = new FileStream(path, FileMode.OpenOrCreate, shared ? FileAccess.Read : FileAccess.ReadWrite, shared ? FileShare.Read : FileShare.None);
                s_owners[path] = shared ? count + 1 : -1;
                return new ResourceFileLock(path, stream);
            }
        }
        public void Dispose()
        {
            lock (s_owners) {
                if (m_stream == null) {
                    return;
                }

                try { m_stream.Dispose(); }
                finally {
                    m_stream = null; if (s_owners[m_path] <= 1) {
                        s_owners.Remove(m_path);
                    }
                    else {
                        s_owners[m_path]--;
                    }
                }
            }
        }
    }
}

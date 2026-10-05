using System;
using System.IO;
using System.Threading;

namespace ZRAsset
{
    /// <summary>可接入 Android AssetManager、第三方 APK 读取库或宿主包内文件 API。</summary>
    public interface IResourceBuiltinFileAccessor
    {
        bool Exists(string location);
        Stream OpenRead(string location);
    }
    public sealed class BuiltinAccessorTransport: IDownloadTransport
    {
        private readonly IResourceBuiltinFileAccessor m_accessor;
        public BuiltinAccessorTransport(IResourceBuiltinFileAccessor accessor) { m_accessor = accessor ?? throw new ArgumentNullException(nameof(accessor)); }
        public async ResourceOperationBase<DownloadTransportResponse> SendAsync(DownloadTransportRequest request,
            Action<long> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Offset != 0) {
                throw new NotSupportedException("首包访问器只支持完整文件提取。");
            }

            if (!m_accessor.Exists(request.Url)) {
                throw new FileNotFoundException("宿主首包文件不存在。", request.Url);
            }

            using Stream input = m_accessor.OpenRead(request.Url) ?? throw new IOException("首包访问器返回空流。");
            if (!input.CanRead) {
                throw new IOException("首包流不可读取。");
            }

            using var output = new FileStream(request.OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var buffer = new byte[64 * 1024]; long total = 0;
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                var count = input.Read(buffer, 0, buffer.Length);
                if (count == 0) {
                    break;
                }

                if (count > request.MaximumBytes - total) {
                    return new DownloadTransportResponse { ExceededLimit = true, Error = "首包流超过清单长度。" };
                }

                output.Write(buffer, 0, count); total += count; progress?.Invoke(total);
                await ResourceOperationBase.Yield();
            }
            return new DownloadTransportResponse { StatusCode = 200, ContentLength = total };
        }
    }
}

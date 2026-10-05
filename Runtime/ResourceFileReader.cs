using System;
using System.IO;
using System.Threading;
using System.Text;
using UnityEngine.Networking;

namespace ZRAsset
{
    /// <summary>普通清单/文件读取入口。签名发布仍使用它自己的大小上限、重定向和信任校验流程。</summary>
    public static class ResourceFileReader
    {
        // Includes base64/encryption overhead of a 64 MiB encoded manifest.
        public const int MaximumTextBytes = 96 * 1024 * 1024;
        public static FileStream OpenRead(ResourceFileLocation file)
        {
            RequireSynchronousRead(file);
            return new FileStream(file.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public static string ReadText(ResourceFileLocation file)
        {
            using FileStream stream = OpenRead(file);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static void RequireSynchronousRead(ResourceFileLocation file)
        {
            if (file == null) {
                throw new ArgumentNullException(nameof(file));
            }

            if ((file.Capabilities & ResourceFileCapabilities.SynchronousRead) == 0) {
                throw new NotSupportedException("该文件来源不支持同步读取，请使用异步读取：" + file.Kind);
            }
        }

        public static ResourceOperationBase<string> ReadTextAsync(string location, CancellationToken cancellationToken = default,
            ResourceDownloadPolicy networkPolicy = null)
        {
            return ReadTextAsync(location, MaximumTextBytes, cancellationToken, networkPolicy);
        }

        /// <summary>在接收及本地读取阶段限制字节数；不信任 Content-Length，也限制分块响应。</summary>
        public static async ResourceOperationBase<string> ReadTextAsync(string location, int maximumBytes, CancellationToken cancellationToken = default,
            ResourceDownloadPolicy networkPolicy = null)
        {
            if (maximumBytes < 0 || maximumBytes > MaximumTextBytes) {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var file = new ResourceFileLocation(location);
            byte[] bytes;
            if (file.LocalPath != null) {
                using FileStream input = OpenRead(file);
                if (input.Length > maximumBytes) {
                    throw new InvalidDataException("清单超过读取上限。");
                }

                bytes = await ResourceFileIO.Shared.ReadRawBytesAsync(input, maximumBytes, cancellationToken);
                return input.Position != input.Length
                    ? throw new InvalidDataException("清单在读取期间发生变化。")
                    : await DecodeTextAsync(bytes, cancellationToken);
            }
            using var handler = new ResourceManifestBinary.BoundedManifestDownload(maximumBytes);
            using var request = new UnityWebRequest(file.Location, UnityWebRequest.kHttpVerbGET, handler, null);
            request.timeout = 30;
            networkPolicy?.Configure(request, new ResourceRequestContext(file.Location, file.Location, ResourceRequestKind.Manifest));
            cancellationToken.ThrowIfCancellationRequested();
            ResourceOperationBase operation = UnityOperations.WaitAsync(request.SendWebRequest());
            try {
                while (!operation.IsDone && !handler.Exceeded) {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ResourceOperationBase.Yield();
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (handler.Exceeded) {
                    request.Abort();
                }

                await operation;
            }
            catch (OperationCanceledException) {
                request.Abort();
                await operation;
                throw;
            }
            if (handler.Exceeded) {
                throw ResourceFailure.Annotate(new InvalidDataException("清单超过读取上限。"), ResourceErrorCode.InvalidManifest, ResourceStage.CheckRelease);
            }

            if (request.result != UnityWebRequest.Result.Success) {
                throw ResourceFailure.Annotate(new IOException($"Manifest load failed: {file.Location}: {request.error}"), ResourceFailure.HttpErrorCode(request.responseCode), ResourceStage.CheckRelease, request.responseCode);
            }

            bytes = handler.ToArray();
            return await DecodeTextAsync(bytes, cancellationToken);
        }

        private static async ResourceOperationBase<string> DecodeTextAsync(byte[] bytes, CancellationToken token)
        {
            // StreamReader preserves the existing UTF-8/BOM behavior. Bound each decode step on WebGL.
            using var stream = new MemoryStream(bytes, false);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096);
            var result = new StringBuilder();
            var chars = new char[4096];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int count;
            while ((count = reader.Read(chars, 0, chars.Length)) > 0) {
                token.ThrowIfCancellationRequested();
                result.Append(chars, 0, count);
                if (clock.Elapsed.TotalMilliseconds >= 4) { await ResourceOperationBase.Yield(); clock.Restart(); }
            }
            token.ThrowIfCancellationRequested();
            return result.ToString();
        }
    }
}

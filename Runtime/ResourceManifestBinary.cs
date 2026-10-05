using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine.Networking;

namespace ZRAsset
{
    /// <summary>ZRMB v1：字符串表与有界数组，读入后仍执行正式清单校验。不是 YooAsset 二进制格式。</summary>
    public static class ResourceManifestBinary
    {
        private static readonly OperationSemaphore s_decodeSlots = new OperationSemaphore(2, 2);

        public static async ResourceOperationBase<ResourceManifest> LoadAsync(string location, CancellationToken cancellationToken = default,
            ResourceDownloadPolicy networkPolicy = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new ResourceFileLocation(location);
            byte[] bytes;
            if (file.LocalPath != null) {
                using FileStream input = ResourceFileReader.OpenRead(file);
                bytes = await ResourceFileIO.Shared.ReadRawBytesAsync(input, MaximumBytes, cancellationToken);
                if (input.Position != input.Length) {
                    throw new InvalidDataException("清单在读取期间发生变化。");
                }
            }
            else {
                using var handler = new BoundedManifestDownload(MaximumBytes);
                using var request = new UnityWebRequest(file.Location, UnityWebRequest.kHttpVerbGET, handler, null);
                request.timeout = 30;
                networkPolicy?.Configure(request, new ResourceRequestContext(file.Location, file.Location, ResourceRequestKind.Manifest));
                cancellationToken.ThrowIfCancellationRequested();
                UnityWebRequestAsyncOperation native = request.SendWebRequest();
                try {
                    while (!native.isDone) {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ResourceOperationBase.Yield();
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (handler.Exceeded) {
                        throw new InvalidDataException("清单超过 64 MiB。");
                    }

                    if (request.result != UnityWebRequest.Result.Success) {
                        throw new IOException(request.error);
                    }

                    bytes = handler.ToArray();
                }
                finally {
                    if (!native.isDone) {
                        request.Abort();
                    }
                }
            }
            return await DeserializeAsync(bytes, cancellationToken);
        }

        /// <summary>普通平台有界后台解码；WebGL 分批解码及校验。调用方在操作完成前不得修改 bytes。</summary>
        public static async ResourceOperationBase<ResourceManifest> DeserializeAsync(byte[] bytes, CancellationToken cancellationToken = default)
        {
            await s_decodeSlots.WaitAsync(cancellationToken);
            try {
#if UNITY_WEBGL && !UNITY_EDITOR
                return await DeserializeCooperativeAsync(bytes, cancellationToken);
#else
                return await ResourceOperationBase.Run(() => DeserializeCore(bytes, cancellationToken));
#endif
            }
            finally { s_decodeSlots.Release(); }
        }

        internal static async ResourceOperationBase<ResourceManifest> DeserializeCooperativeAsync(byte[] bytes, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            await ResourceOperationBase.Yield();
            ResourceManifest result = null;
            using IEnumerator<int> steps = DecodeSteps(bytes, value => result = value).GetEnumerator();
            var clock = Stopwatch.StartNew();
            var count = 0;
            while (true) {
                token.ThrowIfCancellationRequested();
                if (!steps.MoveNext()) {
                    break;
                }

                if (++count >= 16384 || clock.Elapsed.TotalMilliseconds >= 4) {
                    await ResourceOperationBase.Yield();
                    clock.Restart(); count = 0;
                }
            }
            token.ThrowIfCancellationRequested();
            return result;
        }

        internal sealed class BoundedManifestDownload: DownloadHandlerScript
        {
            private readonly MemoryStream m_bytes = new MemoryStream();
            private readonly int m_maximum;
            internal bool Exceeded { get; private set; }
            internal long Length
            {
                get
                {
                    return m_bytes.Length;
                }
            }

            internal BoundedManifestDownload(int maximum) : base(new byte[64 * 1024])
            {
                if (maximum < 0 || maximum > ResourceFileReader.MaximumTextBytes) {
                    throw new ArgumentOutOfRangeException(nameof(maximum));
                }

                m_maximum = maximum;
            }
            protected override void ReceiveContentLengthHeader(ulong length)
            {
                if (length > (ulong)m_maximum) {
                    Exceeded = true;
                }
            }
            protected override bool ReceiveData(byte[] data, int count)
            {
                return Append(data, count);
            }

            internal bool Append(byte[] data, int count)
            {
                if (Exceeded || data == null || count < 0 || count > data.Length || count > m_maximum - m_bytes.Length) { Exceeded = true; return false; }
                m_bytes.Write(data, 0, count); return true;
            }
            internal byte[] ToArray()
            {
                return m_bytes.ToArray();
            }

            public override void Dispose() { m_bytes.Dispose(); base.Dispose(); }
        }
        private const int Magic = 0x424d525a, MaximumItems = 1000000, MaximumBytes = 64 * 1024 * 1024;
        public static byte[] Serialize(ResourceManifest manifest)
        {
            manifest.Validate();
            var budget = MaximumItems * 4;
            void Count(int value) { if (value > MaximumItems || (budget -= value) < 0) { throw new InvalidDataException("清单数组数量超过上限。"); } }
            var strings = new List<string>(); var index = new Dictionary<string, int>(StringComparer.Ordinal);
            int Id(string value) { if (value == null) { return -1; } if (!index.TryGetValue(value, out var id)) { id = strings.Count; strings.Add(value); index.Add(value, id); } return id; }
            using var body = new MemoryStream(); using var w = new BinaryWriter(body, Encoding.UTF8, true);
            void S(string value)
            {
                w.Write(Id(value));
            }

            void A(string[] values) { Count(values.Length); w.Write(values.Length); foreach (var value in values) { S(value); } }
            w.Write(manifest.FormatVersion); S(manifest.PackageName); S(manifest.PackageVersion); S(manifest.BuildTarget);
            Count(manifest.Bundles.Length); w.Write(manifest.Bundles.Length);
            foreach (BundleInfo b in manifest.Bundles) {
                S(b.Name); S(b.Hash); S(b.Sha256); w.Write(b.Crc); w.Write(b.Size); A(b.Dependencies); w.Write((int)b.FileType);
                S(b.Encryption); S(b.EncryptionKeyId); w.Write(b.UnencryptedSize); S(b.UnencryptedSha256);
            }
            Count(manifest.Assets.Length); w.Write(manifest.Assets.Length);
            foreach (AssetInfo a in manifest.Assets) {
                S(a.Address); S(a.AssetPath); S(a.BundleName); w.Write((int)a.Kind); S(a.Guid); A(a.Tags ?? Array.Empty<string>());
                w.Write((int)a.FileType); w.Write(a.FileOffset); w.Write(a.FileSize); S(a.FileSha256);
                if (manifest.FormatVersion >= 6) {
                    A(a.DependencyBundles);
                }
            }
            w.Flush(); Count(strings.Count);
            using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
            writer.Write(Magic); writer.Write(manifest.FormatVersion >= 6 ? 2 : 1); writer.Write(strings.Count);
            foreach (var value in strings) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
            writer.Write(body.ToArray()); writer.Flush();
            return output.Length > MaximumBytes ? throw new InvalidDataException("二进制清单超过 64 MiB 上限。") : output.ToArray();
        }
        public static ResourceManifest Deserialize(byte[] bytes)
        {
            return DeserializeCore(bytes, default);
        }

        private static ResourceManifest DeserializeCore(byte[] bytes, CancellationToken token)
        {
            ResourceManifest result = null;
            foreach (var step in DecodeSteps(bytes, value => result = value)) {
                token.ThrowIfCancellationRequested();
            }

            token.ThrowIfCancellationRequested();
            return result;
        }

        private static IEnumerable<int> DecodeSteps(byte[] bytes, Action<ResourceManifest> publish)
        {
            if (bytes == null || bytes.Length > MaximumBytes) {
                throw new InvalidDataException("二进制清单为空或超过上限。");
            }

            using var stream = new MemoryStream(bytes, false);
            var utf8 = new UTF8Encoding(false, true);
            using var r = new BinaryReader(stream, utf8);
            var budget = MaximumItems * 4;
            int Count(int minimumBytes = 1)
            {
                var n = r.ReadInt32();
                return n < 0 || n > MaximumItems || (long)n * minimumBytes > stream.Length - stream.Position || (budget -= n) < 0
                    ? throw new InvalidDataException("清单数组长度无效。")
                    : n;
            }
            if (r.ReadInt32() != Magic) {
                throw new InvalidDataException("不支持的二进制清单格式。");
            }

            var binaryVersion = r.ReadInt32();
            if (binaryVersion != 1 && binaryVersion != 2) {
                throw new InvalidDataException("不支持的二进制清单格式。");
            }

            var strings = new string[Count(4)];
            for (var i = 0; i < strings.Length; i++) {
                var n = r.ReadInt32();
                if (n < 0 || n > stream.Length - stream.Position) {
                    throw new InvalidDataException("字符串越界。");
                }

                if (n <= 64 * 1024) {
                    strings[i] = utf8.GetString(bytes, checked((int)stream.Position), n);
                    stream.Position += n;
                }
                else {
                    Decoder decoder = utf8.GetDecoder();
                    var chars = new char[(64 * 1024) + 2];
                    var text = new StringBuilder();
                    var remaining = n;
                    while (remaining > 0) {
                        var count = Math.Min(64 * 1024, remaining);
                        remaining -= count;
                        var decoded = decoder.GetChars(bytes, checked((int)stream.Position), count, chars, 0, remaining == 0);
                        text.Append(chars, 0, decoded);
                        stream.Position += count;
                        yield return 0;
                    }
                    strings[i] = text.ToString();
                }
                yield return 0;
            }
            string S()
            {
                var id = r.ReadInt32();
                return id == -1 ? null : id < 0 || id >= strings.Length ? throw new InvalidDataException("字符串索引越界。") : strings[id];
            }
            IEnumerable<int> A(Action<string[]> assign)
            {
                var values = new string[Count(4)];
                for (var i = 0; i < values.Length; i++) { values[i] = S(); yield return 0; }
                assign(values);
            }
            var m = new ResourceManifest { FormatVersion = r.ReadInt32(), PackageName = S(), PackageVersion = S(), BuildTarget = S() };
            if (m.FormatVersion >= 6 && binaryVersion != 2) {
                throw new InvalidDataException("V6 requires binary manifest v2.");
            }
            // At least 52 bytes per bundle, 48/52 bytes per asset, before any referenced arrays.
            m.Bundles = new BundleInfo[Count(52)];
            for (var i = 0; i < m.Bundles.Length; i++) {
                var bundle = new BundleInfo { Name = S(), Hash = S(), Sha256 = S(), Crc = r.ReadUInt32(), Size = r.ReadInt64() };
                foreach (var step in A(value => bundle.Dependencies = value)) {
                    yield return step;
                }

                bundle.FileType = (ResourceFileType)r.ReadInt32();
                bundle.Encryption = S(); bundle.EncryptionKeyId = S();
                bundle.UnencryptedSize = r.ReadInt64(); bundle.UnencryptedSha256 = S();
                m.Bundles[i] = bundle;
                yield return 0;
            }
            m.Assets = new AssetInfo[Count(m.FormatVersion >= 6 ? 52 : 48)];
            for (var i = 0; i < m.Assets.Length; i++) {
                var asset = new AssetInfo { Address = S(), AssetPath = S(), BundleName = S(), Kind = (ResourceKind)r.ReadInt32(), Guid = S() };
                foreach (var step in A(value => asset.Tags = value)) {
                    yield return step;
                }

                asset.FileType = (ResourceFileType)r.ReadInt32(); asset.FileOffset = r.ReadInt64();
                asset.FileSize = r.ReadInt64(); asset.FileSha256 = S();
                if (m.FormatVersion >= 6) {
                    foreach (var step in A(value => asset.DependencyBundles = value)) {
                        yield return step;
                    }
                }

                m.Assets[i] = asset;
                yield return 0;
            }
            if (stream.Position != stream.Length) {
                throw new InvalidDataException("清单存在额外尾部字节。");
            }

            foreach (var step in m.ValidateSteps()) {
                yield return step;
            }

            publish(m);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    public sealed partial class ResourceManifest
    {
        /// <summary>异步解析及校验。WebGL 的 JSON 解析和自定义 Codec 仍为同步调用；大型 WebGL 清单推荐 ZRMB。</summary>
        public static async ResourceOperationBase<ResourceManifest> FromJsonAsync(string json, IResourceKeyProvider manifestKeys = null,
            IResourceManifestCodec codec = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                var binary = await ResourceManifestEnvelope.TryDecodeBytesAsync(json, manifestKeys, codec, cancellationToken);
                if (binary != null) {
                    return await ResourceManifestBinary.DeserializeAsync(binary, cancellationToken);
                }
                ResourceManifest result = null;
                await ResourceManifestWork.RunAsync(ParseSteps(json, value => result = value), cancellationToken);
                return result;
            }
            catch (Exception error) when (error is InvalidDataException || error is ArgumentException) {
                ResourceFailure.Annotate(error, ResourceErrorCode.InvalidManifest, ResourceStage.Verify);
                throw;
            }
        }

        private static IEnumerable<int> ParseSteps(string json, Action<ResourceManifest> publish)
        {
            // Unity documents the string overload of FromJson as safe on background threads.
            ResourceManifest manifest = JsonUtility.FromJson<ResourceManifest>(json) ?? throw new InvalidDataException("Empty resource manifest.");
            foreach (var step in manifest.ValidateSteps()) {
                yield return step;
            }

            publish(manifest);
        }

        /// <summary>异步复制及校验；操作完成前调用方不得修改源 DTO 或其中的数组。</summary>
        public async ResourceOperationBase<ResourceManifest> CloneAsync(CancellationToken cancellationToken = default)
        {
            ResourceManifest copy = null;
            await ResourceManifestWork.RunAsync(CopySteps(value => copy = value), cancellationToken);
            await ResourceManifestWork.RunAsync(copy.ValidateSteps(), cancellationToken);
            return copy;
        }

        internal async ResourceOperationBase<ResourceManifest> CopySnapshotAsync(CancellationToken token)
        {
            ResourceManifest copy = null;
            await ResourceManifestWork.RunAsync(CopySteps(value => copy = value), token);
            return copy;
        }

        public static ResourceManifest FromJson(string json, IResourceKeyProvider manifestKeys = null, IResourceManifestCodec codec = null)
        {
            try {
                if (ResourceManifestEnvelope.IsEnvelope(json)) {
                    return ResourceManifestEnvelope.Decode(json, manifestKeys, codec);
                }

                ResourceManifest manifest = JsonUtility.FromJson<ResourceManifest>(json) ?? throw new InvalidDataException("Empty resource manifest.");
                manifest.Validate();
                return manifest;
            }
            catch (Exception error) when (error is InvalidDataException || error is ArgumentException) {
                ResourceFailure.Annotate(error, ResourceErrorCode.InvalidManifest, ResourceStage.Verify);
                throw;
            }
        }
    }
}

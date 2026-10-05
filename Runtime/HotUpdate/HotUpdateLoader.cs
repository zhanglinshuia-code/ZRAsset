using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;

namespace ZRAsset.HotUpdate
{
    public enum HotUpdateState { Idle, Preparing, ApplyingMetadata, LoadingAssemblies, Starting, Started, RequiresRestart }

    /// <summary>限制代码字节快照的总量。兼容标识应在构建宿主时写入，不能信任下载清单自行声明兼容。</summary>
    public sealed class HotUpdateOptions
    {
        public string PlayerBuildId { get; }
        public long MaxSingleBinaryBytes { get; }
        public long MaxTotalBinaryBytes { get; }
        public HotUpdateOptions(string playerBuildId, long maxSingleBinaryBytes = 64L * 1024 * 1024,
            long maxTotalBinaryBytes = 256L * 1024 * 1024)
        {
            if (string.IsNullOrWhiteSpace(playerBuildId) || playerBuildId != playerBuildId.Trim()) {
                throw new ArgumentException("宿主构建标识不能为空或包含首尾空白。", nameof(playerBuildId));
            }

            if (maxSingleBinaryBytes < 1 || maxSingleBinaryBytes > int.MaxValue) {
                throw new ArgumentOutOfRangeException(nameof(maxSingleBinaryBytes));
            }

            if (maxTotalBinaryBytes < maxSingleBinaryBytes || maxTotalBinaryBytes > int.MaxValue) {
                throw new ArgumentOutOfRangeException(nameof(maxTotalBinaryBytes));
            }

            PlayerBuildId = playerBuildId;
            MaxSingleBinaryBytes = maxSingleBinaryBytes;
            MaxTotalBinaryBytes = maxTotalBinaryBytes;
        }
    }

    public sealed class HotUpdateResult
    {
        public string RuntimeName { get; internal set; }
        public string ContentVersion { get; internal set; }
        public string PlayerBuildId { get; internal set; }
        public IReadOnlyList<string> LoadedAssemblies { get; internal set; }
        public int AppliedMetadataCount { get; internal set; }
    }

    public sealed class HotUpdateRestartRequiredException: InvalidOperationException
    {
        public HotUpdateRestartRequiredException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// 单次进程代码启动器：先读全文件并校验，再补充元数据、按依赖加载 DLL，最后启动入口。
    /// 释放 TextAsset 只归还资源引用；已经提交给 CLR 的代码与元数据无法用资源卸载或版本指针回滚。
    /// </summary>
    public sealed class HotUpdateLoader
    {
        private sealed class PreparedBinary
        {
            internal byte[] Bytes;
        }

        // 正式适配器共享进程门闩；自定义测试后端有独立状态，不能污染真实代码启动。
        private static readonly OperationSemaphore s_processGate = new(1, 1);
        private static HotUpdateLoader s_processOwner;
        public static HotUpdateLoader Shared { get; } = new HotUpdateLoader(new HybridClrHotUpdateRuntime());
        private readonly IHotUpdateRuntime m_runtime;
        private readonly bool m_processRuntime;
        private readonly int m_threadId = Thread.CurrentThread.ManagedThreadId;
        private bool m_running;
        private ResourceManager m_startedResources;
        private string m_startedAddress, m_startedPlayerBuildId;
        private HotUpdateResult m_result;

        public HotUpdateState State { get; private set; }
        public Exception Error { get; private set; }

        public HotUpdateLoader(IHotUpdateRuntime runtime)
        {
            m_runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            m_processRuntime = runtime is HybridClrHotUpdateRuntime || runtime is ManagedHotUpdateRuntime ||
                runtime is EditorSimulationHotUpdateRuntime;
        }

        public ResourceOperationBase<HotUpdateResult> StartAsync(ResourceManager resources, string manifestAddress,
            HotUpdateOptions options, CancellationToken cancellationToken = default)
        {
            return StartWithCommitAsync(resources, manifestAddress, options, null, cancellationToken);
        }

        // 仅供启动协调器使用：全部代码校验及进程门闩检查完成后，提交代码前激活对应资源。
        internal ResourceOperationBase<HotUpdateResult> StartWithCommitAsync(ResourceManager resources, string manifestAddress,
            HotUpdateOptions options, Func<ResourceOperationBase> beforeCommit, CancellationToken cancellationToken)
        {
            CheckThread();
            if (resources == null) {
                throw new ArgumentNullException(nameof(resources));
            }

            if (string.IsNullOrWhiteSpace(manifestAddress)) {
                throw new ArgumentException("代码清单地址不能为空。", nameof(manifestAddress));
            }

            if (options == null) {
                throw new ArgumentNullException(nameof(options));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (m_running) {
                throw new InvalidOperationException("热更新启动尚未完成，请等待原始启动任务，不能并发启动另一次入口。");
            }

            if (State == HotUpdateState.RequiresRestart) {
                throw new HotUpdateRestartRequiredException("当前启动器已经进入不可恢复状态，请重启进程。", Error);
            }

            if (State == HotUpdateState.Started) {
                return ReferenceEquals(m_startedResources, resources) && m_startedAddress == manifestAddress && m_startedPlayerBuildId == options.PlayerBuildId
                    ? ResourceOperationBase.FromResult(m_result)
                    : throw new HotUpdateRestartRequiredException("代码已经启动。切换资源管理器或代码版本需要重启进程。");
            }
            m_running = true;
            State = HotUpdateState.Preparing;
            Error = null;
            return RunAsync(resources, manifestAddress, options, beforeCommit, cancellationToken);
        }

        private async ResourceOperationBase<HotUpdateResult> RunAsync(ResourceManager resources, string manifestAddress,
            HotUpdateOptions options, Func<ResourceOperationBase> beforeCommit, CancellationToken cancellationToken)
        {
            bool committed = false, entered = false;
            var prepared = new List<PreparedBinary>();
            try {
                m_runtime.ValidateEnvironment();
                string json;
                using (AssetHandle<TextAsset> handle = resources.LoadAssetAsync<TextAsset>(manifestAddress, cancellationToken)) {
                    TextAsset asset = await handle.Operation;
                    json = asset.text;
                    if (json.Length > 1024 * 1024) {
                        throw new InvalidDataException("热更新清单超过 1 Mi 字符。");
                    }
                }
                var manifest = HotUpdateManifest.FromJson(json);
                ResourcePlatform.ValidateBuildTarget(manifest.BuildTarget);
                if (!string.Equals(manifest.PlayerBuildId, options.PlayerBuildId, StringComparison.Ordinal)) {
                    throw new HotUpdateHostMismatchException(options.PlayerBuildId, manifest.PlayerBuildId);
                }

                HotUpdateAssembly[] ordered = manifest.GetLoadOrder();
                long total = 0;
                foreach (HotUpdateBinary file in manifest.AotMetadata) {
                    CheckSize(file);
                }

                foreach (HotUpdateAssembly file in ordered) {
                    CheckSize(file);
                }

                // 按文件读取避免同时保留多个 TextAsset 使用凭证；字节副本在全部校验完成前由本启动器持有。
                foreach (HotUpdateBinary file in manifest.AotMetadata) {
                    await PrepareAsync(file);
                }

                foreach (HotUpdateAssembly file in ordered) {
                    await PrepareAsync(file);
                }

                if (m_processRuntime) {
                    await s_processGate.WaitAsync(cancellationToken);
                    entered = true;
                    if (s_processOwner != null && !ReferenceEquals(s_processOwner, this)) {
                        throw new HotUpdateRestartRequiredException("本进程已有其他启动器提交过代码或元数据。再次启动或换版需要重启。");
                    }
                }
                foreach (HotUpdateAssembly file in ordered) {
                    if (m_runtime.IsAssemblyLoaded(file.Name)) {
                        throw new HotUpdateRestartRequiredException($"程序集 {file.Name} 已由其他流程加载，无法确认对应代码版本，请重启或显式使用 Editor 模拟模式。");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (beforeCommit != null) {
                    await beforeCommit();
                }

                cancellationToken.ThrowIfCancellationRequested();
                // 从这里开始不再响应取消；Assembly.Load 本身就可能执行模块初始化器。
                committed = true;
                if (m_processRuntime) {
                    s_processOwner = this;
                }

                int metadataCount = 0, index = 0;
                State = HotUpdateState.ApplyingMetadata;
                foreach (HotUpdateBinary metadata in manifest.AotMetadata) {
                    PreparedBinary payload = prepared[index++];
                    if (m_runtime.RequiresAotMetadata) {
                        m_runtime.LoadMetadata(metadata.Name, payload.Bytes);
                        metadataCount++;
                    }
                    payload.Bytes = null;
                }
                State = HotUpdateState.LoadingAssemblies;
                object entryAssembly = null;
                var loadedNames = new List<string>();
                foreach (HotUpdateAssembly assembly in ordered) {
                    PreparedBinary payload = prepared[index++];
                    var loaded = m_runtime.LoadAssembly(assembly.Name, payload.Bytes);
                    payload.Bytes = null;
                    if (loaded == null) {
                        throw new InvalidOperationException("运行时未返回程序集对象：" + assembly.Name);
                    }

                    loadedNames.Add(assembly.Name);
                    if (assembly.Name == manifest.EntryAssembly) {
                        entryAssembly = loaded;
                    }
                }
                State = HotUpdateState.Starting;
                await m_runtime.InvokeEntryAsync(entryAssembly, manifest.EntryType, manifest.EntryMethod, resources);
                m_result = new HotUpdateResult
                {
                    RuntimeName = m_runtime.Name,
                    ContentVersion = manifest.ContentVersion,
                    PlayerBuildId = manifest.PlayerBuildId,
                    LoadedAssemblies = loadedNames.AsReadOnly(),
                    AppliedMetadataCount = metadataCount
                };
                m_startedResources = resources;
                m_startedAddress = manifestAddress;
                m_startedPlayerBuildId = options.PlayerBuildId;
                State = HotUpdateState.Started;
                return m_result;

                void CheckSize(HotUpdateBinary file)
                {
                    if (file.Address == manifestAddress) {
                        throw new InvalidDataException("代码文件不能使用清单自身的资源地址。");
                    }

                    if (file.Size > options.MaxSingleBinaryBytes || file.Size > options.MaxTotalBinaryBytes - total) {
                        throw new InvalidDataException("代码字节量超过宿主允许的上限：" + file.Name);
                    }

                    total += file.Size;
                }

                async ResourceOperationBase PrepareAsync(HotUpdateBinary file)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] bytes;
                    using (AssetHandle<TextAsset> handle = resources.LoadAssetAsync<TextAsset>(file.Address, cancellationToken)) {
                        bytes = (await handle.Operation).bytes;
                    }

                    if (bytes.LongLength != file.Size) {
                        throw new InvalidDataException("代码文件大小不符：" + file.Name);
                    }

                    var actual = await ComputeHashAsync(bytes, cancellationToken);
                    if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase)) {
                        throw new InvalidDataException("代码文件 SHA-256 不符：" + file.Name);
                    }

                    HotUpdateAssemblyIdentity.RequireName(bytes, file.Name);
                    prepared.Add(new PreparedBinary { Bytes = bytes });
                }
            }
            catch (Exception exception) {
                State = committed || exception is HotUpdateRestartRequiredException ? HotUpdateState.RequiresRestart : HotUpdateState.Idle;
                Error = exception;
                if (committed && !(exception is HotUpdateRestartRequiredException)) {
                    throw new HotUpdateRestartRequiredException("热更新已开始提交，发生错误后不能回滚或重试入口，请重启进程。", exception);
                }

                throw;
            }
            finally {
                foreach (PreparedBinary payload in prepared) {
                    payload.Bytes = null;
                }

                prepared.Clear();
                if (entered) {
                    s_processGate.Release();
                }

                m_running = false;
            }
        }

        private static async ResourceOperationBase<string> ComputeHashAsync(byte[] bytes, CancellationToken token)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return await HashAsync(true);
#else
            // 字节快照仅由当前请求持有；后台哈希不调用任何 Unity API。
            return await ResourceOperationBase.Run(() => HashAsync(false).GetAwaiter().GetResult());
#endif
            async ResourceOperationBase<string> HashAsync(bool cooperative)
            {
                using (var sha = SHA256.Create()) {
                    var timer = Stopwatch.StartNew();
                    for (var offset = 0; offset < bytes.Length;) {
                        token.ThrowIfCancellationRequested();
                        var count = Math.Min(256 * 1024, bytes.Length - offset);
                        sha.TransformBlock(bytes, offset, count, bytes, offset);
                        offset += count;
                        if (cooperative && timer.Elapsed.TotalMilliseconds >= 2) { await ResourceOperationBase.Yield(); timer.Restart(); }
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    token.ThrowIfCancellationRequested();
                    return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != m_threadId) {
                throw new InvalidOperationException("HotUpdateLoader 必须在创建它的 Unity 主线程使用。");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace ZRAsset
{
    public sealed partial class ResourceVersionManager
    {
        [Serializable]
        private sealed class BootJournal
        {
            public int Format = 1;
            public string Pending, Session;
            public bool CancelledBeforeCommit;
            public VersionPointer Previous;
            public List<string> Failed = new();
        }
        // 此标记只判断同一进程；真正的恢复只在下次进程/Unity 域启动执行。
        private static readonly string s_bootSession = Guid.NewGuid().ToString("N");
        private string StatePath(string name)
        {
            return DownloadStorage.ValidatePath(m_options.CacheRoot,
            Path.Combine(m_options.CacheRoot, "ZRAssetVersions", m_buildTarget, name));
        }

        private BootJournal ReadBootJournal()
        {
            var path = StatePath("boot.json");
            if (!File.Exists(path)) {
                return new BootJournal();
            }

            try {
                BootJournal value = JsonUtility.FromJson<BootJournal>(ReadCheckedState(path));
                if (value == null || value.Format != 1 || value.Failed == null) {
                    throw new InvalidDataException();
                }
                // JsonUtility 会将空的嵌套引用反序列化为字段全空的对象；首次安装没有旧指针。
                if (value.Previous != null) {
                    if (string.IsNullOrEmpty(value.Previous.Active) && string.IsNullOrEmpty(value.Previous.Previous) &&
                        string.IsNullOrEmpty(value.Previous.ManifestSha256)) {
                        value.Previous = null;
                    }
                    else {
                        ValidateVersion(value.Previous.Active);
                        if (!string.IsNullOrEmpty(value.Previous.Previous)) {
                            ValidateVersion(value.Previous.Previous);
                        }

                        if (!DownloadStorage.IsSha256(value.Previous.ManifestSha256)) {
                            throw new InvalidDataException("启动记录的旧指针损坏。");
                        }
                    }
                }
                foreach (var version in value.Failed) {
                    ValidateVersion(version);
                }

                if (value.Pending != null && value.Pending.Length != 0) {
                    ValidateVersion(value.Pending);
                    if (string.IsNullOrEmpty(value.Session)) {
                        throw new InvalidDataException();
                    }
                }
                return value;
            }
            catch (ArgumentException error) { throw new InvalidDataException("启动记录损坏。", error); }
        }

        /// <summary>启动协调器在检查网络更新之前调用；未成功启动的版本不会在本次启动再次自动尝试。</summary>
        internal void RecoverInterruptedBoot()
        {
            CheckThread(); Enter();
            try {
                BootJournal journal = ReadBootJournal();
                if (string.IsNullOrEmpty(journal.Pending)) {
                    return;
                }

                if (journal.Session == s_bootSession && !journal.CancelledBeforeCommit) {
                    throw new HotUpdate.HotUpdateRestartRequiredException("本进程已有未完成的代码启动，请重启后恢复。");
                }

                RecoverBootJournal(journal);
            }
            finally { Exit(); }
        }

        // 只由确认 Loader 未提交代码的协调器调用。先记录清理意图，恢复中再次退出也不能误封版本。
        internal void AbortBootBeforeCommit()
        {
            CheckThread(); Enter();
            try {
                BootJournal journal = ReadBootJournal();
                if (journal.Session != s_bootSession || string.IsNullOrEmpty(journal.Pending)) {
                    throw new InvalidOperationException("待取消的启动记录不属于当前进程。");
                }

                journal.CancelledBeforeCommit = true;
                WriteCheckedState(StatePath("boot.json"), JsonUtility.ToJson(journal));
                RecoverBootJournal(journal);
            }
            finally { Exit(); }
        }

        private void RecoverBootJournal(BootJournal journal)
        {
            ResourceManifest manifest = null;
            if (m_pointer?.Active == journal.Pending && journal.Previous != null) {
                var json = File.ReadAllText(ManifestPath(journal.Previous.Active));
                if (!string.Equals(Hash(json), journal.Previous.ManifestSha256, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("启动恢复的旧版清单已损坏。");
                }
                manifest = ValidateTarget(ResourceManifest.FromJson(json), journal.Previous.Active);
            }
            CommitBootRecovery(journal, manifest);
        }

        private async ResourceOperationBase RecoverBootJournalAsync(BootJournal journal, CancellationToken token)
        {
            ResourceManifest manifest = null;
            if (m_pointer?.Active == journal.Pending && journal.Previous != null) {
                var json = await ResourceFileReader.ReadTextAsync(ManifestPath(journal.Previous.Active), token);
                if (!string.Equals(await HashAsync(json, token), journal.Previous.ManifestSha256, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException("启动恢复的旧版清单已损坏。");
                }
                manifest = await ParseTargetAsync(json, journal.Previous.Active, token);
                if (journal.Previous.HasSelection) {
                    await ValidateRequiredBundlesAsync(manifest, journal.Previous.RequiredBundles, token);
                }
            }
            token.ThrowIfCancellationRequested();
            // The small journal and pointer writes form the same non-cancellable commit as the synchronous API.
            CommitBootRecovery(journal, manifest);
        }

        private void CommitBootRecovery(BootJournal journal, ResourceManifest manifest)
        {
            if (!journal.CancelledBeforeCommit && !journal.Failed.Contains(journal.Pending)) { journal.Failed.Add(journal.Pending); }
            // Only undo this boot's pointer; preserve any externally activated version.
            if (m_pointer?.Active == journal.Pending) {
                if (journal.Previous == null) {
                    DownloadStorage.DeleteFile(PointerPath());
                    DownloadStorage.DeleteFile(BackupPath());
                    m_pointer = null;
                    m_activeManifest = null;
                }
                else {
                    WriteAtomic(PointerPath(), JsonUtility.ToJson(journal.Previous));
                    WriteAtomic(BackupPath(), JsonUtility.ToJson(journal.Previous));
                    m_pointer = journal.Previous;
                    m_activeManifest = manifest;
                }
            }
            journal.Pending = null;
            journal.Session = null;
            journal.Previous = null;
            journal.CancelledBeforeCommit = false;
            WriteCheckedState(StatePath("boot.json"), JsonUtility.ToJson(journal));
        }

        internal async ResourceOperationBase RecoverInterruptedBootAsync(CancellationToken token)
        {
            CheckThread();
            await EnterAsync(token);
            try {
                BootJournal journal = ReadBootJournal();
                if (string.IsNullOrEmpty(journal.Pending)) { return; }
                if (journal.Session == s_bootSession && !journal.CancelledBeforeCommit) {
                    throw new HotUpdate.HotUpdateRestartRequiredException("本进程已有未完成的代码启动，请重启后恢复。");
                }
                await RecoverBootJournalAsync(journal, token);
            }
            finally { Exit(); }
        }

        internal async ResourceOperationBase AbortBootBeforeCommitAsync()
        {
            CheckThread();
            await EnterAsync(default);
            try {
                BootJournal journal = ReadBootJournal();
                if (journal.Session != s_bootSession || string.IsNullOrEmpty(journal.Pending)) {
                    throw new InvalidOperationException("待取消的启动记录不属于当前进程。");
                }
                journal.CancelledBeforeCommit = true;
                WriteCheckedState(StatePath("boot.json"), JsonUtility.ToJson(journal));
                await RecoverBootJournalAsync(journal, default);
            }
            finally { Exit(); }
        }

        internal void RequireBootAllowed(string version)
        {
            CheckThread();
            ValidateVersion(version);
            Enter();
            try { RequireBootAllowedCore(version); }
            finally { Exit(); }
        }

        internal ResourceOperationBase RequireBootAllowedAsync(string version, CancellationToken token)
        {
            ValidateVersion(version);
            return ChangeBootStateAsync(() => RequireBootAllowedCore(version), token);
        }

        private void RequireBootAllowedCore(string version)
        {
            if (ReadBootJournal().Failed.Contains(version)) {
                throw new InvalidDataException("该版本上次启动失败，发布新版本后才能自动重试：" + version);
            }
        }

        internal void BeginBoot(string version)
        {
            CheckThread();
            ValidateVersion(version);
            Enter();
            try { BeginBootCore(version); }
            finally { Exit(); }
        }

        internal ResourceOperationBase BeginBootAsync(string version, CancellationToken token)
        {
            ValidateVersion(version);
            return ChangeBootStateAsync(() => BeginBootCore(version), token);
        }

        private void BeginBootCore(string version)
        {
            BootJournal journal = ReadBootJournal();
            if (!string.IsNullOrEmpty(journal.Pending)) { throw new InvalidOperationException("已有待完成的启动记录。"); }
            if (journal.Failed.Contains(version)) { throw new InvalidDataException("禁止自动启动失败版本：" + version); }
            journal.Pending = version;
            journal.Session = s_bootSession;
            journal.Previous = m_pointer;
            journal.CancelledBeforeCommit = false;
            WriteCheckedState(StatePath("boot.json"), JsonUtility.ToJson(journal));
        }

        internal void CompleteBoot()
        {
            CheckThread();
            Enter();
            try { CompleteBootCore(); }
            finally { Exit(); }
        }

        internal ResourceOperationBase CompleteBootAsync()
        {
            return ChangeBootStateAsync(CompleteBootCore, default);
        }

        private void CompleteBootCore()
        {
            BootJournal journal = ReadBootJournal();
            if (journal.Session != s_bootSession || journal.Pending != m_pointer?.Active) {
                throw new InvalidOperationException("启动记录与活动版本不一致。");
            }
            journal.Pending = null;
            journal.Session = null;
            journal.Previous = null;
            WriteCheckedState(StatePath("boot.json"), JsonUtility.ToJson(journal));
        }

        private async ResourceOperationBase ChangeBootStateAsync(Action change, CancellationToken token)
        {
            CheckThread();
            await EnterAsync(token);
            try { change(); }
            finally { Exit(); }
        }

        [Serializable] private sealed class CheckedState { public string Json, Sha256; }
        private static void WriteCheckedState(string path, string json)
        {
            WriteAtomic(path, JsonUtility.ToJson(new CheckedState { Json = json, Sha256 = Hash(json) }));
        }

        private static string ReadCheckedState(string path)
        {
            if (new FileInfo(path).Length > 1024 * 1024) {
                throw new InvalidDataException("本地状态超出长度上限。");
            }

            try {
                CheckedState state = JsonUtility.FromJson<CheckedState>(File.ReadAllText(path));
                return state?.Json == null || !string.Equals(Hash(state.Json), state.Sha256, StringComparison.OrdinalIgnoreCase)
                    ? throw new InvalidDataException("本地状态完整性校验失败；不能自动降低安全边界。")
                    : state.Json;
            }
            catch (ArgumentException error) { throw new InvalidDataException("本地状态格式损坏。", error); }
        }
    }
}

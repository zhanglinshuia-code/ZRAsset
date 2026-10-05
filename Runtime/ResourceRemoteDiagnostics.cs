using System;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking.PlayerConnection;

namespace ZRAsset
{
    [Serializable]
    public sealed class ResourceDiagnosticFrame
    {
        public string CapturedUtc;
        public ResourceDiagnosticSnapshot[] Managers;
        public ResourceOperationDiagnostic[] Operations;
        public string Error;
    }
    [Serializable]
    public sealed class ResourceOperationDiagnostic
    {
        public string Type, Name, State;
        public float Progress;
        public uint Priority;
        public long Id, ParentId;
        public double ElapsedMilliseconds;
    }
    /// <summary>只在开发版显式启用的 PlayerConnection 诊断；远端只能请求快照，不能执行资源操作。</summary>
    public static class ResourceRemoteDiagnostics
    {
        public static readonly Guid s_requestId = new("b9bdbfd8-3ad0-44f5-aca8-123600812a11");
        public static readonly Guid s_responseId = new("b9bdbfd8-3ad0-44f5-aca8-123600812a12");
        public const int MaximumMessageBytes = 4 * 1024 * 1024;
        private static bool s_enabled;
        private static double s_lastRequest = -1;
        public static Func<ResourceDiagnosticManager, bool> ManagerFilter { get; set; }
        public static ResourceDiagnosticFrame Capture()
        {
            ResourceDiagnosticSnapshot[] snapshots = ResourceDiagnostics.GetManagers().Where(m => ManagerFilter == null || ManagerFilter(m)).Select(m => ResourceDiagnostics.TryCapture(m.Id, out ResourceDiagnosticSnapshot s) ? s : null).Where(s => s != null).ToArray();
            return new ResourceDiagnosticFrame { CapturedUtc = DateTime.UtcNow.ToString("O"), Managers = snapshots, Operations = OperationSystem.CaptureDiagnostics() };
        }
        public static void Enable()
        {
            if (!Debug.isDebugBuild && !Application.isEditor) {
                throw new InvalidOperationException("远程诊断只允许开发版启用。");
            }

            if (s_enabled) {
                return;
            }

            PlayerConnection.instance.Register(s_requestId, OnRequest); s_enabled = true;
        }
        public static void Disable()
        {
            if (!s_enabled) {
                return;
            }

            PlayerConnection.instance.Unregister(s_requestId, OnRequest); s_enabled = false;
        }
        private static void OnRequest(MessageEventArgs message)
        {
            if (Time.realtimeSinceStartupAsDouble - s_lastRequest < 0.25) {
                return;
            }

            s_lastRequest = Time.realtimeSinceStartupAsDouble;
            if (message.data != null && message.data.Length > 16) {
                return;
            }

            byte[] bytes;
            try { bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(Capture())); }
            catch (Exception error) { bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new ResourceDiagnosticFrame { Error = error.Message })); }
            if (bytes.Length > MaximumMessageBytes) {
                bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new ResourceDiagnosticFrame { Error = "诊断快照超过 4 MiB，请缩小采集范围。" }));
            }

            PlayerConnection.instance.Send(s_responseId, bytes);
        }
    }
}

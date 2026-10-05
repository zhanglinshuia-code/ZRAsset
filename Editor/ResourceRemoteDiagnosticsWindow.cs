using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Networking.PlayerConnection;
using UnityEngine;
using UnityEngine.Networking.PlayerConnection;

namespace ZRAsset.Editor
{
    public sealed class ResourceRemoteDiagnosticsWindow: EditorWindow
    {
        private readonly List<(int player, ResourceDiagnosticFrame frame)> m_history = new();
        private int m_selected;
        private Vector2 m_scroll;
        private bool m_refresh;
        private double m_next;
        [MenuItem("ZRAsset/远程资源诊断")]
        public static void Open()
        {
            GetWindow<ResourceRemoteDiagnosticsWindow>("ZRAsset 远程诊断");
        }

        private void OnEnable()
        {
            EditorConnection.instance.Register(ResourceRemoteDiagnostics.s_responseId, Receive);
        }

        private void OnDisable()
        {
            EditorConnection.instance.Unregister(ResourceRemoteDiagnostics.s_responseId, Receive);
        }

        private void Receive(MessageEventArgs message)
        {
            if (message.data == null || message.data.Length > ResourceRemoteDiagnostics.MaximumMessageBytes) {
                return;
            }

            try { Add(message.playerId, JsonUtility.FromJson<ResourceDiagnosticFrame>(Encoding.UTF8.GetString(message.data))); }
            catch (Exception error) { Debug.LogWarning("诊断消息格式无效：" + error.Message); }
        }
        private void Add(int player, ResourceDiagnosticFrame frame)
        {
            if (frame == null) {
                return;
            }

            if (m_history.Count == 120) {
                m_history.RemoveAt(0);
            }

            m_history.Add((player, frame)); m_selected = m_history.Count - 1; Repaint();
        }
        private void Update()
        {
            if (m_refresh && EditorApplication.timeSinceStartup >= m_next) { m_next = EditorApplication.timeSinceStartup + 1; Request(); }
        }
        private void Request()
        {
            EditorConnection.instance.Send(ResourceRemoteDiagnostics.s_requestId, Array.Empty<byte>());
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button("采集已连接 Player")) {
                    Request();
                }

                if (GUILayout.Button("采集本地")) {
                    Add(0, ResourceRemoteDiagnostics.Capture());
                }

                m_refresh = GUILayout.Toggle(m_refresh, "每秒采集");
                if (GUILayout.Button("清空历史")) {
                    m_history.Clear();
                }
            }
            if (m_history.Count == 0) { EditorGUILayout.HelpBox("Player 中调用 ResourceRemoteDiagnostics.Enable()，使用 Development Build + Autoconnect Profiler 连接。", MessageType.Info); return; }
            m_selected = EditorGUILayout.IntSlider("历史帧（最多 120）", m_selected, 0, m_history.Count - 1);
            (int player, ResourceDiagnosticFrame frame) item = m_history[m_selected]; ResourceDiagnosticFrame frame = item.frame;
            EditorGUILayout.LabelField("Player " + item.player, frame.CapturedUtc ?? "");
            if (GUILayout.Button("导出本帧 JSON")) {
                var path = EditorUtility.SaveFilePanel("导出诊断", "", "resource-frame", "json"); if (path.Length > 0) {
                    File.WriteAllText(path, JsonUtility.ToJson(frame, true));
                }
            }
            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);
            if (!string.IsNullOrEmpty(frame.Error)) {
                EditorGUILayout.HelpBox(frame.Error, MessageType.Warning);
            }

            foreach (ResourceDiagnosticSnapshot manager in frame.Managers ?? Array.Empty<ResourceDiagnosticSnapshot>()) {
                EditorGUILayout.LabelField(manager.Name, EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"资源 {manager.Summary.Assets}   AB 包 {manager.Summary.Bundles}   使用凭证 {manager.Summary.Handles}");
                EditorGUILayout.TextArea(manager.ToJson(), GUILayout.MinHeight(100));
            }
            EditorGUILayout.LabelField("异步操作", EditorStyles.boldLabel);
            foreach (ResourceOperationDiagnostic op in frame.Operations ?? Array.Empty<ResourceOperationDiagnostic>()) {
                EditorGUILayout.LabelField(op.Name ?? op.Type, $"#{op.Id} ← {op.ParentId}  {ResourceEditorChinese.Text(op.State)}  {op.Progress:P0}  {op.ElapsedMilliseconds:F0} ms");
            }

            EditorGUILayout.EndScrollView();
        }
    }
}

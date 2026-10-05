using System;
using UnityEditor;
using UnityEngine;
using ZRAsset.HotUpdate;

namespace ZRAsset.Editor
{
    [CustomEditor(typeof(HotUpdateStartupConfig))]
    public sealed class HotUpdateStartupConfigEditor: UnityEditor.Editor
    {
        private bool m_showAdvanced;
        private string m_validationMessage;
        private MessageType m_validationType;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.LabelField("热更下载地址配置", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("修改并保存此配置资产即可，无需改代码。启动场景组件必须引用此资产。配置随主包发布；运行中的启动会话不会自动切换地址。", MessageType.Info);
            Draw("SignedReleaseUrl", "版本检查地址", "签名发布查询的完整 URL，由发布人员提供。");
            Draw("RemoteBundleBaseUrl", "资源 CDN 根地址", "AB 文件的下载根目录，支持 HTTP / HTTPS，不能带查询参数或片段。需与服务器发布目录一致。");
            Draw("AllowHttpLoopback", "允许本机 HTTP 测试", "仅供本机模拟服务验证，正式发布关闭。");
            serializedObject.ApplyModifiedProperties();
            if (GUILayout.Button("检查配置（不联网）")) {
                try {
                    ((HotUpdateStartupConfig)target).CreateOptions();
                    m_validationMessage = "配置结构通过。服务器可达性、资源目录和发布内容仍需实际启动验证。";
                    m_validationType = MessageType.Info;
                }
                catch (Exception error) {
                    m_validationMessage = error.GetBaseException().Message;
                    m_validationType = MessageType.Error;
                }
            }
            if (!string.IsNullOrEmpty(m_validationMessage)) {
                EditorGUILayout.HelpBox(m_validationMessage, m_validationType);
            }

            EditorGUILayout.Space();
            m_showAdvanced = EditorGUILayout.Foldout(m_showAdvanced, "技术配置（宿主、公钥、缓存与并发）", true);
            if (!m_showAdvanced) {
                return;
            }

            SerializedProperty property = serializedObject.GetIterator();
            var enterChildren = true;
            while (property.NextVisible(enterChildren)) {
                enterChildren = false;
                if (property.name == "m_Script" || property.name == "SignedReleaseUrl" ||
                    property.name == "RemoteBundleBaseUrl" || property.name == "AllowHttpLoopback") {
                    continue;
                }

                ResourceEditorChinese.PropertyField(property, true);
            }
            serializedObject.ApplyModifiedProperties();
        }

        private void Draw(string name, string label, string tooltip)
        {
            ResourceEditorChinese.PropertyField(serializedObject.FindProperty(name), new GUIContent(label, tooltip));
        }
    }
}

using UnityEditor;
using UnityEngine;

namespace ZRAsset.Editor
{
    [InitializeOnLoad]
    internal static class OperationEditorDriver
    {
        static OperationEditorDriver()
        {
            OperationSystem.Initialize();
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }
        private static ResourceOperationBase s_reportedShutdown;
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode && !ResourcePackages.SessionClosing) {
                ResourcePackages.BeginEditorPlaySession();
            }

            if (state == PlayModeStateChange.ExitingPlayMode) {
                ResourcePackages.ShutdownSessionAsync();
            }

            if (state == PlayModeStateChange.ExitingEditMode && ResourcePackages.SessionClosing) {
                if (ResourcePackages.SessionShutdown.IsDone) {
                    ResourcePackages.ShutdownSessionAsync();
                }

                EditorApplication.isPlaying = false;
                Debug.LogWarning("ZRAsset 正在清理上一 Play Mode 会话的资源，请等待完成后再进入 Play Mode。");
            }
        }
        private static void Update()
        {
            if (!Application.isPlaying) {
                OperationSystem.Update();
            }

            ResourceOperationBase shutdown = ResourcePackages.SessionShutdown;
            if (shutdown != null && shutdown.IsDone && shutdown.Status != OperationStatus.Succeeded && s_reportedShutdown != shutdown) {
                s_reportedShutdown = shutdown;
                Debug.LogError("ZRAsset Play Mode 资源清理失败，保留资源及租约以便检查：" + shutdown.Error);
            }
        }
    }
}

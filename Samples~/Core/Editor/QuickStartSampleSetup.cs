using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ZRAsset.Editor;

namespace ZRAsset.Samples.Editor
{
    public static class QuickStartSampleSetup
    {
        private const string ConfigGuid = "13c5cefc56c625f4dadf4390896246a7";
        private const string SceneGuid = "0629d946b66350444bc01d603ea71bc6";

        [MenuItem("ZRAsset/示例/构建并打开快速入门")]
        public static void BuildAndOpen()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) {
                throw new InvalidOperationException("请先退出 Play Mode，再构建入门资源。");
            }
            string configPath = AssetDatabase.GUIDToAssetPath(ConfigGuid);
            string scenePath = AssetDatabase.GUIDToAssetPath(SceneGuid);
            var config = AssetDatabase.LoadAssetAtPath<BundleBuildConfig>(configPath);
            if (config == null || string.IsNullOrEmpty(scenePath)) {
                throw new InvalidOperationException("请从 Package Manager 导入 ZRAsset 的 Core examples。");
            }
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) { return; }
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            string output = BundleBuilder.GetDefaultOutput(config, target);
            BundleBuilder.Build(config, output, target);
            BundleBuilder.CopyBuild(output, BundleBuilder.GetDefaultBuiltInRoot(config), target);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            EditorSceneManager.OpenScene(scenePath);
            Debug.Log("ZRAsset 入门资源已构建。点击 Play，场景会显示 AssetBundle 加载结果。");
        }
    }
}

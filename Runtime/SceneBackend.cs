using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZRAsset
{
    /// <summary>后端契约：加载失败不能遗留已加载场景；卸载失败时必须仍保留场景及其使用权。</summary>
    public interface ISceneBackend
    {
        ResourceOperationBase<Scene> LoadSceneAsync(string path, LoadSceneMode mode);
        ResourceOperationBase UnloadSceneAsync(Scene scene);
    }

    public sealed class UnitySceneBackend: ISceneBackend, IControlledSceneBackend
    {
        public ResourceOperationBase<Scene> LoadSceneAsync(string path, LoadSceneMode mode)
        {
            return BeginLoadScene(path, new ResourceSceneLoadOptions(mode), default).Operation;
        }

        public ISceneLoadRequest BeginLoadScene(string path, ResourceSceneLoadOptions options, System.Threading.CancellationToken cancellationToken)
        {
            return new UnitySceneLoadRequest(path, options, () => SceneManager.LoadSceneAsync(path, new LoadSceneParameters(options.Mode, options.LocalPhysicsMode)), cancellationToken);
        }

        public async ResourceOperationBase UnloadSceneAsync(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) {
                return;
            }

            UnitySceneLoadRequest.RequireUnblockedQueue();
            if (SceneManager.sceneCount <= 1) {
                throw new InvalidOperationException("Unity cannot unload the last scene. Load a replacement or bootstrap scene first.");
            }

            AsyncOperation request = SceneManager.UnloadSceneAsync(scene) ?? throw new InvalidOperationException($"Scene unload did not start: {scene.path}");
            await UnityOperations.WaitAsync(request);
            if (scene.IsValid() && scene.isLoaded) {
                throw new InvalidOperationException($"Scene is still loaded: {scene.path}");
            }
        }
    }
}

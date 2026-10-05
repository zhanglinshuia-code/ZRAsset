# UniTask

先安装 UniTask 2.x，再从 Package Manager 导入此示例。示例程序集引用 `ZRAsset.Runtime` 和 `UniTask`，核心框架无第三方依赖。

```csharp
using var handle = package.LoadAssetAsync<UnityEngine.GameObject>("hero");
UnityEngine.GameObject prefab = await handle.Operation.AsUniTask(cancellationToken);
```

使用带状态的 `UniTask.WaitUntil`，避免为每次轮询创建闭包。取消只停止该等待，不取消其他 Handle 共享的 Provider；调用方仍须释放自己持有的 Handle。资源操作失败会保留原异常。

API 依据：[UniTask 官方源码](https://github.com/Cysharp/UniTask/blob/master/src/UniTask/Assets/Plugins/UniTask/Runtime/UniTask.WaitUntil.cs)。

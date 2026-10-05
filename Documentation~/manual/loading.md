# 加载与生命周期

[文档首页](../index.md) · [资源构建](building.md) · 下一篇：[更新](updates.md)

## 谁拥有资源

应用启动模块拥有 Package；业务模块拥有 Scope 或 Handle；Provider 是框架内部共享加载记录。一个 Unity 对象裸引用不代表框架所有权。只有有效 Handle、Scope、实例或场景租约能够阻止对应资源回收。

推荐给登录界面、战斗场景、配置读取批次各建立 Scope。Scope 适合统一退出边界；需要独立寿命时直接持有 Handle。ResourceManager 是高级入口，用于自定义文件系统、后端或维护工具。

## Scope 托管加载

以下片段假设 package 已初始化、token 是业务取消令牌：

```csharp
await using (var scope = package.CreateScope("login")) {
    Texture2D icon = await scope.LoadAsync<Texture2D>("ui/login-icon", token);
    // 在此作用域中使用 icon。退出前让实际使用者停止引用它。
}
```

长期界面不能把 Scope 写在一个立即返回的初始化方法中然后让图片继续显示。应把 Scope 保存为界面成员，在关闭界面、销毁对象后调用 DisposeAsync。加载失败时 LoadAsync 会移除该次跟踪；Scope 关闭也会取消其等待者并按逆序清理拥有的条目。

## 独立 Handle

```csharp
using (AssetHandle<TextAsset> handle = package.LoadAssetAsync<TextAsset>("config/game", token)) {
    TextAsset text = await handle.Operation;
    string content = text.text;
    // 解析或复制业务数据。
}
```

LoadAssetAsync 返回 Handle，等待的是 `handle.Operation`。await 完成不意味着所有权已释放。操作失败、业务取消或方法抛异常时仍应确保句柄进入释放路径。Release/Dispose 是归还该消费者的引用，不是强行取消所有共享消费者。

同地址不同类型或不同加载种类可能对应不同 Provider。不要把主资源、子资源集合和同 Bundle 全部资源的结果混为一个缓存项。集合加载返回 AssetCollectionHandle，也需要在完成实际使用后释放。

## 实例

```csharp
InstanceHandle instance = package.InstantiateAsync("characters/player", cancellationToken: token);
try {
    GameObject player = await instance.Operation;
    // 使用 player。
}
finally { await instance.DestroyAsync(); }
```

InstanceHandle 内部保留 Prefab 资源，销毁实例后才归还。若手动 Instantiate 一个已加载 Prefab，必须自己保证 Prefab Handle 的寿命覆盖所有克隆对象。外部销毁对象后，框架的生命周期轮询可以发现变化，但明确调用 DestroyAsync 更容易控制业务关闭时序。

## 场景

```csharp
SceneHandle scene = package.LoadSceneAsync("scenes/battle", cancellationToken: token);
try {
    await scene.Operation;
    // 执行该场景业务。
}
finally { await scene.UnloadAsync(); }
```

默认叠加加载。同一资源管理器对同一路径只允许一个活跃场景 Handle。延迟激活和独立物理场景使用 ResourceSceneLoadOptions，并要求后端支持受控场景加载。延迟激活后需显式调用 SceneHandle.ActivateAsync；不要把“已加载待激活”误当成完整业务就绪。

场景请求有前驱顺序约束。取消队列中间的请求，不意味着后面的请求可以越过仍在运行的前驱。

## RawFile

LoadRawFileAsync 返回 RawFileHandle。等待 Operation 后按句柄公开 API 读取字节、文本或流。独立、未加密的本地 RawFile 才能通过 GetRawFilePath 取得直接路径；归档条目和加密内容使用流/字节入口。OpenRead 返回的流独立持有引用，释放 Handle 不会关闭流；业务应明确关闭流，之后才能安全结束管理器。

## 提前释放与转移

Scope 创建的 Handle 需要提前释放时，使用 `await scope.ReleaseAsync(handle)`，这样同时删除 Scope 跟踪和取消注册。Detach 只转移所有权，不释放资源；成功后由接收方负责关闭。正在释放或关闭的 Scope 不允许转移。

## 释放与卸载

| 操作 | 语义 |
| --- | --- |
| Handle.Release/Dispose | 归还本消费者所有权 |
| Scope.DisposeAsync | 关闭其拥有条目，实例/场景使用对应异步清理 |
| UnloadUnused | 尝试回收符合延迟条件的零引用资源 |
| UnloadUnusedAsync | 强制检查闲置资源，可选择等待 Unity 引擎回收 |
| UnloadAllAssetsAsync | 清理资源，保留包/管理器；要求符合生命周期约束 |
| Package.DisposeAsync | 关闭该包及其管理器、下载与租约，移除注册 |
| ForceDisposeAsync | 明确撤销现有消费者，属于强制恢复路径 |

资源引用数归零、Bundle 卸载以及操作系统实际归还内存不是同一时间点。大量实例和场景仍有存活轮询成本。强制关闭不适合代替正常业务资源管理。

## 关闭顺序与取消

正常关闭：停止发起新加载 → 停止 UI/音频/场景业务 → 销毁实例和卸载场景 → 关闭 Scope/Handle → 关闭 Package。包仍有消费者时拒绝普通关闭，是为了防止业务继续使用已经卸载的数据。

取消单个等待者不会取消其他使用者共享的底层工作。Unity 原生操作不能总被真正中断，因此“用户停止等待”和“底层已经清理完成”之间可能有时间差。应等待框架的关闭操作完成，而不是立即覆盖缓存或切换版本。

同步 API 仅适用于来源、后端和已准备状态支持的情况；不能在主线程用等待阻塞的方式把异步下载变成同步。

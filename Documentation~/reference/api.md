# API 导航

[文档首页](../index.md) · [配置参考](configuration.md)

本页按任务组织推荐入口。完整公开签名以链接的源码为准。旧重载保留兼容，不表示新项目应优先选择长参数列表。

## 包和初始化

| 任务 | 入口 | 返回与约束 |
| --- | --- | --- |
| 注册命名包 | ResourcePackages.CreatePackage | 名称唯一，主线程调用 |
| 查找已注册包 | GetPackage / TryGetPackage | 不创建或自动初始化 |
| 本地首包初始化 | ResourcePackage.InitializeAsync(options, token) | 等完成后才能加载 |
| 配置更新 | ConfigureUpdatesAsync(target, downloads, options, token) | 恢复版本状态，不自动等同于加载活动资源 |
| 打开活动版本 | LoadActiveAsync | 必须已有合法活动版本 |
| 切换与回滚 | ActivateAsync / RollbackAsync | 先归还旧消费者 |
| 关闭 | DisposeAsync | 关闭拥有的管理器并移除注册 |

源码：[包注册](../../Runtime/ResourcePackages.cs)、[包](../../Runtime/ResourcePackage.cs)、[配置入口](../../Runtime/ResourceInitializationOptions.cs)。

## 资源加载与结果

| 入口 | 返回 | 完成后如何使用 | 结束 |
| --- | --- | --- | --- |
| LoadAssetAsync&lt;T&gt; | AssetHandle&lt;T&gt; | await handle.Operation 或访问 Asset | Release/Dispose |
| LoadSubAssetsAsync / LoadAllAssetsAsync | AssetCollectionHandle | 等待集合操作 | Release/Dispose |
| InstantiateAsync | InstanceHandle | await Operation 得到 GameObject | await DestroyAsync |
| LoadSceneAsync | SceneHandle | await Operation，按需要 ActivateAsync | await UnloadAsync |
| LoadRawFileAsync | RawFileHandle | await Operation，再 ReadText/ReadBytes/OpenRead | 关闭流并释放 Handle |
| Scope.LoadAsync&lt;T&gt; | ResourceOperationBase&lt;T&gt; | 得到 Scope 保活的 Unity 对象 | 关闭 Scope |

代码中 LoadAssetAsync 返回的 Handle 本身不是 .NET Task。不要照搬其他框架的 .Task、WaitForAsyncComplete 等名字。框架操作支持 await、IEnumerator 和 Completed。

## Scope 和回收

CreateScope 创建业务拥有容器；Scope 的 Handle 加载入口保持可精细控制；LoadAsync 提供直接对象结果。ReleaseAsync(handle) 提前释放并移除跟踪，Detach(handle) 转移所有权。DisposeAsync 按类型执行异步清理。

UnloadUnused 检查零引用和延迟条件；ResourceManager.UnloadUnusedAsync 可选择等待引擎回收；AutoUnloadUnused 安排持续检查。UnloadAllAssetsAsync 保留容器对象但清理资源，DisposeAsync 结束管理器。ForceDisposeAsync 用于显式撤销所有权的恢复路径。

源码：[Scope](../../Runtime/ResourceScope.cs)、[托管加载](../../Runtime/ResourceScope.Loading.cs)、[回收](../../Runtime/ResourceManager.Maintenance.cs)。

## 元数据、选择和准备

WarmupCatalogAsync 用于提前建立查询索引；SelectAsync 根据 ResourceSelection 选择元数据；PlanSelectionAsync 返回所需文件与来源计划。Manifest 属性和 GetManifestAsync 返回快照，后者适合大清单。读取元数据不等于加载 Unity 对象。

CreateDownloader 为选择集创建下载器，实际启动、进度和结束以 [ResourceDownloader](../../Runtime/ResourceDownloader.cs) 的公开操作为准。导入与解包分别由 ResourceImporter、ResourceUnpacker 处理，写入同样遵守锁和校验。

## 版本与签名

ResourceVersionManager：FetchSignedReleaseAsync → PlanPrepareSignedReleaseAsync → PrepareSignedReleaseAsync；通过 Package.ActivateAsync 完成内存与磁盘版本配合。PrepareSignedSelectionAsync 用于选择性内容。CompareAsync 比较逻辑变化，准备计划反映实际设备可用文件。

ResourceReleaseSetManager.CreateAsync 创建多包集合管理器，LoadActiveAsync 恢复集合，ActivateSignedAsync 处理签名集合，GetPackage 取得成员资源管理器。集合的 GetPackage 返回类型与全局 ResourcePackages 的命名包对象不同，接入时以签名为准。

## 完整 RawFile 读取示例

下面的方法由调用方提供已经初始化的包和地址，不接管 Package：

```csharp
public static async ZRAsset.ResourceOperationBase<string> ReadConfigAsync(
    ZRAsset.ResourcePackage package, string address, System.Threading.CancellationToken token)
{
    using (ZRAsset.RawFileHandle file = package.LoadRawFileAsync(address, token)) {
        await file.Operation;
        return await file.ReadTextAsync(cancellationToken: token);
    }
}
```

ReadText/ReadBytes 默认最多 32 MiB，业务可以显式指定更小限额。大文件优先使用流或分块处理，不要只因为可配置上限就一次分配全部内容。

## 高级扩展

| 需求 | 扩展接口 |
| --- | --- |
| 自定义来源与平台安装 | IResourceFileSystem、IResourceFileInspector、ResourceSourceOptions |
| 自定义 Unity/SDK 加载 | IUnityResourceLoader、IResourceBackend |
| 文件解密 | IResourceDecryptionServices、IResourceKeyProvider |
| 清单编码 | IResourceManifestCodec |
| 网络请求与重试 | ResourceDownloadPolicy、IResourceDownloadRetryPolicy、IResourceDownloadUrlPolicy |
| 构建收集与任务 | ResourceCollectionExtension、IResourceBuildTask、ResourceBuildExtension |

扩展接口的拥有关系、主线程和关闭契约与内置实现相同。内部成员即使通过自定义程序集访问，也不属于稳定公开 API 承诺。

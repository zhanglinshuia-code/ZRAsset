# 下载与版本更新

[文档首页](../index.md) · [生命周期](loading.md) · 下一篇：[启动](startup.md)

## 准备与激活是两个阶段

准备把目标文件和清单校验后放到版本目录；激活才改变应用使用的版本。可以在旧内容仍被使用时准备新版本，但切换前必须结束旧资源的使用。不能因为“文件下载完成”就认为运行中的 Package 自动读取了新清单。

## 更新初始化

以下片段假设 remoteRoot 是该包远端文件根、cacheRoot 是本地公共缓存父目录，token 为取消令牌：

```csharp
var package = ResourcePackages.CreatePackage("core");
var network = new ResourceDownloadPolicy();
var downloads = new BundleDownloadOptions(remoteRoot, "bootstrap", cacheRoot,
    networkPolicy: network);
await package.ConfigureUpdatesAsync(ResourcePlatform.Current.BuildTarget, downloads,
    new ResourceInitializationOptions {
        Root = builtInRoot,
        ManifestFormat = ResourceManifestFormat.Json,
        NetworkPolicy = network
    }, token);
```

ConfigureUpdatesAsync 恢复版本状态但不等于完成资源加载。已有活动版本时调用 LoadActiveAsync；首次安装走明确首包激活或签名更新流程。推荐由统一启动控制器组合这些分支。

0.35 的策略规则：options.NetworkPolicy 非空时覆盖 downloads.NetworkPolicy；为空时沿用后者。不合并两者的请求头和回调。将同一个策略对象传给相关模块可避免配置分叉。

## 签名更新步骤

1. 从可信构建预置公钥，创建 ResourceReleaseTrustOptions，明确版本序号和来源策略。
2. 调用 FetchSignedReleaseAsync 获取并验证签名候选。
3. 调用 PlanPrepareSignedReleaseAsync 估算实际缺失文件与下载量。
4. 展示所需空间和下载量，取得业务需要的用户选择。
5. 调用 PrepareSignedReleaseAsync 下载并校验目标版本。
6. 关闭旧 Scope、Handle、实例及场景。
7. 调用 Package.ActivateAsync 切换，重新进入业务。

候选的公开 Manifest 是副本，修改它不能改变已经认证的内容。签名绑定发布描述，文件仍需长度和 SHA-256 检查。不要把从相同不可信网络临时取得的公钥直接作为信任根。

## 下载量、进度和取消

清单差异中的 EstimatedDownloadBytes 是逻辑差异，不知道设备上有哪些可复用文件。向用户显示实际下载量应使用准备计划的 DownloadBytes。缓存和已安装平台包会降低网络需求；计划完成后磁盘状态仍可能发生变化。

BundleDownloadQueue 支持共享文件任务、优先级、暂停、恢复、重试和字节进度。Pause 阻止启动新请求，已发出的请求继续完成。重试退避会归还下载名额，但仍保留目标文件写入锁，防止另一写入者污染断点。

UI 优先使用进度事件或低频快照。GetProgress 每次构造数组，不适合在大队列下无条件每帧调用。中途取消后保留的断点仍需验证响应范围、验证器和内容身份，不能直接信任同名 .part 文件。

## 标签与选择性准备

ResourceSelection 可按地址、标签等选择内容，准备计划会包含所选资源所需依赖。选择少量地址不一定只下载少量字节：若它们共享一个大 Bundle，仍需完整容器。

先 WarmupCatalogAsync / SelectAsync / PlanSelectionAsync 再进行查询或准备，避免第一次同步查询在大清单上集中构建索引。异步操作完成前不要修改传入的 DTO 和数组。

## 激活和回滚

正常通过 Package 包装入口 ActivateAsync/RollbackAsync 完成切换，让包同时管理磁盘版本与内存管理器。直接操作 Versions 是高级用法，必须自行保证 Package 的当前管理器同步更新。

激活遇到旧消费者未释放时会拒绝，先修复所有权。若准备成功而激活失败，不应重新下载所有已验证内容；应根据错误定位旧管理器关闭、文件可用性或指针提交。回滚也要关闭当前消费者，并确保上一版本文件可用。

多包联合更新使用 ResourceReleaseSetManager，以唯一集合指针提交全部成员版本；独立逐包激活不能提供集合一致性。

## 离线行为

离线应仅使用已验证缓存、首包和已安装平台内容。缺文件时失败或按明确业务策略回退，不能悄悄发网络请求。首次离线安装、缓存损坏、升级后离线恢复是不同场景，生产接入应分别测试。



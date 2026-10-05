# 配置、路径和默认值

[文档首页](../index.md) · [API 导航](api.md)

## ResourceInitializationOptions

| 字段 | 默认 | 解释 |
| --- | --- | --- |
| Root | null | 命名包默认首包目录；空字符串在更新来源中可显式表示不使用首包 |
| ManifestFormat | Json | Json、Binary、Encoded 三选一 |
| UnloadDelaySeconds | 5 | 非负且有限，资源空闲后的延迟回收 |
| RetryPolicy | null | 由下层建立其默认加载重试策略 |
| LoadOptions | null | 默认 Bundle 并发 4、对象加载并发 8、精确地址匹配 |
| DecryptionServices | null | 读取加密资源需显式提供 |
| ManifestKeys / ManifestCodec | null | 编码清单按实际编码方式提供 |
| NetworkPolicy | null | 初始化请求策略；更新配置中空值回退到下载选项策略 |

该结构体传值复制配置字段，但引用字段不是自动深拷贝所有外部服务。不要在操作期间修改可变 DTO、外部密钥服务或自定义回调所依赖的状态，除非明确按其线程和生命周期契约设计。

## 0.35 网络策略优先级

| 配置方式 | 生效结果 |
| --- | --- |
| InitializeAsync(options) | options.NetworkPolicy |
| ConfigureUpdates，options.NetworkPolicy 为 null | downloads.NetworkPolicy |
| ConfigureUpdates，options.NetworkPolicy 非空 | options.NetworkPolicy 整体覆盖下载选项策略 |
| 两处策略指向同一对象 | 使用同一对象，行为一致 |

同步与异步 ConfigureUpdates 使用相同规则，不把两个 Header 字典合并，也不串联两套回调。原 downloads 不被修改，包只创建自己的配置副本。0.34 的更新配置忽略 options.NetworkPolicy，升级时检查曾同时传入不同策略的调用。

## 下载与加载默认值

| 对象 | 参数 | 默认与范围 |
| --- | --- | --- |
| BundleDownloadOptions | MaxConcurrentDownloads | 3，允许 1–32 |
| BundleDownloadOptions | RequestTimeoutSeconds | 60，允许 1–600 |
| BundleDownloadOptions | RetryPolicy | 未提供时 new ResourceRetryPolicy(2, 0.5) |
| ResourceDownloadPolicy | MaxRequestsPerFrame | 8，至少 1 |
| ResourceDownloadPolicy | WatchdogTimeoutSeconds | 30，非负有限值 |
| ResourceLoadOptions | MaxConcurrentBundleLoads | 4，允许 1–64 |
| ResourceLoadOptions | MaxConcurrentAssetLoads | 8，允许 1–64 |
| OperationSystem | MaxMillisecondsPerUpdate | 5，协作预算 |
| ResourceFileIOOptions | MaxConcurrentOperations | 2 |
| ResourceFileIOOptions | BufferSizeBytes | 256 KiB，实际使用按输入与模式约束 |
| ResourceFileIOOptions | CooperativeSliceMilliseconds | 2 |

并发数不等于每帧可完成的任务数，也不意味着内存峰值不变。提高下载并发与加载并发会同时改变磁盘、网络、反序列化和内存压力。

## 网络来源规则

RemoteBaseUrl 必须是绝对 HTTP(S) 目录，不带查询和片段。默认 CacheRoot 是 Application.persistentDataPath 下的 ZRAssetCache。缓存版本必须是安全目录名。

未设置 credentialOrigins 时，请求头和配置回调仅应用于原始 origin。显式列表替代默认规则。包含凭据配置的请求禁用自动重定向。配置回调不能替换 URL、HTTP 方法或处理器；Range 和 If-Range 由续传协议管理。

## 路径示例

假设 PackageName=core，PackageVersion=v1，目标平台 StandaloneWindows64：

| 类型 | 路径 |
| --- | --- |
| 命名包构建 | Build/ZRAsset/StandaloneWindows64/Packages/core/v1 |
| 命名包首包 | Assets/StreamingAssets/ZRAsset/Packages/core |
| 未命名兼容构建 | Build/ZRAsset/StandaloneWindows64 |
| 未命名兼容首包 | Assets/StreamingAssets/ZRAsset |
| Package 更新缓存根 | 调用方 cacheRoot/ZRAssetPackages/core |
| 版本状态 | 包级缓存根/ZRAssetVersions/目标平台 |

远端根和 builtInRoot 已经指向该包的内容位置；框架不会随意在服务器 URL 上再追加 PackageName。构建输出、首包和缓存目录的布局并不完全相同，不要机械复制路径。

## 限额与格式

Binary 清单最多 64 MiB；Encoded 解码结果最多 64 MiB；普通文本入口编码字节最多 96 MiB；RawFile 便捷整段读取默认最多 32 MiB。联合清单另有 8 MiB、128 个成员包的限制。调用场景可以更严格，不能因为总上限较大而忽略接收和解压限额。

源码：[初始化选项](../../Runtime/ResourceInitializationOptions.cs)、[下载选项](../../Runtime/BundleDownloadQueue.cs)、[加载选项](../../Runtime/ResourceLoadOptions.cs)、[I/O 配置](../../Runtime/Download/ResourceFileIO.cs)。

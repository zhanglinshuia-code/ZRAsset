# 启动和 HybridCLR

[文档首页](../index.md) · [下载更新](updates.md) · 下一篇：[发布服务](release-service.md)

## 先分清资源更新与代码更新

Bundle 更新改变资源内容，HybridCLR 更新托管程序集。框架提供两者的构建与启动协作，但资源包版本变化不等于可以随意替换已经加载进进程的程序集。代码升级受宿主 AOT、热更程序集身份和加载顺序约束。

只使用资源更新时不需要安装 HybridCLR。需要 IL2CPP 热更时，先在宿主工程完成 HybridCLR 安装、热更程序集设置及匹配工具链准备，再运行 ZRAsset 构建流程。

## 三层启动对象

| 对象 | 责任 |
| --- | --- |
| HotUpdateStartupConfig | 编辑器保存启动地址、包身份、首包、并发及信任公钥配置 |
| HotUpdateBootstrap | 执行检查、准备、激活、资源与热更入口加载、恢复流程 |
| HotUpdateStartupController | 向 UI 提供下载确认、业务就绪、重试、取消和状态通知 |

HotUpdateStartupSession 管理一次可重试会话，处理重复启动、取消和关闭。业务不要在按钮每次点击时另建一套无关联的 Bootstrap，以免产生多个版本写入者。

## 配置步骤

1. 创建并保存 HotUpdateStartupConfig，固定 PlayerBuildId 与命名包。
2. 指定 SignedReleaseUrl 和 RemoteBundleBaseUrl。前者是签名描述接口，后者是内容根；不能交换。
3. 配置信任公钥、最低序号和首包目录。生产地址使用 HTTPS；本机 HTTP 例外只用于本地验证。
4. 若清单使用编码/加密，配置 EncodedBuiltInManifest，并在运行时注入相同密钥服务及 Codec。
5. 配置并发和加载预算，先采用默认值，通过真实设备测量调整。
6. 使用 CreateOptions 注入短期网络凭据和自定义来源，而不把令牌或密钥材料保存到配置资产中。

具体字段与默认值见 [HotUpdateStartupConfig](../../Runtime/HotUpdate/HotUpdateStartupConfig.cs)。

## UI 接线与就绪

参考 [ProductionStartupComponent](../../Samples~/Core/ProductionStartupComponent.cs) 和 [HotUpdateBootstrapComponent](../../Samples~/Core/HotUpdateBootstrapComponent.cs)。这些组件是可修改的示例；框架本身不规定游戏 UI 方案。

Controller 暴露 Changed、BusinessReadyRequested 等事件。显示等待下载确认时，用户同意后调用 ConfirmDownload。资源和代码加载完成后，业务仍需创建首屏、绑定依赖和检查关键数据；真正可交互时调用 ConfirmBusinessReady。业务失败应调用 FailBusinessReady，避免启动器误认为已成功。

默认 BusinessReadyTimeoutSeconds 为 60 秒。超时是业务启动失败的证据，不应通过把超时改成无限长掩盖遗漏回调。取消后应等待当前会话清理，再使用 RetryAsync。RecheckContentAsync 重新检查内容，不表示运行时卸载已加载程序集。

## HybridCLR 构建与加载顺序

1. 固定宿主 Player 构建及 PlayerBuildId，生成与该宿主匹配的 AOT 元数据。
2. 编译热更程序集，收集依赖和入口；构建热更清单及相关资源。
3. 将热更程序集、元数据和业务资源作为同一内容版本准备并校验。
4. 运行时按需要补充 AOT 元数据，再按依赖顺序加载热更程序集，然后调用配置入口。
5. 业务确认就绪后才视为启动成功。

Prefab/场景引用的热更组件类型必须在 Unity 反序列化需要它们之前就绪。宿主与热更内容身份不匹配时应停止，不能简单忽略校验继续加载。已加载代码的副作用和静态状态也不会随资源指针回滚而自动撤销。

## 回退与重新启动

启动失败恢复涉及持久化状态和本进程状态。资源文件可以回退到已验证版本，程序集加载状态却可能要求重启进程。HostCacheLifecycle 用宿主身份约束旧缓存，防止新 Player 错用旧 AOT/热更内容。

接入后至少演练：首次在线、首次离线、旧版升级、下载中退出、业务就绪失败、重启离线恢复、缓存损坏、宿主升级。上线前应在自己的业务场景和发行设备上检查实际行为。

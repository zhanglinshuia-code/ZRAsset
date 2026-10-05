# 从 YooAsset 迁移

[文档首页](../index.md)

本页说明迁移到当前 ZRAsset 的工作步骤，不承诺与特定 YooAsset 版本 API 或文件格式兼容。不要把原有清单和 Bundle 发布目录直接交给 ZRAsset；需要按 ZRAsset 的地址、依赖与版本协议重新构建，并用目标项目实际内容验证。

## 先迁移概念再迁移调用

| 原业务需求 | ZRAsset 中的落点 |
| --- | --- |
| 按逻辑包管理资源 | 命名 ResourcePackage |
| 稳定资源地址 | BundleBuildConfig Entries/Rules |
| 异步加载并持有资源 | AssetHandle、AssetCollectionHandle |
| 按界面/关卡统一释放 | ResourceScope |
| 实例和场景所有权 | InstanceHandle、SceneHandle |
| 按标签准备内容 | ResourceSelection 与准备计划 |
| 版本检查和下载 | ResourceVersionManager 签名检查/准备 |
| 多包一致切换 | ResourceReleaseSetManager |
| 业务启动与回退 | HotUpdateBootstrap / StartupController |

表中按需求映射，不表示两个框架的同名对象具有完全相同的返回值、取消行为或寿命。

## 推荐迁移顺序

1. 列出现有包名、地址、标签、资源类型、分包规则和业务关闭边界。
2. 选择一个最小业务模块，建立命名包并保留稳定地址。
3. 用 ZRAsset 重新构建内容，从本地首包完成加载。
4. 将所有权迁移为显式 Handle 或 Scope；检查 Prefab 克隆、场景、音频和 UI。
5. 接入新的缓存根、签名发布与更新启动流程。
6. 演练升级、取消、损坏、离线和回滚；最后迁移其他模块。

先在业务边界增加自己的资源服务接口，可以减少大量业务代码直接依赖某一框架返回类型。但接口必须保留必要的异步和释放语义，不能只返回裸 UnityEngine.Object 然后丢掉所有权。

## 特别检查

- `LoadAssetAsync` 返回 Handle，等待 `handle.Operation`，不机械替换其他框架的 Task 属性。
- 场景默认叠加加载，延迟激活须使用支持该能力的后端。
- 同步加载只在具备能力且内容已准备时使用。
- 取消单个消费者不一定取消底层共享请求。
- 下载完成、版本激活、代码加载和业务就绪是不同阶段。
- 旧缓存不按目录名称直接复用，文件和清单身份必须经过新协议验证。

先阅读[快速入门](../manual/quick-start.md)和[生命周期](../manual/loading.md)，再阅读[更新](../manual/updates.md)。

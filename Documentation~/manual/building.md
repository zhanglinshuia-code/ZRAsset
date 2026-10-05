# 资源收集与构建

[文档首页](../index.md) · [快速入门](quick-start.md) · 下一篇：[生命周期](loading.md)

## 三种名字

PackageName 是资源包身份，例如 core、ui；Address 是业务访问键，例如 ui/login；BundleName 是容器文件名，例如 ui_login.bundle。资源版本 PackageVersion 用于版本目录和清单身份。框架 UPM 版本 0.35.0 表示代码版本，与资源版本 v1/v2 独立。

稳定业务地址让素材目录可以调整而不用修改业务代码。默认按精确地址匹配，不要依赖大小写忽略或去扩展名的隐式规则；需要此类匹配时显式配置 LocationMatching，并检查冲突。

## 显式条目与目录规则

构建配置的 Entries 适合启动场景、关键 Prefab 和稳定业务地址。Rules 适合成批资源。规则地址由 AddressPrefix 加相对路径组成，通常保留扩展名。Rules 和 Entries 最终进入同一个分析过程，会共同进行地址、文件名和类型冲突校验。

| 设置 | 作用 | 容易混淆的地方 |
| --- | --- | --- |
| CollectionGroup | 管理逻辑分组与标签 | 不直接决定 Bundle 名 |
| Tags | 运行时选择性查询/准备 | 不等同于把所有同标签资源放进一个文件 |
| Packing | Separately、Directory、Together、Label | 决定容器粒度 |
| CollectorRole | 主资源或依赖用途 | 依赖项不一定作为业务可直接查询地址 |
| Disabled | 禁用某组或条目 | 修改后需要重新分析与构建 |
| ExtractSharedDependencies | 提取共享依赖 | 影响文件数、包体重复与加载依赖 |

场景不能和普通 Unity 资源随意混进同一 Bundle。RawFile/Archive 按字节处理源文件，不能用 Unity 对象的加载方式读取。

## 如何选择分包

按单资源分包便于独立更新，但增加文件数量、请求和元数据。按目录或业务模块合包减少文件数，却可能让一个很小的资源更新导致整包更新。先按业务同时使用/同时更新的边界分组，再检查构建报告的共享依赖和体积。不要单纯追求 Bundle 数量最少。

共享贴图如果被多个主资源引用，自动提取可以降低冗余，但也会形成更多依赖。加载一个资源时，框架准备其依赖闭包；一个依赖容器仍有任何消费者时不会卸载。更细的资源粒度依赖需要相应清单格式与构建配置配合。

## Built-in 与 SBP

BuiltIn 是无需安装 SBP 的默认后端。选择 ScriptableBuildPipeline 前先确认适配器可用。缺失 SBP 时应收到明确错误，不能认为会自动换回另一后端。两种后端统一生成 ZRAsset 清单和报告，但后端产物、缓存策略和依赖分析结果需要各自验证。

默认输出规则见[配置参考](../reference/configuration.md)。ForceRebuild 控制本次构建，并不删除全部共享缓存。CacheServerHost 为空表示使用本地缓存，设置后 SBP 使用其缓存服务器配置。不要通过清空整个 Unity Library 来验证普通增量构建。

## 文件类型

| FileType | 输入与输出 | 运行时入口 |
| --- | --- | --- |
| AssetBundle | Unity 资源/场景 → Bundle | LoadAsset、Instantiate、LoadScene |
| RawFile | 源文件字节 → 独立文件 | LoadRawFile |
| Archive | 多个原始文件 → 共享归档，清单记录区间 | LoadRawFile，遵守句柄和容器租约 |

一个 JSON 配置既可作为 TextAsset 放入 Bundle，也可作为 RawFile。前者使用 Unity 对象加载体系；后者直接读取字节。选择取决于业务消费方式，不取决于扩展名本身。

## 清单、加密和质量检查

JSON 清单适合检查，Binary 对应 `manifest.zrmb`，Encoded 对应 `manifest.zrme`。Encoded 可以包含压缩及加密信封，具体读取需要相同 Codec/密钥配置。资源加密与清单加密分别设置 key ID；构建配置不存储密钥材料。

构建质量策略在发布前检查文件、资源数量、重复依赖和基线变化等。不要在质量失败后把暂存目录当作可发布内容。扩展任务按 PlanReady、FilesBuilt、FilesEncrypted、ManifestReady、QualityPassed、BeforePublish 顺序执行；清单修改应在 ManifestReady 及之前完成。

## 一次可重复的构建流程

1. 固定目标平台、资源版本及配置；保存工程资源。
2. 在资源收集窗口检查地址、规则和冲突，查看依赖分析。
3. 执行构建，确认报告和清单生成成功。
4. 做本地首包复制或交给发布工具上传。
5. 从真实运行时初始化并加载关键资源，而不只检查构建退出码。
6. 发布后把该资源版本视为不可变；内容变化使用新版本号。

构建配置源码：[BundleBuildConfig](../../Editor/BundleBuildConfig.cs)。

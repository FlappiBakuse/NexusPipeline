# 插件运行与安装

本页定义宿主对插件发现、安装事务、加载和资源释放的责任。包字段与公开服务见[插件 API](../reference/plugin-api/README.md)，作者与发行流程见[Plugins 文档](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/README.md)。

## 责任与身份

| Owner | 责任 |
|---|---|
| `PluginManager` | 扫描当前安装目录、校验兼容性、加载插件与登记运行态；不访问网络决定下载策略 |
| `PluginRepositoryService` | 编排官方 catalog、README 与商店投影 |
| `PluginRepositoryOperations` / `PluginPackageService` | 串行登记安装操作、校验包并暂存冻结候选 |
| `PluginInstallRecovery` | 启动时应用 pending、交换目录并提交安装归属 |
| `PluginManagementView` | 为 Web、MCP 和状态接口提供同一运行态及商店归属投影 |

`manifest.name` 是稳定的小写 kebab-case 逻辑身份，用于配置、密钥、偏好和脚本声明；`artifactName` 是严格区分大小写的源码、安装与发行物理身份。schema 2 的安装目录为 `plugins/<ArtifactName>/`，必须与 manifest 一致。

管理器的 summary、历史和前端资产投影位于同名 partial 文件，共享同一运行集合与快照缓存。安装恢复按内置种子、状态读写、安装、卸载和路径职责分文件，保留单一 Sync 及 schema 3 阶段顺序。UI、Web API、历史贡献注册、scope 存储与验证各有对应文件，不共用通用注册中心。

## 发现、缓存与通道

官方来源固定为 NexusPipeline-Plugins。catalog 只作为索引，运行时事实来自 manifest，展示信息来自 store 与 README。目录校验包括 schema、重复身份、兼容性、官方 URL、包大小和摘要。

目录在五分钟验证有效期内复用内存快照；过期或手动刷新使用 HTTP validator。304 更新验证时间，200 仅在内容摘要变化时替换快照。网络失败只允许复用同通道的已验证磁盘缓存，并明确标记 stale；没有可信缓存时报告不可用。README 和本地管理投影分别按内容身份、文件指纹及运行修订失效。

stable 与 develop 的缓存、ETag、候选、pending 和 ownership 身份隔离。preview 同版本内容摘要变化可以更新；摘要相同不因源码 SHA 变化重装。可信 preview 可被同版本 stable 替换，高版本不自动降级。候选冻结后不得按名称重新取得另一份包。

## 安装事务与恢复

1. 从已验证 catalog 冻结候选，下载时限制大小并核对 SHA256。
2. 解压到 `.nxp/state/plugins/staging/`，拒绝越界路径、重复条目和超限资源。
3. 二次核对 manifest 身份、版本、类型、API、能力和实际载荷。
4. 写入 pending 并返回重启要求；当前运行插件不在请求中被替换。
5. 下次启动先恢复并应用 pending，再扫描和加载插件；交换前失败还原旧目录，交换完成后的 journal 幂等收尾。

更新和卸载必须具有可信 ownership；未验证的本地目录不能由商店接管。catalog 中已下架但仍有可信归属的插件可以卸载。归属还须匹配 artifact、通道及当前目录，不能跨通道回退。

持久状态位于 `.nxp/state/plugins/`：catalog 及其元数据是可验证展示缓存，ownership 记录安装事实，pending、staging 与 backup 组成跨重启事务。pending/ownership 只接受 schema 3，记录 channel、sourceCommit 和安装意图；stable 的 sourceCommit 可为空，develop 必须为完整 SHA。未知状态或恢复失败保留原文件及 staging，并阻断相关写入。宿主更新保留用户插件目录。

## 运行加载与扩展边界

managed-code 通过 `INexusPlugin` 生命周期与 `IPluginHostContext` 使用公开服务；数据化专项通过 manifest、resolve 和后端脚本推导 profile，不包含浏览器代码或程序集。数据化插件默认启用，managed 插件默认禁用，启停偏好在重启后生效。

加载前比较 minHostVersion 与 API 兼容性；不兼容插件保留管理元数据，但不加载程序集或注册专项能力。前端资源只来自已启用、运行有效、版本匹配且清单合法的模块。资源来源、公开端口与浏览器生命周期分别见[访问契约](../reference/plugin-api/access.md)、[Managed API](../reference/plugin-api/managed.md)和[Frontend API](../reference/plugin-api/frontend.md)。

脚本实例持久化插件身份与根路径等声明，操作开始时解析当前 profile，并把有效配置、脚本与输入冻结到计划或会话。候选输入响应携实际 inputName；启动行为由冻结计划消费用户设置，不能根据展示名称猜测能力。

capability 声明不自动赋予业务实现。通知、模拟器和执行准入由宿主持有，插件只能经已接入的 key 或公开端口调用；执行预览还须声明 execution-preview-client 并满足前端模块与运行资格。

## 生命周期与失败隔离

managed 插件初始化、启动和停止有二十秒截止时间。停止时撤销后台任务、UI/Web API/历史与用户设置贡献、徽章、事件及 provider 注册；用户运行事件按插件作用域跟踪并有界排空。某个贡献失败不使其他插件或用户的读取失败。

配置、密钥、用户数据、实体 scope 与二进制资产按插件身份隔离。JSON 损坏时保留原始 corrupt 现场，再按相应存储规则处理空值；后续保存不能覆盖原始证据。插件不拥有宿主 SMTP 身份与网络策略；统一出口见[通知与网络边界](observability.md)。

## 代码与验证入口

实现位于 `src/Modules/Plugins`，公开契约位于 `src/NexusPipeline.Plugin.Abstractions`；安装恢复与兼容性回归见 `tests/NexusPipeline.Tests/Plugins`，执行方式见[测试命令](../testing/commands.md)。受控测试不证明外部插件的真实业务成功。

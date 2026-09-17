# 插件运行与安装

## 插件仓库与安装事务

官方插件源固定为 `FlappiBakuse/NexusPipeline-Plugins`。每个正式源码插件目录维护 `plugin.json`（运行时事实）与 `store.json`（商店展示元数据），源码按 `plugins/general/`（managed-code）和 `plugins/specialized/`（data-specialized）分类；GitHub Actions 据此生成根目录 `catalog.json` 与扁平 `packages/<artifactName>/`。schemaVersion 2 的 manifest 必须使用小写 kebab-case 机器 ID，并声明严格区分大小写的 `artifactName`；源码目录、宿主安装目录、发行目录和 ZIP 名称均使用 artifactName，配置、密钥、作用域和偏好仍使用机器 ID。版本使用 `major.minor.patch`、`-beta.N` 或 `-rc.N`，按 beta、rc、stable 顺序比较；catalog 可选提供 `createdAt` 与最新 changelog 日期 `updatedAt`，缺失的创建日期按空值处理。catalog 条目包含名称、正式 artifactName、显示信息、受限 Nexus 版本、插件类型、最低宿主版本、官方 raw 包地址、包大小、SHA256 和最近更新记录。客户端对 catalog 做 schema、重复名称、artifactName、官方 URL、版本、大小、SHA256、可选 createdAt 和 changelog 校验，并将最近成功目录缓存到 `.nxp/state/plugins/catalog-cache.json`，同时写入 `.nxp/state/plugins/catalog-cache.meta.json` 保存源地址、ETag、Last-Modified、最近验证时间和内容 SHA256。宿主保留 catalog 作为高效索引，新增插件由自身 manifest/store 驱动生成。

插件页默认显示「插件仓库」，提供浏览、安装、更新和卸载；「本地插件」继续显示当前运行目录的分组与启停状态。catalog 在最近 5 分钟验证有效期内直接复用内存快照；过期或手动刷新时携带 ETag/Last-Modified 发起条件请求，`304` 只更新时间和验证元数据，`200` 仅在内容 SHA256 变化时替换 catalog 快照；没有 HTTP validator 时仍以校验后的内容哈希判断是否变化。网络失败时显示经校验的磁盘缓存并标记为 stale，没有可用缓存则返回仓库不可用状态。README 按官方 artifact/version 记录条件验证缓存，本地 README 按路径、文件大小和 LastWriteTimeUtc 指纹复用。`PluginManager` 的本地插件管理投影使用运行时修订缓存，启停、安装/更新/卸载登记、重载和归属/待处理事务变化会使其失效；前端保留当前列表和详情，在后台验证期间继续展示。

插件安装/更新按以下顺序执行：

1. 从 catalog 下载包，限制响应大小并校验声明大小与 SHA256；
2. 将 ZIP 解压到 `.nxp/state/plugins/staging/`，拒绝绝对路径、`..`、重复条目、越界路径和超过资源上限的压缩内容；
3. 检查根 `plugin.json` 与 catalog 的名称、artifactName、版本、类型、API 和 capability 一致，并验证数据插件文件或 managed-code 入口程序集存在；
4. 写入带有机器 ID 和 artifactName 的 `pending.json`，返回“重启后生效”；
5. 下次启动应用 pending 事务，再由 `PluginManager.LoadAll` 扫描当前插件目录。`PluginInstallRecovery` 使用 artifactName 进行目录交换；交换前失败会恢复旧插件，交换完成后的 journal 可幂等重试。

`PluginManager.LoadAll` 在运行时再次比较每个插件的 `minHostVersion` 与宿主当前受限版本。手动放入最低版本过高的插件会保留在管理投影中并标记 `Incompatible`，同时跳过数据插件能力/解析器注册与 managed-code 程序集加载；前端详情显示最低宿主版本和升级提示。Plugin API 不兼容继续使用独立的运行时错误码，避免与宿主版本不兼容混淆。

插件状态持久化在 `.nxp/state/plugins/`：`catalog-cache.json` 为经校验的目录快照，`catalog-cache.meta.json` 为条件验证元数据，`ownership.json` 为商店安装版本和 SHA 归属，`pending.json` 为跨重启事务，`staging/` 与 `backup/` 为操作现场。卸载只依赖本地插件目录和已验证的归属记录，catalog 暂不可用时仍可创建卸载事务；本地已安装但已从 catalog 移除且仍有归属记录的插件以 `unlisted` 状态保留卸载入口。未验证归属的本地插件不进入商店更新/卸载事务。现有用户 `plugins/` 在宿主更新时保留；宿主更新器只交换 exe 与 `wwwroot/`。

插件配置、密钥和作用域 JSON 解析失败时保留 `.corrupt-<timestamp>-<guid>` 现场，再以空值继续运行；后续写入不会覆盖原始损坏文件。managed-code 生命周期初始化、启动和停止均有 20 秒截止时间；用户运行事件在插件作用域中跟踪，并在清理时执行有界排空。

宿主启动时对旧外观数据执行一次性格式搬迁：读取 `config/appearance.json`、`user-assets/appearance/wallpapers/` 与 `.nxp/state/appearance-runtime.json`，把壁纸资产导入旧配置记录的原提供方插件命名空间（资产 scope 为 `wallpapers`），再把搬迁载荷原子写入该插件的 `legacy-appearance-import` 作用域数据；成功后写标记 `.nxp/state/appearance-migration.json`，任一步失败都不写标记并在下次启动重试，旧文件保留。搬迁只做格式转换，载荷结构、字段语义与消费约定见 [Plugin API 参考](../reference/plugin-api/managed.md) 的「旧外观数据搬迁」。

managed-code 插件可以通过当前 Plugin API v1.8 注册用户列表徽章、通用 UI 贡献、作用域数据、二进制资产、插件 Web API、历史展示、插件自有本地化资源、模拟器支持 provider 和 SMTP 收件人覆盖。宿主通过 `GET /api/plugin-contributions/user-list-badges` 一次读取全部用户的聚合展示数据，按插件贡献提供的顺序投影并校验；用户列表不理解具体插件业务，单个处理器异常也不会阻断其他用户或插件的徽章读取。插件徽章读取应使用本地状态，不能在列表请求中执行网络签到。Frontend API 1.5 插件以 `web/` 下的构建后 ES module/CSS 扩展页面；只有精确版本匹配的插件会加载前端资源。宿主只提供通用资产存储、二进制 Web API 与通用外观表面（主题、token、背景表面与外观变更事件），壁纸配置、配额、校验、去重、轮换与配色属于插件业务；运行预览服务由宿主按活动执行目标提供，前端通过 sidecar slot 展示，插件词典通过 `host.i18n` 按请求语言读取。



## 插件扩展与所有权

数据化专项插件采用运行目录 `plugins/<ArtifactName>/plugin.json + data/`，managed-code 插件采用同一目录下的 `plugin.json + entryAssembly`。manifest 的 `name` 是稳定的小写 kebab-case 机器 ID，`artifactName` 是严格区分大小写的源码、安装和发行物理身份。实现与主仓库分离，官方源目录和包资产位于 `NexusPipeline-Plugins`。宿主保留 Generic ADB、MuMuManager 与通知基础设施；雷电、夜神和 BlueStacks 的专属探测与驱动由可选的 managed-code `EmulatorSupport` 插件提供。数据化 capability 通过 `plugin.json` 的 `capabilities` 数组登记，专项插件用 `emulator` key 声明其脚本实例支持模拟器启动方式。

插件仓库与本地运行目录：

- `PluginManager` 只扫描当前安装目录 `plugins/<ArtifactName>/`，schema 2 要求物理目录名与 manifest.artifactName 完全一致；逻辑身份取 manifest.name。它不读取网络，也不决定包下载策略。
- `PluginRepositoryService` 只信任固定官方 `catalog.json`，先使用 5 分钟验证有效期内的内存快照；过期时以 `catalog-cache.meta.json` 中的 ETag/Last-Modified 发起条件请求，`304` 复用快照，`200` 以内容 SHA256 决定是否更新 catalog 文件；请求失败时使用已校验的磁盘缓存并标记 `stale`，没有可用缓存则返回 `repository_unavailable`。更新资格还要求本地目录与已验证 `ownership.json` 归属一致；catalog 由插件自身的 manifest、store 和包生成。
- 插件 ZIP 经 SHA256、大小、ZIP 条目路径/压缩资源上限和 manifest 二次校验后进入 `.nxp/state/plugins/staging/`；`pending.json` 记录逻辑机器 ID、目标 artifactName 和跨重启事务，启动时应用事务后扫描当前插件目录。
- `.nxp/state/plugins/ownership.json` 记录由官方商店安装的版本、SHA 和 artifactName；`catalog-cache.json` 与 `catalog-cache.meta.json` 共同构成可验证的离线展示缓存。更新器只交换宿主 exe 与 `wwwroot/`，运行时 `plugins/` 保持原目录。
- Web 端点为 `GET /api/plugins/store`、`POST /api/plugins/store/refresh` 和 `POST /api/plugins/store/{name}/{install|update|uninstall}`；启用、禁用与商店操作成功响应统一返回 `restartRequired`，前端据此提示重启生效。
- managed-code 用户级设置端点为 `GET /api/plugin-contributions/user-global/{userId}` 与 `PUT /api/plugin-contributions/user-global/{userId}/{pluginName}/{contributionId}`；用户列表徽章使用单次聚合端点 `GET /api/plugin-contributions/user-list-badges`，宿主负责异常隔离、白名单校验和 HTML 展示数据投影。
- v1.3 通用 UI 贡献使用 `POST /api/plugin-contributions/ui/query`、`PUT /api/plugin-contributions/ui/{plugin}/{contribution}` 和 `POST /api/plugin-contributions/ui/{plugin}/{contribution}/action/{action}`；插件 Web API 使用 `GET|POST|PUT|PATCH|DELETE /api/plugin-api/{plugin}/<route>`。v1.5 插件本地化使用 manifest 的 `localization` 资源和 `PluginLocalizedText` 引用；v1.6 增加通用二进制资产端口与二进制 Web API 传输；v1.7 增加 managed-code 模拟器支持 provider 注册端口；v1.8 为插件通知增加可选 SMTP 收件人覆盖，留空时使用宿主全局收件人。
- `GET /api/plugin-runtime/frontend` 只发布已启用、运行态有效、版本兼容且资源清单有效的前端模块；公开静态资源限定在插件 `web/` 目录，并仅支持 GET/HEAD 与白名单 MIME。

宿主外部网络出口：`OutboundHttpClientProvider` 按每次请求读取当前 `AppSettings`，统一供插件 catalog/包下载、宿主更新和 Webhook 使用。代理模式为 `none`、`system`、`http`；自定义代理的密码通过 `SecretStore` DPAPI 存储，API/UI 只返回占位符。SMTP、Control API、MCP 以及插件子进程不经过该出口；loopback 目标始终禁用代理。

插件分类：

| 类别 | 形态 | 职责 | 启用语义 |
|---|---|---|---|
| managed-code 插件 | 独立项目 + `NexusPipeline.Plugin.Abstractions` API v1.8 + manifest | 通过通用用户数据、声明式设置、作用域数据、二进制资产、历史展示、插件 Web API、用户列表徽章、用户运行事件、HTTP、通知、本地化、可选模拟器 provider 和 SMTP 收件人覆盖实现插件能力 | 默认禁用；启用后重启加载，API 不兼容或初始化失败会进入对应运行态 |
| 数据化专项插件 | `plugins/<ArtifactName>/plugin.json + data/`（`DataSpecializedPlugin` 扫描注册） | 接管专项脚本实例配置：`Resolve(rootPath, inputs)` 按 `data/resolve.json` 推导主程序/参数/配置/日志/判断脚本，`inputs` 为实例保存的用户输入值（`{input:名称}` 内联替换）；`logPath` 可为空 = 判定日志走进程 stdout | 默认启用；偏好以机器 ID 为 key 写入 `AppSettings.PluginPreferences`，重启后应用 |

> **通知通道**：宿主脚本与队列的 Webhook/SMTP 通知由 `NotificationDispatcher` 发送；managed-code 插件可通过 `IPluginNotificationService` 提交 `PluginNotification` DTO，沿用宿主全局渠道配置。Plugin API v1.8 允许通知按需覆盖 SMTP 收件人；SMTP 服务器、身份与 Webhook 配置仍由宿主管理。GameCheckIn v0.3.1 的任务级通知通过宿主全局渠道发送，空白 SMTP 收件人继承全局收件人。

Capability 扩展约束：

- 数据插件 capability 通过 key 登记；managed-code 插件只通过 API v1.8 服务端口工作，宿主不把后台任务 capability 当作专项脚本选择器。
- 数据化插件可在 `plugin.json` 增加 `capabilities: ["..."]`；未知 key 由宿主登记但不自动赋予业务语义。`emulator` 只声明专项脚本可选择安卓模拟器启动；managed-code 驱动插件通过 `IPluginHostContextV1_7.EmulatorSupport` 注册 provider。
- `PluginSummary` 负责 manifest 与本地展示元数据；`PluginManagementView` 负责跨控制面共享的运行态、展示元数据、商店归属和 pending 事务字段，Web、MCP 与状态接口从同一投影读取。
- Plugin API v1.8 继续提供显式 `IPluginHostContext` / `IPluginHostContextV1_1` / `IPluginHostContextV1_2` / `IPluginHostContextV1_3` / `IPluginHostContextV1_4` / `IPluginHostContextV1_6` / `IPluginHostContextV1_7` / `IPluginHostContextV1_8` 服务端口；插件全局配置、插件级密钥、按用户配置/密钥、实体作用域数据和二进制资产分层存储于 `config/plugins/`，managed-code 插件停止时后台任务、UI/Web API/历史贡献、用户设置贡献、用户列表徽章、事件订阅与模拟器 provider 注册统一取消。本地化资源随插件目录校验并按请求语言投影。

执行预览属于宿主控制的能力。插件需在 manifest 中声明 `execution-preview-client`，同时满足已启用且存在前端模块，才能通过 `ExecutionPreviewService` 获取预览；具体截图实现仍由宿主持有，插件身份负责能力声明与准入。

编写插件：插件的 manifest、`resolve.json`、判断脚本和配置还原描述组成独立契约。详细字段、示例、路径模板、判断脚本输入输出、配置还原 DSL 与部署约束统一维护在[插件 API 索引](../reference/plugin-api/README.md)；本节只说明宿主模块边界和代码定位。

- managed-code 插件实现独立 API 项目的 `INexusPlugin` 生命周期，并按需使用 `IPluginHostContextV1_8`；v1.7 继承 v1.6 的通用用户数据、声明式 UI、作用域数据、二进制资产、历史展示、插件 Web API、用户全局管理、用户列表徽章、用户运行事件、HTTP、日志、通知、任务和本地化端口，并新增模拟器 provider 注册；v1.8 增加 `PluginNotification.SmtpTo` 收件人覆盖。
- 需要前端的插件在 manifest 中声明 `frontend-module` 与 Frontend API `1.5`，入口位于 `web/` 并导出 `activate(host)`；入口由插件的 Vue/TypeScript/Vite 构建链生成，需要前端本地化时增加 `localization` 与 `i18n/` 词典，启用且精确匹配后由宿主加载，版本和声明变化继续经过 manifest、路径和资源校验。
- 数据化专项插件由 `plugins/<ArtifactName>/plugin.json + data/` 描述，`DataSpecializedPlugin` 负责发现和注册；`name` 继续作为脚本实例和运行时逻辑身份。脚本实例持久化 `PluginType + RootPath` 等稳定声明，宿主在 API、准入、配置编辑和运行时解析当前 profile，并将当次操作的有效结果冻结到执行计划或会话标记。
- 通知、模拟器和执行准入属于宿主能力；插件通过明确 capability 或公开 API 端口接入，不直接访问宿主组合根、领域模型或 Web 层。

数据化专项 profile 的输入候选响应携带实际 `inputName`，宿主前端按响应消费输入契约；`self-managed-pc-launch` 的启动开关、参数和等待时间保留在用户设置中，仅由运行时计划决定 PC 模式下的宿主启动行为。

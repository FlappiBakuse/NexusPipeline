# 控制面能力现状（Control Plane）

本文件记录可管理能力在 Web、CLI 和 MCP 三个控制面的入口现状，供开发与排障参考。MCP 只保留面向 Agent 的核心子集；删除、密钥、插件安装/开关、商店、服务重启和更新应用等高风险或低频运维操作由本地 CLI 与管理页面承担。

## 能力现状

| Capability | Web | CLI | MCP |
|---|---|---|---|
| 脚本读取 | `GET /api/scripts` | `script list/get` | `list_scripts` / `get_script` |
| 脚本类型图标 | `GET /api/scripts/type-icons/{typeId}` | `script type-icon --output` | 由 CLI/Web 承担 |
| 脚本写入 | 脚本 CRUD API（新建/更新专项脚本实例的响应顶层附带 `validation`：script-save 语境的配置校验结果，含角落通知；通用脚本、无绑定用户或无可运行的任务协议诊断时缺省） | `script create/update/delete`（返回同一校验投影） | `create_script` / `update_script`（删除走 CLI） |
| 专项配置探测 | `POST /api/scripts/probe`（专项插件按根目录 + `pluginInputs` 用户输入推导，响应含插件声明的 inputs 表单 schema） | `script probe --plugin --root [--file inputs.json]` | `probe_script` |
| 用户读取 | `GET /api/users` | `user list/get` | `list_users` / `get_user` |
| 用户写入 | 用户 CRUD API | `user create/update/delete` | `create_user` / `update_user`（删除走 CLI） |
| 用户绑定 | `/api/users/{id}/bindings` | `user binding ...` | `add_binding` / `update_binding`（删除走 CLI） |
| 用户绑定配置编辑 | `/api/users/{id}/bindings/{scriptId}/edit-config`（仅回环请求） | `user binding config ...` | 由 CLI/Web 承担 |
| 用户全局设置 | `/api/users/{id}/global-settings` | `user global-settings get/update` | 由 CLI/Web 承担 |
| 队列读取 | `GET /api/queues` | `queue list/get` | `list_queues` / `get_queue` |
| 队列写入 | 队列 CRUD API | `queue create/update/delete` | `create_queue` / `update_queue`（仅安全队列） |
| 运行 | 调度中心/Control API | `run script/queue` | `run_script` / `run_queue` |
| 运行计划解释 | `POST /api/dispatch/explain/script`、`POST /api/dispatch/explain/queue`（只读） | `run script/queue --dry-run` | `explain_script_run` / `explain_queue_run` |
| 取消 | `/api/cancel`、系统操作取消 | `run cancel` | `cancel_run` / `cancel_system_action` |
| 运行观察 | `/api/status`、`/api/events`（SSE）、运行详情 | `status`、run 轮询 | `get_status` / `list_runs` / `get_run` |
| Provider 配置初始化 | `POST /api/provider-configurations`、`GET /api/provider-configurations`、`GET /api/provider-configurations/{operationId}`、`POST /api/provider-configurations/{operationId}/cancel` | `provider-config start/list/get/cancel` | `list_provider_configurations`（只读） |
| 历史 | `/api/history/summary`（范围、脚本、队列、用户、状态筛选）→ `/api/history/dates` → `/api/history/users?date=...` → `/api/history?date=...&userKey=...`；详情 `/api/history/detail`；截图 `/api/history/image` | `history list/dates/users/summary/get/image`，支持范围、用户键及状态筛选 | `list_history` / `get_history` / `get_history_summary` |
| 本机路径选择 | `POST /api/native-dialog`（仅回环请求） | `native-dialog --file`；路径也可直接传入其他命令 | — |
| 插件读取 | `GET /api/plugins` | `plugin list/get` | `list_plugins` |
| 插件商店/安装/开关/批量更新 | 插件页 + Control API（`POST /api/plugins/store/update-all`） | `plugin install/update/uninstall/enable/disable`、`plugin store list/get/refresh/update-all` | 由 CLI/Web 承担 |
| 插件用户设置 | 贡献接口 | `plugin user-settings ...` | 由 CLI/Web 承担 |
| 客户端会话 | `POST /api/client-sessions`（Web）；桌面由已验证 IPC 签发 | `client-session create/get/revoke`（Web 能力） | — |
| 插件网页登录 | 已验证桌面 IPC 发起；`GET /api/browser-login/{operationId}` 查状态、`DELETE` 取消 | — | — |
| 设置读取 | 设置 API | `settings get` | `get_settings`（密钥脱敏，包含配置修复开关和宿主语言） |
| 设置写入 | 设置 API | `settings update` | 由 CLI/Web 承担（MCP 只读） |
| 系统诊断 | `GET /api/diagnostics`；`POST /api/diagnostics/export`；受管 artifact 下载 | `doctor`、`doctor export --output <新文件>` | `get_diagnostics` |
| 通知截图开关 | 设置 API（`webhookScreenshotEnabled` / `smtpScreenshotEnabled`） | `settings update` | `get_settings` 只读返回开关状态 |
| 更新 | 更新 API | `update status/check/download/cancel/apply` | `get_update_status` |
| 任务摘要与计划 | 用户页任务概览、绑定任务预览、运行报告 | `user task-summaries`、`user binding task-plan`、`run tasks` | `get_task_summaries` / `get_task_plan` / `get_run_tasks` |
| 配置修复 | 预览后按 token 提交 | `user binding repair-preview/repair` | `preview_config_repair`（只读） |
| 配置编辑状态 | 编辑会话与绑定快照状态 | `script edit-sessions`、`user binding config-status` | 由 CLI/Web 承担 |
| 今日概览布局 | 卡片目录、可见卡片与顺序 | `dashboard cards/get/update` | `get_dashboard`（只读） |
| 扩展页面与贡献 | 插件页面、卡片、配置字段和动作 | `plugin frontend`、`plugin ui query/save/action`、`plugin request` | 不提供通用插件调用 |
| 运行截图 | execution-preview 二进制 | `run preview --plugin --output` | 由 CLI/Web 承担 |
| 资源约束与文件浏览 | `/api/limits`、`/api/fs/browse` | `limits`、`fs browse` | 由 CLI/Web 承担 |
| 访问令牌回读 | 设置专用读取入口 | `settings access-token` | 禁止 |


HTTP 更新状态与 MCP `get_update_status` 提供可空的 `lastApplyFailure`，字段为 `transactionId`、`targetVersion`、`code` 和 `atUtc`。它投影最近一次有效失败结果，仅在目标版本高于当前运行版本时显示；旧结果和未知文件保持原字节。

更新状态 `cancelling` 表示原操作 IO 与拥有文件清理仍在结束，期间拒绝新的检查、下载或应用。可空 `installerToolsWarning` 表示安装专用工具需要 Setup 修复；不将缺失工具假造为 Host 副本。

更新状态投影在发现候选版本后补充 `policyVerified`、`canDownload`、`manualUpdateRequired`、`updateBlockCode`、`barrierVersion`、`migrationUrl` 和 `policyError`。`updateBlockCode=breaking-update` 时，页面显示手动下载安装包与配置迁移指引，下载、下次启动应用和自动应用入口均被后端拒绝；策略无法验证时使用 `policy-unavailable` 并保持 fail-closed。

插件读取的本地列表、商店列表和详情投影均可提供 additive 字段 `createdAt`；该字段来自 catalog 的可选创建日期，缺失时返回空值，`updatedAt` 继续表示最新 changelog 日期。

自动更新由设置中的 `UpdateCheckEnabled`、`UpdateAutoApplyEnabled` 与 `PluginAutoUpdateEnabled` 分别控制。前两项同时开启时，程序启动恢复收尾后、宿主服务启动前会检查并尝试下载、应用宿主更新；已验证的 `Ready` 包也会在该阶段应用。检查或下载超时/失败时继续启动当前版本，同一目标自动失败后冷却 12 小时；策略屏障不允许自动下载。`PluginAutoUpdateEnabled` 独立控制插件更新：启动阶段在插件事务恢复及加载前检查并暂存符合条件的已安装插件，运行期间每 12 小时检查更新，并在维护租约空闲时安排安全重启。手动宿主更新和插件商店批量更新入口保持可用。

GameCheckIn 的独立签到页面由插件导航注册；状态读取位于 `GET /api/plugin-api/game-checkin/state`，任务新增、更新和删除使用 `/api/plugin-api/game-checkin/tasks`，排序使用 `PUT /api/plugin-api/game-checkin/tasks/order`，手动执行使用 `POST /api/plugin-api/game-checkin/tasks/run`。每个任务保存自己的计划和平台凭据；通知通过宿主全局渠道发送，任务可覆盖 SMTP 收件人。v2 任务存储不导入 v0.3.0 的任务数据。

网页登录使用固定 flow、限定采集字段与独立临时 Electron 会话。外站页面不能调用 Nexus IPC；候选通过只读身份验证及窗口清理后才对当前编辑器生效。`X-Nxp-Client-Session` 由宿主验证并注入插件请求，JSON 中自报客户端身份无效。Web 会话保存在当前页面内存，不能获取 native 能力。详见[网页登录与客户端会话](plugin-api/browser-login.md)。

GameCheckIn 的 `credentials/editors`、`credentials/editors/state`、`credentials/draft`、`credentials/lease` 与 `credentials/login/prepare` 管理本客户端的编辑会话和字段代次。`POST tasks/credential/read` 是唯一明文回读入口；自动来源凭据先经 `POST tasks/credential/reveal/confirm` 主动确认，授权最多 24 小时且随当前 Host/客户端会话失效。普通任务 DTO、候选状态、轮询和诊断保持脱敏；专用响应使用 `no-store`。任务保存引用后端候选句柄，成功提交才消费，取消编辑保留已保存值。

## 命令输入与前端对应关系

CLI 管理宿主持久化状态、执行生命周期和插件公开接口；每次写入仍经 Control API 的校验、租约和原子持久化。命令帮助可用 `NexusPipeline.exe help --json` 查询。所有新增命令沿用 `--json` envelope 和非零失败退出码；JSON 文件可用 `--file -` 从标准输入读取。

- `script probe --plugin <id> --root <path> --file <inputs.json>` 的文件只含输入键值对象；创建/更新脚本文件中的对应字段仍为 `pluginInputs`。
- `provider-config start --file <request.json>` 接收前端同一初始化请求，返回操作 ID；用 `get <operationId>` 查询并用 `cancel <operationId>` 取消。取消响应表示已请求，最终状态和清理须继续查询。
- `user binding task-plan/config-status/repair-preview <user> <script>` 支持稳定 ID 或唯一名称；`repair` 的文件包含预览返回的 `token`，过期时由后端拒绝。绑定排序使用 `user binding reorder <user> --ids <完整脚本ID列表>`。
- `dashboard get` 返回 `{etag,snapshot}`；`dashboard update --etag <原始带引号ETag> --file <json>` 的正文只含 `schemaVersion`、`catalogRevision`、`visibleCardIds`。同语言读取后再修改，遇到冲突重新读取，不能覆盖并发编辑。
- `history summary/dates/list` 接受 `--from`、`--to`、`--days`、`--script`、`--queue`、`--user-key`、`--status`；`history users --date YYYY-MM-DD` 读取当日用户索引。历史 ID 和用户键按记录使用，不因当前对象改名或删除而重新解析。
- 图标、历史截图和运行预览用 `--output <新文件>` 下载，拒绝已有文件、链接或缺失父目录；输出回执含长度及 SHA256。预览尚未产生图像时返回 `available=false`，不创建文件。
- `plugin ui query --file <json>` 读取指定 slot/context；`save <plugin> <contribution>` 与 `action <plugin> <contribution> <action>` 使用同一声明式贡献请求。插件自身路由由 `plugin request <plugin> <GET|POST|PUT|PATCH|DELETE> <相对路径>` 调用，JSON 用 `--file`，上传用 `--binary <文件> --content-type <mime>`，下载用 `--output`；路径不能跳出指定插件。请求仍受插件注册的访问策略限制。
- 需要编辑会话的插件请求先用 `client-session create --json` 获取 Web 会话，将输出保存在调用者管理的文件，再以 `--session-file <json>` 传给插件命令；结束后 `client-session revoke --session-file <json>`。会话文件含凭据，不进入版本库。CLI 不伪造桌面身份，不能启动受信任 Electron 网页登录或获取 native 能力。
- 网页路由、弹窗开合、浏览器显示语言/外观偏好和窗口控制属于当前前端客户端状态，没有宿主全局写入命令。原生选择器仍需本机用户交互；自动化调用直接提供路径。运行观察可使用 `status`、`run list/get/tasks` 轮询，不依赖页面 SSE 连接。

MCP 的队列创建/修改只接受 `completionAction=none`，修改已有危险队列也会拒绝；删除、秘密读写、安装开关、配置修复提交、Provider 初始化、重启和电源操作保持在 CLI/Web。`get_history` 返回记录和每次尝试最多 200 行日志尾部；完整日志及截图由 CLI/Web 获取。脚本更新省略 `pluginInputs` 时保留原输入，显式空对象表示清空；Provider 配置身份由初始化事务维护。

## 行为护栏

- 三端复用 Application Command、核心服务和共享投影；矩阵记录入口差异，不复制领域规则。
- 运行计划解释复用真实执行的冻结计划构建器与 `ExecutionStateStore` 准入评估，不创建运行登记、资源租约、历史记录、配置交换或系统操作。
- 系统诊断检查本地运行状态，不自动修复、不改变用户数据、不默认发起网络请求、不读取设置密钥；安装目录写权限检查使用受控临时探针并在检查后清理。支持包限制为 8 MiB，最近日志尾部限制为 2 MiB，并在写入 ZIP 前执行敏感信息 canary。
- Host 只向本实例受管 staging 生成诊断 ZIP。导出响应提供短期 artifact ID、长度与 SHA256；`GET /api/diagnostics/artifacts/<id>` 按服务端登记的文件提供内容。CLI 下载并核验这个 artifact，再用 CreateNew 写入调用者明确指定的本机文件，已有目标、链接或缺失父目录均拒绝。Web 的任意 `outputPath` 或 `isCli` 不提供调用者文件写权限。
- 带 Origin 的 HTTP 管理请求要求相同的 HTTP scheme、主机与实际端口。NAT/外部域名入口还要求真实非回环对端、当前 remote bound 和通过 Bearer 认证；伪转发头不改变连接来源。回环地址及本机 LAN 地址继续遵守本机来源校验，跨端口例外仅用于只读 status 探测。
- `run_queue` 提交执行前经 `McpPolicy.ValidateQueueExecution` 复核队列快照的完成操作，任何非 `none` 动作返回 `dangerous_completion_action`。
- `get_settings` 对 Webhook、SMTP 和访问令牌只返回空值或 `enc:***` 占位符。
- 通知截图开关按渠道全局生效；脚本通知可携带所选运行截图，队列汇总通知不附图；Webhook 协议能力存在差异时由发送器记录兼容性警告。
- MCP 网络边界独立于 Web 远程访问设置：仅 loopback、Host/Origin 校验、请求体上限 2 MiB。
- 用户绑定的通用设置包含 `RunDays` 与 `MaxSuccessfulRunsPerDay`；后者使用 `-1` 表示不限制，达到正数上限后生成 `skipped` 历史记录。
- 历史页面按日期、用户、运行记录分层查询；`/api/history/users` 与带 `userKey` 的记录查询都在宿主侧完成过滤，避免把整日记录一次性返回前端。
- 历史查询支持 `success`、`failed`、`partial`、`cancelled`、`skipped` 状态筛选；`/api/history/summary` 返回总数、状态计数、累计/平均耗时、成功率和按日趋势。`durationMs` 由开始/结束时间在返回投影中计算，运行历史 JSON 不增加该字段。
- 本机路径选择器只接受回环请求，由 Windows 原生选择器返回路径；返回值写入可继续手动编辑的文本框，不提供远程文件系统浏览能力。
- 已绑定脚本实例的配置编辑预检查与操作请求均只接受回环请求，远程请求直接返回 `403 local_only`。
- 配置编辑由 Host 会话持有，网页关闭或路由切换不提交或取消事务；启动目标软件时不调整目标或浏览器窗口的前后顺序。
- 配置编辑启动会按专项 profile 处理 fresh/reuse 目标、文件/目录候选隔离和 `configEditor` 准备脚本；准备失败返回 `execution_failed` 并保留恢复标记供自愈。
- 配置编辑保存、取消与崩溃恢复共享 `.session`、`edit-isolation`、`original-extra` 和附加快照事务，收尾完成后清理空闲 `work/`。
- 插件批量更新只选择官方 catalog 中已安装、与当前宿主兼容、存在新版本且没有待处理事务的插件，按顺序登记更新；更新必须通过安装归属、通道与实际目录身份校验，未验证的本地插件不得接管。单项失败会进入汇总结果并继续处理后续插件，完成后由用户按页面提示重启宿主。
- 插件启用、禁用与商店单插件安装、更新、卸载响应都返回 `restartRequired: true`；批量更新在至少一项登记成功时返回同一字段，页面据此显示重启入口。
- 插件管理与详情投影包含 `minHostVersion`。运行时发现宿主版本过低时保留插件元数据并返回 `state=Incompatible`、`runtimeErrorCode=plugin_incompatible_host`；插件商店将 `host_version_too_low`、`plugin_api_incompatible` 和 `invalid_version` 分别投影为 `compatibilityCode`，只有已安装插件存在更高且宿主不兼容的新版本时使用 `status=update-requires-host-upgrade`，其余兼容性失败使用 `status=incompatible` 并禁止安装或更新。
- `lightweightMode` 在两种模式的设置读取与状态中保持原字段，设置写入接口拒绝修改；托盘提供切换入口；停机时也可修改配置文件后重启。轻量模式提供 Web UI、后台服务和托盘，不启用 Electron；`openDesktopOnStartup` 控制轻量模式冷启动打开浏览器及普通模式登录自启动显示桌面。显式后台和轻量模式重启不自动打开，主动激活与现有 `web` 命令仍可打开网页。
- `/api/status` 返回进程实例标识 `instanceId`、本次重启交接标识 `restartHandoffId` 与实际监听端口 `actualPort`；`POST /api/settings/restart` 返回候选端口 `newPort`、本次交接标识 `handoffId` 与旧实例 `instanceId`。管理页面据此确认新实例已经接管再跳转；只读的 `GET /api/status` 放行同主机的其他端口并返回可读 CORS 应答，其余接口保持同源要求。
- `GET /api/settings` 的 `status.remote` 区分已保存开关 `allowed` 与当前监听策略 `bound`，并返回 `internalAddress`、`publicAddress` 和实际 `port`；每类地址至多一个，未检测到时为 null。虚拟网卡和保留 IPv4 不作为公网入口，NAT 出口不从本机网卡推断。令牌始终通过 `secretKey/accessToken` 密钥事务保存，响应仅返回是否设置及掩码。
- 外观设置中的二级表面透明度开关仅影响 Modal、选择器、时间/日期弹层和同类浮层；关闭后这些表面使用不透明背景，一级页面表面保持原有外观设置。

## 取消响应

`POST /api/cancel` 返回兼容的 `accepted`、`already_requested` 或 `already_finished`。标准 `Server-Timing` 响应头记录 `request`（transport 接收至 handler）、`cancel`（解析并接受取消）和 `received`（服务接收墙钟，诊断描述）。运行快照中的取消阶段计时采用单调时钟；响应头和展示墙钟不能替代进程停止证明。

## 维护规则

新增能力时先实现 Web/CLI 入口与 Application Command，再评估 Agent 场景是否需要 MCP 工具；保持核心子集克制，避免工具面向全量 API 膨胀。本表为信息性记录，不设强制校验测试。

专项任务查询、运行快照与历史深链接见[专项任务控制面](plugin-api/task-protocol.md#控制面)。

## 脚本类型图标

`GET /api/scripts/type-icons/{typeId}` 返回图像二进制，沿用 Control API 认证。`general` 返回宿主内嵌项目 ICO；其他类型由已启用且兼容的专项插件或执行 provider 的 `scriptTypeIcon` 声明来源，固定上游完整 SHA 与图标 SHA-256。已知类型未声明图标时使用项目图标；未知或未启用类型返回 404，客户端不能提供下载 URL。

宿主加载插件后以最多三个并行下载在后台预取类型图标，不等待弹窗打开，不阻塞启动；停止宿主时取消预取并等待清理。Scripts 服务使用宿主代理下载，仅允许完整 SHA 固定的 HTTPS raw 来源并拒绝重定向，大小上限为 2 MiB、超时 10 秒。经摘要验证的图像缓存于 `.nxp/cache/script-type-icons/`，缓存命中无需联网；并发下载共享任务，单个客户端取消不影响其他请求。网络或校验失败使用项目图标，后续弹窗请求可以重试；缓存写入失败仍返回已经验证的图像。上游图标字节不包含在源码和发行包中。

## 今日概览布局

`GET /api/dashboard/cards` 返回目录；`GET /api/dashboard/layout` 返回 schemaVersion、catalogRevision、cards、layout、visibleCardIds。

布局 GET 的强 ETag 是完整响应实际 compact UTF-8 字节的 SHA256，带 `Content-Language`、`Vary: Accept-Language`、`Cache-Control: no-store`。按 Accept-Language 使用宿主语言协商；不提供时沿用 X-Nexus-Locale。catalogRevision 包含 Host 实例与登记代次、完整元数据及词典，切换投影语言不改变目录代次，Host 重启会改变。

PUT `/api/dashboard/layout` 必须回传同语言 GET 的强 If-Match，正文只含 schemaVersion、catalogRevision 和有序唯一 visibleCardIds，空列表合法。缺前置条件返回 428，弱/通配 ETag 返回 400；先检查目录代次（409），再检查完整表示（412）。未知、不可用、固定区块及重复 ID 返回 400；请求体上限 256 KiB、条目超限返回 413。将选中可用记录按请求顺序置为显示，再按旧相对顺序放入未选隐藏记录，仅替换旧 entries 中可用的位置。不可用记录完全保留。写前再次核验来源代次与文件字节。成功 PUT 返回提交快照，不返回 ETag/Last-Modified；客户端随后 GET 取得下次编辑基线。

SSE 的 dashboard.layout.changed 和 dashboard.catalog.changed 用于失效通知；状态所有权及恢复行为见[卡片架构](../architecture/dashboard.md)。

## 专项任务结果

专项任务新报告采用 schema 3 / daily-flow-v2，运行与历史接口保留 verification 的识别覆盖、未知项及实际 worker 完成依据。已知任务无失败且实际执行正常结束、恢复成功时，含未知项的主状态仍为 success；历史筛选、成功配额和通知沿用该真实主状态。未知项只读显示为未核验，旧 schema 2 不补造覆盖信息。

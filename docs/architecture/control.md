# 控制面架构

## 统一控制面与 CLI

常驻服务是运行时数据、执行状态和持久化写入的拥有者。Web 请求、正式 CLI 和 `manage` 交互菜单都通过同一组 `/api/*` 控制端点进入服务，服务内部继续复用 `DispatchCenter`、`ConfigEditCommands`、执行准入、配置交换和各资源的持久化事务。CLI 进程只承担参数解析、目标解析、请求发送和结果格式化，不持有第二套配置写入路径。

正式 CLI 的协议边界如下：

- 命令采用 `status`、`script`、`user`、`queue`、`run`、`history`、`settings`、`plugin`、`update` 和 `system-action` 等 noun/subcommand；运行控制统一使用 `run script`、`run queue` 和 `run cancel`。
- 复杂 payload 统一使用 `--file <json 文件>` 或 `--file -`（标准输入），避免把领域对象拆成大量命令行开关。
- `user global-settings` 管理用户的 General、Notification、Advanced BindingOverrides；`plugin store` 管理官方插件仓库事务；`plugin user-settings` 管理通用插件用户设置贡献，命令均通过 Control API 复用宿主服务。
- `--json` 输出稳定 envelope；标准输出只承载协议数据，连接诊断和运行进度转到标准错误。退出码按参数/校验、找不到或歧义、资源冲突、服务不可用、禁止、执行失败、取消/超时和内部错误分层。
- 目标解析先按大小写不敏感的完整 ID 匹配，再按大小写不敏感的唯一名称匹配；名称匹配不唯一时保留候选 ID 并返回 `ambiguous_target`，禁止静默选择首项。
- Control API 服务发现只接受 `/api/status` 返回的 `service=NexusPipeline`、`controlApiVersion=1` 和 `1024–65535` 范围内的 `actualPort`；状态、CRUD 与运行轮询使用短请求超时，通知测试和更新检查使用长同步超时。
- `doctor` 与 `doctor export` 通过同一诊断服务检查宿主权限、监听器、更新/配置恢复现场、执行/调度状态、插件和外部依赖；安装目录写权限项使用受控临时探针并在检查后清理，支持包只写入脱敏后的诊断事实、插件/运行时状态与最近日志尾部。
- `run script/queue --dry-run` 以及对应的 Control API 只构建当前冻结计划并调用执行状态的共享准入评估，返回用户状态、资源、任务、完成操作和稳定失败码，不登记运行或触发副作用。

轻量模式仍启动 Control API，监听地址固定为 `127.0.0.1`，仅关闭静态 Web UI 与浏览器自动打开。这样命令行自动拉起服务、脚本化调用和本机管理菜单在轻量模式下仍共享同一运行时状态。



## MCP Agent 控制面

宿主在同一个 `nexus-pipeline.exe` 进程内嵌 MCP Server。现有 `HttpListener` 继续承载 Web UI 与 Control API，MCP 使用官方 `ModelContextProtocol.AspNetCore` 的 Streamable HTTP transport，端点为：

```text
http://127.0.0.1:<McpPort>/mcp
```

MCP 的启动条件和运行语义如下：

- `McpEnabled` 默认关闭；关闭时进程不创建 MCP Kestrel listener。
- `McpPort` 默认 `58732`，有效范围为 `1024–65535`。端口是 Agent 配置的一部分，发生占用时记录错误并保持 MCP 不可用，Control API 继续工作，端口不会自动漂移。
- `LightweightMode` 保留 Control API；MCP 是否启动仍由 `McpEnabled` 独立决定，Web UI 继续关闭。
- 宿主停止时按 MCP → Scheduler/恢复任务 → Web → 插件的顺序执行清理；MCP 停止异常只记录诊断，不阻断其余清理步骤。

MCP 只保留面向 Agent 的核心子集（22 个工具）：只读工具覆盖状态、诊断、运行计划解释、脚本、用户、队列、运行、历史、插件、脱敏设置和更新状态；常规变更工具覆盖运行/取消、脚本与用户的创建、绑定管理和取消系统操作。删除类、密钥、插件安装/开关、商店、服务重启和更新应用等高风险或低频运维操作不进入 MCP 工具面，由本地 CLI 与管理页面承担。工具元数据和调用前的应用策略同时参与风险控制，队列完成后的休眠、重启、关机、退出等系统操作保持由本地管理路径配置。

MCP 控制面采用以下行为契约：

- NexusPipeline 信任同一台计算机上的本机进程；loopback、Host、Origin 与请求体限制用于网络和网页边界，MCP 不增加本机进程认证令牌或 SID 鉴权。
- `run_queue` 在提交执行前复核队列快照的 `CompletionAction`；任何非 `none` 动作都返回稳定的 `dangerous_completion_action`，既有 Web/本地队列仍可按本地设置执行完成操作。
- 服务重启统一经过 `HostRestartCoordinator`（入口为 Web 管理页与 CLI）：接受请求时由 `ExecutionStateStore` 原子取得 `HostMaintenanceLease`，租约立即冻结新的运行、配置编辑和宿主配置写入；子进程拉起失败释放租约，子进程已拉起后租约持续到旧进程退出。
- `/api/settings/test` 的通知失败使用非 2xx 与 `notification_test_failed`；CLI 根据服务端错误码生成失败 envelope 和非零退出码。
- `/api/status` 是 Control API 的身份握手，包含 `service=NexusPipeline` 与 `controlApiVersion=1`，CLI 不接受缺少身份或端口越界的其他 HTTP 2xx 响应。
- `list_plugins`、`/api/plugins` 和 `/api/status` 使用共享 `PluginManagementView`，统一表达 schema 2 的 `artifactName`、展示元数据、商店归属和 pending 事务；插件详情通过专用 detail API 提供 README 与完整更新记录。
- 插件用户全局设置与用户级插件设置的读取和写入由 Web/CLI 提供；MCP 不暴露这些低频细粒度入口。
- 执行预览端点按插件声明的 `execution-preview-client`、启用状态和前端存在进行准入，宿主继续负责当前运行目标与截图采集。
- `get_diagnostics` 返回 `DiagnosticsService` 的稳定检查投影；安装目录写权限项使用受控临时探针并在检查后清理，诊断导出只允许 loopback 请求，支持包使用原子输出、大小上限和敏感值 canary。
- `explain_script_run` / `explain_queue_run` 与 CLI dry-run 使用同一 `ExecutionPlanBuilder` 和 `ExecutionStateStore.EvaluateCandidate`，解释过程不写运行历史、不创建 lease、不执行配置交换或完成操作。

MCP 适配层只接收类型化参数，经过 `McpToolContext` 解析稳定 ID/唯一名称，再进入 Application Commands 和已有核心服务。它不复用 Web handler 或 CLI 路由，也不提供万能 CLI/API/shell 工具。运行类调用立即返回 `runId`，Agent 通过 `get_run` 轮询活动或最近完成的运行；业务错误保留在结构化工具结果内：

```json
{
  "ok": false,
  "errorCode": "resource_busy",
  "errorMessage": "脚本正在运行，无法修改",
  "candidates": [],
  "data": null
}
```

MCP 的网络边界独立于 Web 的远程访问设置：Kestrel 只监听 loopback，Host 仅允许 `127.0.0.1`、`localhost` 和 `::1`，Origin 必须为相同 loopback 主机与 MCP 端口，请求体上限为 2 MiB。`get_settings` 对 Webhook、SMTP 和访问令牌只返回空值或 `enc:***` 占位符；secret mutation 只接受显式高风险工具，值经过既有 DPAPI 存储且不会进入返回值或 `Audit.Mcp` 日志。



## 控制面边界

常驻服务持有 `RuntimeContext`、执行状态和持久化写入。公共初始化只读取约束和设置快照；服务与 Web-only 模式取得单实例互斥体后，统一进入 `HostedRuntimeInitializer`，由其完成实体加载、修复和恢复。Web 与 CLI 都是协议适配层：

```text
Web 请求      ─┐
CLI / manage ─┼→ Control API → ApiXxxHandler → Application Command/核心服务 → DataStore/Logger
Scheduler    ─┘                         └→ ExecutionStateStore/ExecutionRunner
```

`manage` 的菜单类通过正式 CLI/Control API 查询和变更，不直接读取或修改运行时实体状态，也不直接调用 `DataStore` 或 `ConfigStore`。Control API 的查询端点在 Normal 与 Lightweight 两种服务模式均可用；Lightweight 只移除静态资源服务。

MCP 位于同一主进程的协议适配层。`McpHost` 只在 `McpEnabled` 时创建 Kestrel listener，使用 `McpPort` 绑定 loopback；工具类依赖 `McpToolContext`，再调用 Application Commands/核心服务。MCP 不依赖 Web handler、CLI 路由或前端投影；写入对象还会经过 `McpPolicy` 行为校验。

重启请求从 Web handler 或 CLI 进入 `Bootstrap.RequestRestart`，再由 `HostRestartCoordinator` 取得 `DispatchCenter` 提供的 `HostMaintenanceLease`。租约与 `ExecutionStateStore` 的执行、编辑、宿主配置变更协调锁共享同一准入域；CLI 通过 `/api/settings/restart` 复用该入口。`run_queue` 额外使用 `McpPolicy.ValidateQueueExecution` 复核已有队列的完成操作，因此队列创建来源不会改变 MCP 执行护栏。

重启恢复使用实例身份协议：`HostInstance` 为每个进程生成一次 `instanceId`，接受重启的旧实例生成 `handoffId` 并随 `nexus-pipeline.exe restart --handoff <id>` 交给子进程，子进程在 `StartupPipeline.RunRestart` 中接管。`GET /api/status` 暴露 `instanceId`、`restartHandoffId` 与 `actualPort`，`POST /api/settings/restart` 返回 `newPort`、`handoffId` 与旧实例 `instanceId`。控制面前端按配置端口与宿主顺延端口逐个读取 `/api/status`，只接受携带本次 `handoffId` 且 `instanceId` 不同于旧实例的应答，再跳转到 `actualPort`；无关 HTTP 服务、仍在应答的旧实例与超时都不会触发跳转。只读的 `GET /api/status` 因此放行同主机的其他端口并返回可读 CORS 应答，其余接口保持同源要求。

运行观察的 SSE 连接沿用同一 Origin 与 Bearer 认证边界，浏览器因远程 Bearer 头限制而通过 `fetch` + `ReadableStream` 消费事件。服务端不接受查询字符串令牌、不实现 `Last-Event-ID` 重放；`stream.ready` 与 `stream.missed` 只触发当前页面重新读取 `/api/status`，网络错误按 500ms、1s、2s、5s、10s 的上限退避重连，4xx 认证/请求错误交给页面重新认证或维持轮询。运行日志在内存中最多保留每个运行 500 行，旧行淘汰时以截断标记提示页面。

# 控制面能力现状（Control Plane）

本文件记录可管理能力在 Web、CLI 和 MCP 三个控制面的入口现状，供开发与排障参考。MCP 只保留面向 Agent 的核心子集；删除、密钥、插件安装/开关、商店、服务重启、更新应用和遗留数据清理等高风险或低频运维操作由本地 CLI 与管理页面承担。

## 能力现状

| Capability | Web | CLI | MCP |
|---|---|---|---|
| 脚本读取 | `GET /api/scripts` | `script list/get` | `list_scripts` |
| 脚本写入 | 脚本 CRUD API | `script create/update/delete` | `create_script` / `update_script`（删除走 CLI） |
| 用户读取 | `GET /api/users` | `user list/get` | `list_users` |
| 用户写入 | 用户 CRUD API | `user create/update/delete` | `create_user`（改名/删除走 CLI） |
| 用户绑定 | `/api/users/{id}/bindings` | `user bindings ...` | `add_binding` / `update_binding`（删除走 CLI） |
| 用户全局设置 | `/api/users/{id}/global-settings` | `user global-settings get/update` | 由 CLI/Web 承担 |
| 队列读取 | `GET /api/queues` | `queue list/get` | `list_queues` |
| 队列写入 | 队列 CRUD API | `queue create/update/delete` | 由 CLI 承担 |
| 运行 | 调度中心/Control API | `run script/queue` | `run_script` / `run_queue` |
| 取消 | `/api/cancel`、系统操作取消 | `cancel` | `cancel_run` / `cancel_system_action` |
| 运行观察 | `/api/status`、运行详情 | `status`、run 轮询 | `get_status` / `list_runs` / `get_run` |
| 历史 | `/api/history/dates` → `/api/history/users?date=...` → `/api/history?date=...&userKey=...`；详情 `/api/history/detail` | `history ...` | `list_history` |
| 本机路径选择 | `POST /api/native-dialog`（仅回环请求） | — | — |
| 插件读取 | `GET /api/plugins` | `plugin list/get` | `list_plugins` |
| 插件商店/安装/开关 | 插件页 + Control API | `plugin install/update/uninstall/enable/disable` | 由 CLI/Web 承担 |
| 插件用户设置 | 贡献接口 | `plugin user-settings ...` | 由 CLI/Web 承担 |
| 设置读取 | 设置 API | `settings status` | `get_settings`（密钥脱敏） |
| 设置写入 | 设置 API | `settings update` | `安全白名单外的写入走 CLI/Web` |
| 更新 | 更新 API | `update check/download/apply` | `get_update_status` |
| 遗留数据清理 | 维护 API | `maintenance prune` | 由 CLI 承担 |

## 行为护栏

- 三端复用 Application Command、核心服务和共享投影；矩阵记录入口差异，不复制领域规则。
- `run_queue` 提交执行前经 `McpPolicy.ValidateQueueExecution` 复核队列快照的完成操作，任何非 `none` 动作返回 `dangerous_completion_action`。
- `get_settings` 对 Webhook、SMTP 和访问令牌只返回空值或 `enc:***` 占位符。
- MCP 网络边界独立于 Web 远程访问设置：仅 loopback、Host/Origin 校验、请求体上限 2 MiB。
- 用户绑定的通用设置包含 `RunDays` 与 `MaxSuccessfulRunsPerDay`；后者使用 `-1` 表示不限制，达到正数上限后生成 `skipped` 历史记录。
- 历史页面按日期、用户、运行记录分层查询；`/api/history/users` 与带 `userKey` 的记录查询都在宿主侧完成过滤，避免把整日记录一次性返回前端。
- 本机路径选择器只接受回环请求，由 Windows 原生选择器返回路径；返回值写入可继续手动编辑的文本框，不提供远程文件系统浏览能力。
- 外观设置中的二级表面透明度开关仅影响 Modal、选择器、时间/日期弹层和同类浮层；关闭后这些表面使用不透明背景，一级页面表面保持原有外观设置。

## 维护规则

新增能力时先实现 Web/CLI 入口与 Application Command，再评估 Agent 场景是否需要 MCP 工具；保持核心子集克制，避免工具面向全量 API 膨胀。本表为信息性记录，不设强制校验测试。

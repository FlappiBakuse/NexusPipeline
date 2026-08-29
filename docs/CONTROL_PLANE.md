# Control Surface Capability Matrix

本文件是可管理能力在 Web、CLI 和 MCP 之间的对齐清单。新增或修改领域能力、Application Command、`[ApiRoute]` 或 MCP 工具时，先更新对应行，再补充最低充分层级的测试。

## 状态定义

- `supported`：该控制面提供稳定入口，并复用宿主的应用命令、核心服务或共享投影。
- `security-restricted`：入口存在，但需要显式的高风险策略或敏感操作授权。
- `intentionally-ui-only`：能力属于前端表现或交互呈现，控制面保持明确的适用范围。
- `not-applicable`：该控制面与能力的语义无关，原因写在 Exception 列。

核心能力的每个控制面都必须写明状态。`Exception` 用于记录风险门禁、范围边界或 `not-applicable` 原因；空白单元格表示矩阵维护错误。

## 核心能力

| Capability | Web | CLI | MCP | Exception |
|---|---|---|---|---|
| `scripts.read` | `supported`（`GET /api/scripts`） | `supported`（`script list/get`） | `supported`（`list_scripts/get_script`） | — |
| `scripts.write` | `supported`（脚本 CRUD） | `supported`（`script create/update/delete`） | `security-restricted`（`create/update` 常规变更，`delete` 为 `risk=destructive`） | MCP 删除工具随 `McpAllowDestructiveTools` 条件注册 |
| `users.read` | `supported`（`GET /api/users`） | `supported`（`user list/get`） | `supported`（`list_users/get_user`） | — |
| `users.write` | `supported`（用户 CRUD） | `supported`（`user create/update/delete`） | `security-restricted`（删除为 `risk=destructive`） | MCP 删除工具随 `McpAllowDestructiveTools` 条件注册 |
| `users.global-settings` | `supported`（`/api/users/{id}/global-settings`） | `supported`（`user global-settings get/update`） | `supported`（`get/update_user_global_settings`） | 三个 BindingOverrides 类别由同一应用命令归一化 |
| `queues.read` | `supported`（`GET /api/queues`） | `supported`（`queue list/get`） | `supported`（`list_queues/get_queue`） | — |
| `queues.write` | `supported`（队列 CRUD） | `supported`（`queue create/update/delete`） | `security-restricted`（删除为 `risk=destructive`） | MCP 删除工具随 `McpAllowDestructiveTools` 条件注册 |
| `execution.run` | `supported`（调度中心/Control API） | `supported`（`run script/queue`） | `supported`（`run_script/run_queue`） | 队列完成后的系统操作由 MCP 策略复核 |
| `execution.cancel` | `supported`（`/api/cancel`） | `supported`（`cancel`） | `supported`（`cancel_run`） | — |
| `plugins.read` | `supported`（`GET /api/plugins`） | `supported`（`plugin list/get`） | `supported`（`list_plugins`） | 三个适配器共用 `PluginManagementView` |
| `plugins.enable-disable` | `supported`（`POST /api/plugins/{name}/enable` 或 `/disable`） | `supported`（`plugin enable/disable`） | `security-restricted`（`risk=destructive`） | MCP 插件开关属于 `risk=destructive`，随 `McpAllowDestructiveTools` 条件注册 |
| `plugins.store.read` | `supported`（`GET /api/plugins/store`） | `supported`（`plugin store list`） | `supported`（`list_plugin_store`） | 共享 catalog、安装状态、替换和 pending 投影 |
| `plugins.store.refresh` | `supported`（`POST /api/plugins/store/refresh`） | `supported`（`plugin store refresh`） | `supported`（`refresh_plugin_store`） | 幂等刷新缓存，不修改插件文件 |
| `plugins.store.install` | `supported`（Control API） | `supported`（`plugin install/store install`） | `security-restricted`（`risk=destructive`） | `risk=destructive` 代码供应链修改；MCP 仅在 `McpAllowDestructiveTools` 开启后提供 |
| `plugins.store.update` | `supported`（Control API） | `supported`（`plugin update/store update`） | `security-restricted`（`risk=destructive`） | `risk=destructive` 代码供应链修改；以 pending 事务跨重启生效 |
| `plugins.store.uninstall` | `supported`（Control API） | `supported`（`plugin uninstall/store uninstall`） | `security-restricted`（`risk=destructive`） | `risk=destructive` 代码供应链修改；卸载事务在重启时完成 |
| `plugin-user-settings.read` | `supported`（用户全局贡献接口） | `supported`（`plugin user-settings list/get`） | `supported`（`list/get_plugin_user_settings`） | `secret` 字段只返回 `configured` 状态 |
| `plugin-user-settings.write` | `supported`（贡献 PUT） | `supported`（`plugin user-settings update`） | `supported`（普通字段） | `secret` 的 `set/clear` 另受 `security-restricted` 规则约束 |
| `plugin-user-settings.secret-write` | `supported`（贡献 PUT） | `supported`（通用 JSON payload） | `security-restricted`（`risk=sensitive`） | `risk=sensitive`；MCP `secret set/clear` 需要 `McpAllowDestructiveTools`；明文不进入输出或日志 |
| `settings.read` | `supported`（设置 API） | `supported`（`settings status`） | `supported`（`get_settings`） | 密钥和令牌只返回脱敏占位符 |
| `settings.write` | `supported`（设置 API） | `supported`（`settings update`） | `supported`（安全白名单） | 高风险密钥写入使用独立 `risk=destructive` 工具 |
| `update.check` | `supported`（更新 API） | `supported`（`update check/status`） | `supported`（`check_update/get_update_status`） | 外部源失败保留稳定错误分类 |
| `update.apply` | `supported`（更新 API） | `supported`（`update apply`） | `security-restricted`（`risk=destructive`） | `risk=destructive` 文件交换、重启和回滚属于高风险事务 |
| `maintenance.prune` | `supported`（维护 API） | `supported`（`maintenance prune`） | `security-restricted`（`risk=destructive`） | `risk=destructive`；仅清理已确认的遗留用户数据候选 |

## 明确的 UI-only 能力

这些能力有意保持在前端表现层，控制面状态通过 `not-applicable` 或 `intentionally-ui-only` 明确记录。

| Capability | Web | CLI | MCP | Exception |
|---|---|---|---|---|
| `appearance.wallpaper-presentation` | `intentionally-ui-only` | `not-applicable` | `not-applicable` | 壁纸选择、轮换和展示效果属于前端表现 |
| `frontend.slot-rendering` | `intentionally-ui-only` | `not-applicable` | `not-applicable` | slot、nav、route 和 renderer 属于 Frontend API contract |
| `execution-preview.image-rendering` | `intentionally-ui-only` | `not-applicable` | `not-applicable` | 图片显示属于插件前端；宿主采集授权由 `execution-preview-client` capability 控制 |

## 维护规则

1. 入口新增时先判断它对应的稳定 capability ID，再更新 Web、CLI、MCP 三列状态。
2. `security-restricted` 必须写明风险类型、策略开关或敏感字段边界，并有对应的低层策略测试。
3. `intentionally-ui-only` 与 `not-applicable` 必须写出 presentation-only 或语义无关原因。
4. 适配器复用 Application Command、核心服务和共享 projection；矩阵不以复制领域规则的方式实现。
5. 治理测试检查表格完整性与高风险行的分类；代码审查判断新能力是否需要新增行。

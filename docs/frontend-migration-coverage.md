# 前端迁移覆盖矩阵（v0.15.4 → 当前）

本文件是 v0.15.7「全面迁移」声明的硬门禁：只有矩阵无未解释项时，才能声明功能与 UI 全面迁移。
`Status` 只允许 `migrated`、`v0.15.7-fix`、`v0.15.8-platform`、`intentionally-removed`、`not-applicable`。

- `v0.15.4 reference`：旧实现位置，仅作迁移完整性对照，不是要恢复的架构目标。
- `Current implementation`：当前 Vue feature / 平台模块。
- `v0.15.8-platform`：v0.15.7 期间仍由 `wwwroot` 迁移期平台模块承载，v0.15.8 完成 TS 化。

## Dashboard

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Dashboard | 状态概览与活动任务 | `views/dashboard.js` | `features/dashboard/DashboardPage.vue` | migrated | `GET /api/status` 轮询 | 空态/错误态 | page-kicker | 360/768/1280 | `dashboard.cards`、`dashboard.after-running` | vitest + codex ui |
| Dashboard | 系统操作卡片 | `views/dashboard.js` | `features/dashboard/SystemActionCard.vue` | migrated | `POST /api/system-action/*` | toast 错误 | data-help | 360/768/1280 | — | codex ui |

## Users

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Users | 用户创建/编辑/删除 | `views/users/index.js`、`user-management.js` | `features/users/UsersPage.vue` | migrated | `POST/PUT/DELETE /api/users` | 校验 toast、确认名删除 | 用户名大小写提示 | 360/768/1280 | — | scripts-users smoke |
| Users | 全局管理 | `views/users/global-management.js` | `features/users/UsersPage.vue`（全局管理弹窗） | migrated | `GET/PUT /api/users/{id}/global-settings` | toast 错误 | 各 section help | 360/768/1280 | `users.global.sections` | codex ui |
| Users | binding 增删改 | `views/users/shared.js`、`user-management.js` | `features/users/UsersPage.vue`（绑定区） | migrated | `POST/PUT/DELETE /api/users/{id}/bindings` | 空态/错误态 | override help | 360/768/1280 | `users.binding.sections` | scripts-users smoke |
| Users | binding locks | `views/users/user-management.js` | `features/users/UsersPage.vue` `bindingValue`/`setBindingValue` | migrated | `binding.effective` 投影 | — | `users.binding.global_override.help` | 360/768/1280 | — | codex ui |
| Users | plugin contributions | `views/users/global-management.js` | `features/users/UsersPage.vue` 贡献字段渲染 | migrated | `GET/PUT /api/plugin-contributions/user-global/...` | 必填校验 toast | field.description | 360/768/1280 | `users.global.sections` | codex ui |
| Users | 用户列表徽章 | `views/users/shared.js` | `features/users/UsersPage.vue` + badges 投影 | migrated | `GET /api/plugin-contributions/user-list-badges` | 异常隔离 | badge title | 360/768/1280 | — | scripts-users smoke |
| Users | 用户排序 | `views/users/index.js` | `features/users/UsersPage.vue` `reorderUsers` | migrated | `PUT /api/users/order` | 失败回滚重载 | drag_to_reorder | 360/768/1280 | — | codex ui |
| Users | 头像上传/移除 | `views/users/shared.js` | `features/users/UsersPage.vue` | migrated | `POST/DELETE /api/users/{id}/avatar` | 类型/大小 toast | — | 360/768/1280 | — | — |
| Users | 倒计时与到期刷新 | `views/users/shared.js` | `features/users/UsersPage.vue` `refreshCountdowns` + `composables/useCountdownRefresh.ts` | migrated | `nextRunAt` 本地计算；到期后延迟单次 `load()` 拉取新状态 | — | — | 360/768/1280 | — | vitest（`useCountdownRefresh.test.ts`） |
| Users | config edit：choose | `views/users/config-edit.js` | `features/users/UsersPage.vue`（chooser 弹窗） | migrated | `GET .../edit-config` `hasSnapshot` | toast 错误 | edit_first copy | 360/768/1280 | — | codex ui |
| Users | config edit：candidate | `views/users/config-edit.js` | `features/users/UsersPage.vue` `configCandidates` | migrated | `config_input_mismatch` 候选 | candidates_help | 360/768/1280 | — | web logic（`buildConfigEditRequest`） |
| Users | config edit：edit/done/cancel | `views/users/config-edit.js` | `features/users/UsersPage.vue` `finishConfigEdit` | migrated | `POST .../edit-config {action}` | validation toasts | edit_progress copy | 360/768/1280 | — | codex ui |
| Users | config edit：会话恢复 | `views/users/shared.js` `restoreEditSessionCard` | `features/users/UsersPage.vue` + `composables/useConfigEditFlow.ts` + `utils/editSession.ts` | migrated | `GET /api/scripts/edit-sessions`；恢复只还原锁定 UI，不重发 `action:start` | 恢复失败静默 | — | 360/768/1280 | — | vitest（`useConfigEditFlow.test.ts` + `editSession.test.ts`） |

## Scripts

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Scripts | CRUD | `views/scripts.js` | `features/scripts/ScriptsPage.vue` + `ScriptCard.vue` | migrated | `GET/POST/PUT/DELETE /api/scripts` | toast、必填校验 | 各字段 data-help | 360/768/1280 | `scripts.list.badges` | scripts-users smoke |
| Scripts | 通用/专项选择 | `views/scripts.js` | `ScriptsPage.vue` chooser | migrated | 本地 | 空态 | config_auto copy | 360/768/1280 | — | codex ui |
| Scripts | 专项 root probe | `views/scripts.js` `probeSpecialRoot` | `ScriptEditorModal.vue`（手工输入与原生目录选择统一） + `utils/scriptProbe.ts` | migrated | `POST /api/scripts/probe`；同签名去重、过期响应抑制 | `scripts.plugin.config_derive_failed` toast | — | — | — | vitest（`ScriptEditorModal.test.ts` + `scriptProbe.test.ts`） |
| Scripts | plugin inputs | `views/scripts.js` | `ScriptsPage.vue` `pluginInputs` 透传 | migrated | `pluginInputs` 落盘 | — | — | 360/768/1280 | `scripts.editor.sections` | codex ui |
| Scripts | launch mode / 游戏集成 | `views/scripts.js` | `ScriptsPage.vue` 游戏集成区 | migrated | `launchGame`/`gameMode`/`gameExe` 等 | 必填校验 | data-help | 360/768/1280 | — | codex ui |
| Scripts | judge / keyword | `views/scripts.js` | `ScriptsPage.vue` 判定区 | migrated | `judgeScript*`/`successKeywords` 等 | 判定脚本必填 toast | judge help | 360/768/1280 | — | codex ui |
| Scripts | advanced fields | `views/scripts.js` | `ScriptsPage.vue` 运行设置区 | migrated | `maxAttempts`/超时/`autoUpdateConfig` | 范围校验 | retry help | 360/768/1280 | — | codex ui |
| Scripts | 排序 | `views/scripts.js` | `ScriptsPage.vue` `reorderScripts` | migrated | `PUT /api/scripts/order` | 失败重载 | drag_to_reorder | 360/768/1280 | — | codex ui |
| Scripts | 判定脚本文件上传 | `views/scripts.js` | `ScriptsPage.vue` `uploadJudgeScript` | migrated | 本地 FileReader | 大小/读取 toast | upload help | 360/768/1280 | — | — |
| Scripts | 卡片徽章定位 | `views/scripts.js` | `ScriptCard.vue` | migrated | — | — | — | 360/768/1280 | `scripts.list.badges` | codex ui |

## Queues

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Queues | CRUD | `views/queues.js` | `features/queues/QueuesPage.vue` + `QueueCard.vue` + `QueueEditorModal.vue` | migrated | `GET/POST/PUT/DELETE /api/queues` | toast、限额校验 | 各字段 data-help | 360/768/1280 | `queues.list.badges`、`queues.editor.sections` | queues smoke |
| Queues | 定时/任务编辑 | `views/queues.js` | `QueueEditorModal.vue` | migrated | `timeSets`/`tasks` 重排 | 校验 toast | queue help | 360/768/1280 | `queues.editor.sections` | visual-contract |
| Queues | 排序与分页 | `views/queues.js` | `QueuesPage.vue` + `NxpPager.vue` | migrated | `PUT /api/queues/order` | 失败重载 | drag_to_reorder | 360/768/1280 | — | codex ui |
| Queues | 卡片徽章定位 | `views/queues.js` | `QueueCard.vue` | migrated | — | — | — | 360/768/1280 | `queues.list.badges` | visual-contract |

## Dispatch

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Dispatch | 运行中列表 | `views/dispatch.js` | `features/dispatch/DispatchPage.vue` | migrated | `GET /api/status` 1s 轮询 | 空态/状态更新失败 toast | updates_every_second | 360/768/1280 | `dispatch.running.badges`、`dispatch.running.sidecar` | queues smoke |
| Dispatch | 目标选择与执行 | `views/dispatch.js` | `DispatchPage.vue` runbar | migrated | `POST /api/dispatch/{script\|queue}` | 未选目标 toast | target_help | 360/768/1280 | `dispatch.run.sections` | queues smoke |
| Dispatch | 执行计划检查 | `views/dispatch.js` | `DispatchPage.vue` explain | migrated | `POST /api/dispatch/explain/*` | 警告/失败码投影 | plan copy | 360/768/1280 | `dispatch.run.sections` | codex ui |
| Dispatch | 系统操作倒计时 | `views/dispatch.js` | `SystemActionCard.vue` | migrated | `POST /api/system-action/*` | toast 错误 | data-help | 360/768/1280 | `dispatch.cards` | codex ui |
| Dispatch | 插件卡片 | `views/dispatch.js` | `DispatchPage.vue` slot | migrated | — | — | — | 360/768/1280 | `dispatch.cards` | codex ui |

## History

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| History | 日期/用户筛选 | `views/history.js` | `features/history/HistoryPage.vue` | migrated | `GET /api/history/dates`、`/users` | 空态 | filter help | 360/768/1280 | — | codex ui |
| History | 用户筛选返回 | `views/history.js` | `HistoryPage.vue` `goBack` | migrated | 本地 | — | — | 360/768/1280 | — | codex ui |
| History | 记录列表 | `views/history.js` | `HistoryPage.vue` | migrated | `GET /api/history?date&userKey` | 空态/错误态 | — | 360/768/1280 | `history.list.badges` | codex ui |
| History | 详情（尝试/日志/截图） | `views/history.js` | `HistoryPage.vue` detail modal | migrated | `GET /api/history/detail`、`/image` | 详情错误态 | 日志 tail help | 360/768/1280 | `history.detail.sections` | codex ui |
| History | 时间范围选择 | `views/history.js` | `HistoryPage.vue` range picker | migrated | 本地 | 日期约束 | date_help | 360/768/1280 | — | codex ui |
| History | object URL 清理 | `views/history.js` | `HistoryPage.vue` `closeDetail`/`onBeforeUnmount` | migrated | — | — | — | — | — | codex ui |

## Plugins

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Plugins | 本地列表 | `views/plugins.js` | `features/plugins/PluginsPage.vue` | migrated | `GET /api/plugins` | 加载/错误态 | reading_local copy | 360/768/1280 | — | visual-contract |
| Plugins | store 列表 | `views/plugins.js` | `PluginsPage.vue` | migrated | `GET /api/plugins/store` | stale/unavailable 态 | catalog help | 360/768/1280 | — | visual-contract |
| Plugins | store refresh | `views/plugins.js` `store-refresh` | `PluginsPage.vue` `refreshStoreAsync` + `utils/storeRefresh.ts` | migrated | `POST /api/plugins/store/refresh` → reload store | 失败 toast 不伪装成功 | — | 360/768/1280 | — | vitest（`storeRefresh.test.ts`） |
| Plugins | update all | `views/plugins.js` | `PluginsPage.vue` `updateAllStorePlugins` | migrated | `POST /api/plugins/store/update-all` | 逐项失败摘要 | update_all copy | 360/768/1280 | — | codex ui |
| Plugins | 详情（README/changelog） | `views/plugins.js` | `PluginsPage.vue` | migrated | `GET .../detail` | readme 错误码 | readme copy | 360/768/1280 | — | visual-contract |
| Plugins | 启用/禁用/安装事务 | `views/plugins.js` | `PluginsPage.vue` `runPluginAction` | migrated | `POST /api/plugins/{name}/{action}` | pending/queued toast | action notice | 360/768/1280 | — | codex ui |
| Plugins | 筛选/排序 | `views/plugins.js` | `PluginsPage.vue` + `plugin-list.js` | migrated | — | no_match 空态 | filter help | 360/768/1280 | — | visual-contract |

## Settings

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Settings | appearance | `views/settings.js` | `features/settings/SettingsPage.vue` | migrated | `PUT /api/settings` | toast 错误 | data-help | 360/768/1280 | `settings.sections` | settings-platform smoke |
| Settings | locale | `views/settings.js` | `SettingsPage.vue` | migrated | `PUT /api/settings` | — | language help | 360/768/1280 | `settings.sections` | settings-platform smoke |
| Settings | remote access | `views/settings.js` | `SettingsPage.vue` | migrated | `PUT /api/settings` + token | 令牌校验 | token help | 360/768/1280 | `settings.sections` | settings-platform smoke |
| Settings | MCP | `views/settings.js` | `SettingsPage.vue` | migrated | `PUT /api/settings` | — | mcp help | 360/768/1280 | `settings.sections` | settings-platform smoke |
| Settings | update ready 备份提醒 | `views/settings.js` `update-backup-warning` | `components/UpdateStatusCard.vue` + `utils/updateStatusView.ts` | migrated | `GET /api/update/status`；离开 ready 自动消失 | — | `settings.update.backup_help` | 360/768/1280 | — | vitest（`updateStatusView.test.ts`） |
| Settings | update action/automation | `views/settings.js` | `SettingsPage.vue` | migrated | `POST /api/update/*` | 失败 toast | update help | 360/768/1280 | `settings.sections` | codex ui |
| Settings | system actions | `views/settings.js` | `SettingsPage.vue` | migrated | `POST /api/system-action/*` | toast 错误 | data-help | 360/768/1280 | `settings.sections` | codex ui |
| Settings | diagnostics | `views/settings.js` | `SettingsPage.vue` | migrated | `GET /api/diagnostics`、导出 | 失败 toast | attention copy | 360/768/1280 | `settings.cards` | codex ui |
| Settings | notifications | `views/settings.js` | `SettingsPage.vue` | migrated | `PUT /api/settings` + secret | 失败 toast | smtp help | 360/768/1280 | `settings.sections` | settings-platform smoke |

## Shell / 平台

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Shell | nav / mobile nav | `app.js` | `app/App.vue` | migrated | hash 路由 | — | — | 360/768/1280 | `shell.nav` | app smoke |
| Shell | theme / appearance | `core/appearance.js` | `wwwroot/core/appearance.js`（迁移期平台） | v0.15.8-platform | token/壁纸/插件主题 | — | — | — | — | codex ui |
| Shell | toast / topbar | `core/ui.js` | `wwwroot/core/ui.js`（迁移期平台） | v0.15.8-platform | — | — | — | — | — | codex ui |
| Shell | auth / boot error | `core/modal.js`、`legacy/auth.ts` | `frontend/src/legacy/*` + `wwwroot/core/*` | v0.15.8-platform | `GET /api/status` 401 重认证 | boot error 态 | — | — | — | app smoke |
| Shell | i18n | `core/i18n.js` + `wwwroot/i18n` | `wwwroot/core/i18n.js` + `frontend/public/i18n`（唯一资源源） | v0.15.8-platform | `t`/`getLocale`/`applyTranslations` | — | — | — | — | docs i18n tests |
| Shell | page state / polling | `core/state.js` | `wwwroot/core/state.js`（迁移期平台） | v0.15.8-platform | route token/interval/abort | — | — | — | — | codex ui |
| Shell | tooltip | `core/tooltip.js` | `wwwroot/core/tooltip.js`（迁移期平台） | v0.15.8-platform | 延迟气泡 | — | 帮助文本契约 | — | — | visual-contract |
| Shell | API client | `core/api.js` | `wwwroot/core/api.js`（迁移期平台） | v0.15.8-platform | bearer/`X-Nexus-Locale`/blob/abort | 错误码投影 | — | — | — | api.test.mjs |
| Plugin route | mount/leave/dispose | `app.js` | `app/PluginRouteHost.ts` | migrated | route 生命周期 | invalid route fallback | — | — | — | codex ui |

## 18 个公开 slot

| # | Slot | Current implementation | Status | Test |
|---|---|---|---|---|
| 1 | `dashboard.cards` | `DashboardPage.vue` | migrated | codex ui |
| 2 | `dashboard.after-running` | `DashboardPage.vue` | migrated | codex ui |
| 3 | `users.list.badges` | `UsersPage.vue` | migrated | codex ui |
| 4 | `users.binding.sections` | `UsersPage.vue` | migrated | codex ui |
| 5 | `users.global.sections` | `UsersPage.vue` | migrated | codex ui |
| 6 | `scripts.list.badges` | `ScriptCard.vue` | migrated | codex ui |
| 7 | `scripts.editor.sections` | `ScriptsPage.vue` | migrated | codex ui |
| 8 | `queues.list.badges` | `QueueCard.vue` | migrated | visual-contract |
| 9 | `queues.editor.sections` | `QueueEditorModal.vue` | migrated | visual-contract |
| 10 | `dispatch.cards` | `DispatchPage.vue` | migrated | codex ui |
| 11 | `dispatch.running.badges` | `DispatchPage.vue` | migrated | codex ui |
| 12 | `dispatch.running.sidecar` | `DispatchPage.vue` | migrated | codex ui |
| 13 | `dispatch.run.sections` | `DispatchPage.vue` | migrated | codex ui |
| 14 | `history.list.badges` | `HistoryPage.vue` | migrated | codex ui |
| 15 | `history.detail.sections` | `HistoryPage.vue` | migrated | codex ui |
| 16 | `settings.sections` | `SettingsPage.vue` | migrated | codex ui |
| 17 | `settings.cards` | `SettingsPage.vue` | migrated | visual-contract |
| 18 | `shell.nav` | `App.vue` | migrated | app smoke |

## 公开 `nxp-*` Native Custom Elements

`frontend/src/ui/register.ts` 注册 25 个公开元素（23 个 primitive + `NxpSwitchSetting` + `NxpLoadingState`），全部 `shadowRoot: false`，
以 light DOM 共享宿主 token / layout。`NxpEntityIcon` 为宿主内部 primitive，不注册为公共元素。

| Status | Test |
|---|---|
| migrated | `ui/register.test.ts` + `codex ui` |

## 每页横向核对

| 检查项 | 结论 |
|---|---|
| loading / empty / network error 三态 | 各 feature 页面均实现（见上表 Error/Empty 列） |
| destructive confirmation | Users 删除用户（输入名确认）、Scripts/Queues 删除确认、Plugins uninstall 事务 |
| keyboard / Escape / locked modal | `NxpModal` 统一 Escape 关闭与 locked 语义；配置编辑事务为 locked modal |
| 360 / 768 / 1280 | 各页面响应式断点；`codex ui` 视觉契约固定视口 |
| help / tooltip | 字段级 `data-help`，键位见《延时气泡提示位置清单.md》 |
| page leave 清理 | 轮询（`onBeforeUnmount` + `state`）、listener、object URL（History）、plugin slot dispose |

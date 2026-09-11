# 前端迁移覆盖矩阵（v0.15.4 → v0.15.8 收口）

**更新日期**：2026-09-12

本文件是 v0.15.8「前端迁移收口」的硬门禁：只有矩阵无未解释项时，才能声明功能与 UI 全面迁移。宿主前端源码唯一入口是 `frontend/`；`frontend/src` 不引用 `wwwroot/`，旧 `@legacy` alias 已从 `frontend/vite.config.ts`、`frontend/vitest.config.ts`、`frontend/tsconfig.json` 移除。

`Status` 只允许 `migrated`、`intentionally-removed`、`not-applicable`；迁移期的阶段标记 `v0.15.7-fix` 与 `v0.15.8-platform` 已随收口作废，不再作为任何行的取值。
`migrated` 由 `frontend/src/**` 的 Vue feature、平台模块或插件桥接承载；`intentionally-removed` 表示旧实现有意删除并由现行契约替代；`not-applicable` 表示不属于 Web 前端迁移范围的表面。

- `v0.15.4 reference`：旧实现位置，仅作迁移完整性对照，不是要恢复的架构目标。
- `Current implementation`：当前 Vue feature / 平台模块 / 插件桥接模块。
- `Test`：`vitest（…）` 路径相对 `frontend/src/`；`codex ui` 指 `node tests\run.mjs codex ui` 的 UI Smoke 套件。

## Dashboard

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Dashboard | 状态概览与活动任务 | `views/dashboard.js` | `features/dashboard/DashboardPage.vue`（3s `setInterval` + `onBeforeUnmount` 清理，`platform/api.ts` 请求） | migrated | `GET /api/status` 轮询 | 空态/错误态 | page-kicker | 360/768/1280 | `dashboard.cards`、`dashboard.after-running` | codex ui |
| Dashboard | 系统操作卡片 | `views/dashboard.js` | `features/dashboard/SystemActionCard.vue` | migrated | `POST /api/system-action/*` | toast 错误 | data-help | 360/768/1280 | — | codex ui |

## Users

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Users | 用户创建/编辑/删除 | `views/users/index.js`、`user-management.js` | `features/users/UsersPage.vue` + `components/UserManagementModal.vue` + `services/usersApi.ts` | migrated | `POST/PUT/DELETE /api/users` | 校验 toast、确认名删除 | 用户名大小写提示 | 360/768/1280 | — | scripts-users smoke |
| Users | 全局管理 | `views/users/global-management.js` | `features/users/components/GlobalManagementModal.vue` + `services/usersApi.ts` | migrated | `GET/PUT /api/users/{id}/global-settings` | toast 错误 | 各 section help | 360/768/1280 | `users.global.sections` | codex ui |
| Users | binding 增删改 | `views/users/shared.js`、`user-management.js` | `features/users/components/UserManagementModal.vue` + `services/usersApi.ts` | migrated | `POST/PUT/DELETE /api/users/{id}/bindings` | 空态/错误态 | override help | 360/768/1280 | `users.binding.sections` | scripts-users smoke |
| Users | binding locks | `views/users/user-management.js` | `features/users/components/UserManagementModal.vue` `bindingValue`/`setBindingValue` | migrated | `binding.effective` 投影 | — | `users.binding.global_override.help` | 360/768/1280 | — | codex ui |
| Users | plugin contributions | `views/users/global-management.js` | `features/users/components/GlobalManagementModal.vue` 贡献字段渲染 | migrated | `GET/PUT /api/plugin-contributions/user-global/...` | 必填校验 toast | field.description | 360/768/1280 | `users.global.sections` | codex ui |
| Users | 用户列表徽章 | `views/users/shared.js` | `features/users/GlobalUserCard.vue` + `UsersPage.vue` badges 投影 | migrated | `GET /api/plugin-contributions/user-list-badges` | 异常隔离 | badge title | 360/768/1280 | `users.list.badges` | scripts-users smoke |
| Users | 用户排序 | `views/users/index.js` | `UsersPage.vue` `reorderUsers` + `ui/sortable.ts` | migrated | `PUT /api/users/order` | 失败回滚重载 | drag_to_reorder | 360/768/1280 | — | vitest（`ui/sortable.test.ts`）、codex ui |
| Users | 头像上传/移除 | `views/users/shared.js` | `UsersPage.vue` + `components/UserManagementModal.vue` | migrated | `POST/DELETE /api/users/{id}/avatar` | 类型/大小 toast | — | 360/768/1280 | — | — |
| Users | 倒计时与到期刷新 | `views/users/shared.js` | `GlobalUserCard.vue` + `composables/useCountdownRefresh.ts` | migrated | `nextRunAt` 本地计算；到期后延迟单次 `load()` 拉取新状态 | — | — | 360/768/1280 | — | vitest（`features/users/composables/useCountdownRefresh.test.ts`） |
| Users | config edit：choose | `views/users/config-edit.js` | `features/users/UsersPage.vue`（chooser 弹窗） | migrated | `GET .../edit-config` `hasSnapshot` | toast 错误 | edit_first copy | 360/768/1280 | — | codex ui |
| Users | config edit：candidate | `views/users/config-edit.js` | `features/users/utils/configEditRequest.ts` + `UsersPage.vue` `configCandidates` | migrated | `config_input_mismatch` 候选 | candidates_help | 360/768/1280 | — | vitest（`features/users/utils/configEditRequest.test.ts`） |
| Users | config edit：edit/done/cancel | `views/users/config-edit.js` | `features/users/components/ConfigEditFlow.vue` + `utils/configEditRequest.ts` | migrated | `POST .../edit-config {action}` | validation toasts | edit_progress copy | 360/768/1280 | — | codex ui |
| Users | config edit：会话恢复 | `views/users/shared.js` `restoreEditSessionCard` | `UsersPage.vue` + `composables/useConfigEditFlow.ts` + `utils/editSession.ts` | migrated | `GET /api/scripts/edit-sessions`；恢复只还原锁定 UI，不重发 `action:start` | 恢复失败静默 | — | 360/768/1280 | — | vitest（`features/users/composables/useConfigEditFlow.test.ts`、`features/users/utils/editSession.test.ts`） |

## Scripts

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Scripts | CRUD | `views/scripts.js` | `features/scripts/ScriptsPage.vue` + `ScriptCard.vue` + `ScriptEditorModal.vue` | migrated | `GET/POST/PUT/DELETE /api/scripts` | toast、必填校验 | 各字段 data-help | 360/768/1280 | `scripts.list.badges` | scripts-users smoke |
| Scripts | 通用/专项选择 | `views/scripts.js` | `ScriptsPage.vue` chooser + `utils/scriptTypes.ts` | migrated | 本地 | 空态 | config_auto copy | 360/768/1280 | — | codex ui |
| Scripts | 专项 root probe | `views/scripts.js` `probeSpecialRoot` | `ScriptEditorModal.vue`（手工输入与原生目录选择统一） + `utils/scriptProbe.ts` | migrated | `POST /api/scripts/probe`；同签名去重、过期响应抑制 | `scripts.plugin.config_derive_failed` toast | — | — | — | vitest（`features/scripts/ScriptEditorModal.test.ts`、`features/scripts/utils/scriptProbe.test.ts`） |
| Scripts | plugin inputs | `views/scripts.js` | `ScriptsPage.vue` `pluginInputs` 透传 + `ScriptEditorModal.vue` | migrated | `pluginInputs` 落盘 | — | — | 360/768/1280 | `scripts.editor.sections` | codex ui |
| Scripts | launch mode / 游戏集成 | `views/scripts.js` | `ScriptsPage.vue` 游戏集成区 | migrated | `launchGame`/`gameMode`/`gameExe` 等 | 必填校验 | data-help | 360/768/1280 | — | codex ui |
| Scripts | judge / keyword | `views/scripts.js` | `ScriptsPage.vue` 判定区 | migrated | `judgeScript*`/`successKeywords` 等 | 判定脚本必填 toast | judge help | 360/768/1280 | — | codex ui |
| Scripts | advanced fields | `views/scripts.js` | `ScriptsPage.vue` 运行设置区 | migrated | `maxAttempts`/超时/`autoUpdateConfig` | 范围校验 | retry help | 360/768/1280 | — | codex ui |
| Scripts | 排序 | `views/scripts.js` | `ScriptsPage.vue` `reorderScripts` + `ui/sortable.ts` | migrated | `PUT /api/scripts/order` | 失败重载 | drag_to_reorder | 360/768/1280 | — | vitest（`ui/sortable.test.ts`）、codex ui |
| Scripts | 判定脚本文件上传 | `views/scripts.js` | `ScriptsPage.vue` `uploadJudgeScript` | migrated | 本地 FileReader | 大小/读取 toast | upload help | 360/768/1280 | — | — |
| Scripts | 卡片徽章与插件状态 | `views/scripts.js`、`core/plugin-list.js` | `ScriptCard.vue` + `features/scripts/utils/pluginStatus.ts` | migrated | 脚本/插件状态派生（`scriptPluginStatus`） | 不可用插件提示 | — | 360/768/1280 | `scripts.list.badges` | scripts-users smoke、codex ui |

## Queues

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Queues | CRUD | `views/queues.js` | `features/queues/QueuesPage.vue` + `QueueCard.vue` + `QueueEditorModal.vue` | migrated | `GET/POST/PUT/DELETE /api/queues` | toast、限额校验 | 各字段 data-help | 360/768/1280 | `queues.list.badges`、`queues.editor.sections` | queues smoke |
| Queues | 定时/任务编辑 | `views/queues.js` | `QueueEditorModal.vue` + `platform/queue-runtime.ts` | migrated | `timeSets`/`tasks` 重排 | 校验 toast | queue help | 360/768/1280 | `queues.editor.sections` | vitest（`features/queues/queueUtils.test.ts`）、visual-contract |
| Queues | 排序与分页 | `views/queues.js` | `QueuesPage.vue` + `ui/sortable.ts` + `ui/primitives/NxpPager.vue` | migrated | `PUT /api/queues/order` | 失败重载 | drag_to_reorder | 360/768/1280 | — | vitest（`ui/sortable.test.ts`、`ui/primitives/NxpPager.test.ts`）、codex ui |
| Queues | 卡片徽章定位 | `views/queues.js` | `QueueCard.vue` | migrated | — | — | — | 360/768/1280 | `queues.list.badges` | visual-contract |

## Dispatch

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Dispatch | 运行中列表 | `views/dispatch.js` | `features/dispatch/DispatchPage.vue` + `platform/page-state.ts` | migrated | `GET /api/status` 1s 轮询 | 空态/状态更新失败 toast | updates_every_second | 360/768/1280 | `dispatch.running.badges`、`dispatch.running.sidecar` | queues smoke |
| Dispatch | 目标选择与执行 | `views/dispatch.js` | `DispatchPage.vue` runbar | migrated | `POST /api/dispatch/{script\|queue}` | 未选目标 toast | target_help | 360/768/1280 | `dispatch.run.sections` | queues smoke |
| Dispatch | 执行计划检查 | `views/dispatch.js` | `DispatchPage.vue` explain | migrated | `POST /api/dispatch/explain/*` | 警告/失败码投影 | plan copy | 360/768/1280 | `dispatch.run.sections` | codex ui |
| Dispatch | 系统操作倒计时 | `views/dispatch.js` | `features/dashboard/SystemActionCard.vue` | migrated | `POST /api/system-action/*` | toast 错误 | data-help | 360/768/1280 | `dispatch.cards` | codex ui |
| Dispatch | 插件卡片 | `views/dispatch.js` | `DispatchPage.vue` slot | migrated | — | — | — | 360/768/1280 | `dispatch.cards` | codex ui |

## History

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| History | 日期/用户筛选 | `views/history.js` | `features/history/HistoryPage.vue` | migrated | `GET /api/history/dates`、`/users` | 空态 | filter help | 360/768/1280 | — | codex ui |
| History | 用户筛选返回 | `views/history.js` | `HistoryPage.vue` `goBack` | migrated | 本地 | — | — | 360/768/1280 | — | codex ui |
| History | 记录列表 | `views/history.js` | `HistoryPage.vue` + `utils/historyFormat.ts` | migrated | `GET /api/history?date&userKey` | 空态/错误态 | — | 360/768/1280 | `history.list.badges` | codex ui |
| History | 详情（尝试/日志/截图） | `views/history.js` | `HistoryPage.vue` + `components/HistoryDetailModal.vue` | migrated | `GET /api/history/detail`、`/image`（`apiBlob` 带 bearer） | 详情错误态 | 日志 tail help | 360/768/1280 | `history.detail.sections` | codex ui |
| History | 时间范围选择 | `views/history.js` | `HistoryPage.vue` range picker | migrated | 本地 | 日期约束 | date_help | 360/768/1280 | — | codex ui |
| History | object URL 清理 | `views/history.js` | `HistoryDetailModal.vue` `closeDetail`/`onBeforeUnmount` | migrated | — | — | — | — | — | codex ui |

## Plugins

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Plugins | 本地列表 | `views/plugins.js` | `features/plugins/PluginsPage.vue` | migrated | `GET /api/plugins` | 加载/错误态 | reading_local copy | 360/768/1280 | — | visual-contract |
| Plugins | store 列表 | `views/plugins.js` | `PluginsPage.vue` | migrated | `GET /api/plugins/store` | stale/unavailable 态 | catalog help | 360/768/1280 | — | visual-contract |
| Plugins | store refresh | `views/plugins.js` `store-refresh` | `PluginsPage.vue` `refreshStoreAsync` + `utils/storeRefresh.ts` | migrated | `POST /api/plugins/store/refresh` → reload store | 失败 toast | — | 360/768/1280 | — | vitest（`features/plugins/utils/storeRefresh.test.ts`） |
| Plugins | update all | `views/plugins.js` | `PluginsPage.vue` `updateAllStorePlugins` | migrated | `POST /api/plugins/store/update-all` | 逐项失败摘要 | update_all copy | 360/768/1280 | — | codex ui |
| Plugins | 详情（README/changelog） | `views/plugins.js`、`core/markdown.js` | `PluginsPage.vue` + `platform/markdown.ts` | migrated | `GET .../detail`；`renderMarkdown` 渲染 | readme 错误码 | readme copy | 360/768/1280 | — | vitest（`platform/markdown.test.ts`）、visual-contract |
| Plugins | 启用/禁用/安装事务 | `views/plugins.js` | `PluginsPage.vue` `runPluginAction` | migrated | `POST /api/plugins/{name}/{action}` | pending/queued toast | action notice | 360/768/1280 | — | codex ui |
| Plugins | 筛选/排序 | `views/plugins.js`、`core/plugin-list.js` | `PluginsPage.vue` + `platform/plugin-list.ts` | migrated | `filterAndSortPlugins`（query/kind/sort） | no_match 空态 | filter help | 360/768/1280 | — | vitest（`platform/plugin-list.test.ts`）、visual-contract |

## Settings

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Settings | appearance（宿主外观服务） | `views/settings.js`、`core/appearance.js` | `platform/appearance.ts`（主题 token/壁纸/轮换/配色）+ `app/bootstrap.ts` `initAppearance` | migrated | 服务端外观快照应用；壁纸与插件主题经 `host.appearance` | 加载失败保留当前主题 | `shell.theme_toggle` | — | — | codex ui |
| Settings | locale | `views/settings.js`、`core/i18n.js` | `SettingsPage.vue` + `platform/i18n.ts`（`setLocale`） | migrated | `PUT /api/settings` | — | language help | 360/768/1280 | `settings.sections` | settings-platform smoke |
| Settings | remote access | `views/settings.js`、`core/modal.js` | `SettingsPage.vue` + `components/SettingsNetworkSection.vue` | migrated | `PUT /api/settings`；`generateToken`/`copyToken` 本地生成与复制 | 令牌校验 | token help | 360/768/1280 | `settings.sections` | settings-platform smoke |
| Settings | MCP | `views/settings.js` | `SettingsPage.vue` | migrated | `PUT /api/settings` | — | mcp help | 360/768/1280 | `settings.sections` | settings-platform smoke |
| Settings | update ready 备份提醒 | `views/settings.js` `update-backup-warning` | `components/UpdateStatusCard.vue` + `utils/updateStatusView.ts` | migrated | `GET /api/update/status`；离开 ready 自动消失 | — | `settings.update.backup_help` | 360/768/1280 | — | vitest（`features/settings/utils/updateStatusView.test.ts`） |
| Settings | update action/automation | `views/settings.js` | `SettingsPage.vue` + `components/UpdateStatusCard.vue` | migrated | `POST /api/update/*` | 失败 toast | update help | 360/768/1280 | `settings.sections` | codex ui |
| Settings | 服务保存后重启 | `views/settings.js` | `SettingsPage.vue` `saveServiceWithRestart`/`restartService` | migrated | `PUT /api/settings`、`POST /api/settings/restart` | 失败 toast | `settings.service.restart_requirements` | 360/768/1280 | `settings.sections` | settings-platform smoke |
| Settings | diagnostics | `views/settings.js` | `SettingsPage.vue` + `components/DiagnosticsSection.vue` | migrated | `GET /api/diagnostics`、导出 | 失败 toast | attention copy | 360/768/1280 | `settings.cards` | codex ui |
| Settings | notifications | `views/settings.js` | `SettingsPage.vue` + `components/SettingsNotificationsSection.vue` | migrated | `PUT /api/settings` + secret | 失败 toast | smtp help | 360/768/1280 | `settings.sections` | settings-platform smoke |

## Shell / 平台

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Shell | nav / mobile nav | `app.js` | `app/App.vue` + `router.ts` + `platform/shell.ts` | migrated | hash 路由 + `<RouterView>`；`setNavOpen`/`setTopbarTitle` | — | — | 360/768/1280 | `shell.nav` | app smoke、vitest（`router.test.ts`） |
| Shell | theme / appearance | `core/appearance.js` | `platform/shell.ts`（主题循环/顶栏/导航态）+ `platform/appearance.ts`（token/壁纸/插件主题） | migrated | `initTheme`/`cycleTheme`/`applyThemeValue`；`host.appearance` | 外观加载失败保留当前主题 | `shell.theme_toggle` | — | — | codex ui、`plugin-bridge/contract.test.ts` |
| Shell | toast / topbar | `core/ui.js` | `platform/toast.ts`（toast 与字段必填/清除错误）+ `platform/shell.ts` | migrated | `toast`/`setRequiredFieldError`/`clearFieldError` | error toast 重复抖动提示 | — | — | — | codex ui |
| Shell | auth / boot error | `core/modal.js`、`legacy/auth.ts` | `platform/auth.ts` + `app/bootstrap.ts` + `app/TokenPrompt.vue` | migrated | `GET /api/status` 401 重认证；`ensureAccessToken`/`installReauthEntry` | boot error 态（`shell.boot.error_details`） | — | — | — | app smoke、settings-platform smoke |
| Shell | i18n | `core/i18n.js` + `wwwroot/i18n` | `platform/i18n.ts` + `frontend/public/i18n`（唯一资源源） | migrated | `t`/`getLocale`/`setLocale`/`applyTranslations`；`X-Nexus-Locale` | 缺失键回退调用点文案 | — | — | — | docs i18n tests |
| Shell | page state / polling | `core/state.js` | `platform/page-state.ts` | migrated | route token、`registerInterval`、`trackController`、离页 `disposePage` | 过期响应按 token 丢弃 | — | — | — | vitest（`platform/page-state.test.ts`） |
| Shell | tooltip | `core/tooltip.js` | `platform/tooltip.ts` | migrated | 延迟气泡与 `data-help` 扫描；`[data-path-trigger]` / `[data-nxp-step]` 不继承字段提示 | — | 字段级 `data-help` 契约 | — | — | vitest（`platform/tooltip.test.ts`、`ui/register.test.ts`）、visual-contract |
| Shell | API client | `core/api.js` | `platform/api.ts` | migrated | bearer/`X-Nexus-Locale`/blob/abort | `ApiError.code`/`status` 投影 | — | — | — | vitest（`platform/api.test.ts`） |
| Shell | 限额警告 | `core/limits.js` | `platform/limits.ts` + `app/bootstrap.ts` 装载 | migrated | `GET /api/limits` 警告与提示条；`dismissWarningOnce`/`dismissWarningForever` | 限额不可用时清空警告 | — | — | — | — |
| Shell | 运行预览 / 环境粒子 | `core/execution-preview.js`、`effects/particles.js` | `platform/execution-preview.ts`（经 `host-adapter.ts` 注入）、`platform/particles.ts` | migrated | `host.executionPreview.capture`；reduced-motion 暂停粒子 | 运行预览返回等待状态 | — | — | `dispatch.running.sidecar` | `plugin-bridge/contract.test.ts` |
| Shell | 悬停滚动 / 输入提示 | `core/dom.js`、`core/forms.js` | `platform/auto-scroll.ts` | migrated | 溢出悬停滚动与 `input-scroll-hint` 显隐 | — | — | — | — | codex ui |
| Plugin bridge | slot / 控件 / 字段渲染 | `core/plugin-slots.js`、`core/controls.js`、`core/plugin-fields.js` | `plugin-bridge/index.ts` facade + `slots.ts`、`controls.ts`、`plugin-fields.ts`、`host-adapter.ts` | migrated | 18 个公开 slot 渲染与 dispose；插件控件与多选字段 | 未支持 slot 抛 `TypeError` | 字段 help/description | 360/768/1280 | 18 个公开 slot | vitest（`plugin-bridge/contract.test.ts`、`plugin-bridge/controls.test.ts`、`plugin-bridge/plugin-fields.test.ts`、`platform/format.test.ts`） |
| Plugin bridge | Frontend API 1.4 运行时 | `core/plugin-runtime.js` | `plugin-bridge/runtime.ts` + `plugin-bridge/types.ts` | migrated | 精确版本 1.4、`host.*` 能力面、route/nav/lifecycle 注册与释放；平台服务只经 `host-adapter.ts` 注入 | 版本不匹配拒绝加载 | — | — | `shell.nav` | `plugin-bridge/contract.test.ts` |
| Plugin route | mount/leave/dispose | `app.js` | `app/PluginRouteHost.vue` | migrated | route token + 插件生命周期与导航态同步 | 无效路由回退 `#/dashboard` | — | — | — | vitest（`router.test.ts`）、codex ui |

路由表在 `frontend/src/router.ts`：宿主页面组件全部懒加载，`frontend/src/ui/UiLabPage.vue`（组件实验室，`#/ui-lab`）为入口 chunk 内联导入，其样式资源随 entry chunk 保留。

## 迁移范围外与已移除项

| Domain | Surface | v0.15.4 reference | Current implementation | Status | API/Behavior | Error/Empty | Help/Tooltip | Responsive | Plugin slot | Test |
|---|---|---|---|---|---|---|---|---|---|---|
| Web 入口 | 旧 HTML 字符串页面 | `wwwroot/views/**`、`app.js` | `router.ts` 路由表 + `<RouterView>` 懒加载页面 | intentionally-removed | 页面统一由真实 Vue Router 承载 | 未知路由走空回退 | — | 360/768/1280 | 18 个公开 slot | vitest（`router.test.ts`）、app smoke |
| i18n | 重复语言资源树 | `wwwroot/i18n/**` | `frontend/public/i18n/`（唯一资源源）+ `platform/i18n.ts` | intentionally-removed | 语言资源只维护一份 | 缺失键回退默认语言 | — | — | — | docs i18n tests |
| 宿主原生 | WinForms 托盘与原生对话框 | `src/`（C# WinForms） | — | not-applicable | 不在 Web 前端迁移范围 | — | — | — | — | unit |

## 18 个公开 slot

| # | Slot | Current implementation | Status | Test |
|---|---|---|---|---|
| 1 | `dashboard.cards` | `features/dashboard/DashboardPage.vue` | migrated | `plugin-bridge/contract.test.ts` + codex ui |
| 2 | `dashboard.after-running` | `DashboardPage.vue` | migrated | `plugin-bridge/contract.test.ts` + codex ui |
| 3 | `users.list.badges` | `features/users/GlobalUserCard.vue` | migrated | `plugin-bridge/contract.test.ts` + scripts-users smoke |
| 4 | `users.binding.sections` | `features/users/components/UserManagementModal.vue` | migrated | `plugin-bridge/contract.test.ts` + codex ui |
| 5 | `users.global.sections` | `features/users/components/GlobalManagementModal.vue` | migrated | `plugin-bridge/contract.test.ts` + codex ui |
| 6 | `scripts.list.badges` | `features/scripts/ScriptCard.vue` | migrated | `plugin-bridge/contract.test.ts` + scripts-users smoke |
| 7 | `scripts.editor.sections` | `features/scripts/ScriptEditorModal.vue` | migrated | `plugin-bridge/contract.test.ts` + codex ui |
| 8 | `queues.list.badges` | `features/queues/QueueCard.vue` | migrated | `plugin-bridge/contract.test.ts` + visual-contract |
| 9 | `queues.editor.sections` | `features/queues/QueueEditorModal.vue` | migrated | `plugin-bridge/contract.test.ts` + visual-contract |
| 10 | `dispatch.cards` | `features/dispatch/DispatchPage.vue` | migrated | `plugin-bridge/contract.test.ts` + codex ui |
| 11 | `dispatch.running.badges` | `DispatchPage.vue` | migrated | `plugin-bridge/contract.test.ts` + codex ui |
| 12 | `dispatch.running.sidecar` | `DispatchPage.vue` | migrated | `plugin-bridge/contract.test.ts` + codex ui |
| 13 | `dispatch.run.sections` | `DispatchPage.vue` | migrated | `plugin-bridge/contract.test.ts` + codex ui |
| 14 | `history.list.badges` | `features/history/HistoryPage.vue` | migrated | `plugin-bridge/contract.test.ts` + codex ui |
| 15 | `history.detail.sections` | `features/history/components/HistoryDetailModal.vue` | migrated | `plugin-bridge/contract.test.ts` + codex ui |
| 16 | `settings.sections` | `features/settings/SettingsPage.vue` | migrated | `plugin-bridge/contract.test.ts` + settings-platform smoke |
| 17 | `settings.cards` | `SettingsPage.vue` | migrated | `plugin-bridge/contract.test.ts` + visual-contract |
| 18 | `shell.nav` | `app/App.vue`（`plugin-nav-host`） | migrated | `plugin-bridge/contract.test.ts` + app smoke |

## 公开 `nxp-*` Native Custom Elements

`frontend/src/ui/register.ts` 注册 25 个公开元素（23 个 primitive + `NxpSwitchSetting` + `NxpLoadingState`），全部 `shadowRoot: false`，
以 light DOM 共享宿主 token 与 shell/layout 样式。`NxpEntityIcon` 为宿主内部 primitive，不注册为公共元素。

元件样式随组件 SFC 维护（`frontend/src/ui/primitives/*.vue`、`frontend/src/ui/composites/*.vue`）；`NxpBadge`、`NxpEmptyState`、`NxpIcon`、`NxpSwitchSetting`、`NxpLoadingState` 复用宿主基础元素与 shell 样式类（`.badge`、`.empty`、`.icon`、`.switch-row`、`.plugin-loading-state`）。

| Status | Test |
|---|---|
| migrated | `ui/register.test.ts`（light DOM 契约）、`ui/sortable.test.ts`、`ui/primitives/NxpModal.test.ts`、`ui/primitives/NxpPager.test.ts`、`ui/primitives/NxpSwitch.test.ts`、`codex ui` |

## 样式单轨与 `wwwroot` 解耦

CSS 单轨布局（加载顺序见 `frontend/src/main.ts`）：

- `frontend/src/styles/tokens.css`：设计 token（`--nx-*` 颜色/圆角/间距/控件高度/焦点环/动效时长）与 reduced-motion 折减。
- `frontend/src/styles/app.css`：基础元素样式（`button`/`input`/`select`/`textarea`/`.btn`/开关控件骨架）与页面、卡片、折叠过渡及拖拽期过渡抑制。
- `frontend/src/styles/shell.css`：布局与 shell、导航/顶栏、页面栅格及插件 surface 样式（原 `wwwroot/style.css`）。
- Nexus UI 元件样式保留在 `frontend/src/ui/**` 的组件 SFC 内，按层叠规则覆盖基础元素样式。

宿主源码与 `wwwroot/` 解耦：

- `frontend/` 是宿主前端源码唯一入口，`frontend/src` 不引用 `wwwroot/`；alias 收敛为 `@`、`@platform`、`@bridge`。
- 旧 `wwwroot/core/**`、`wwwroot/effects/**`、`wwwroot/package.json` 已删除；`wwwroot/` 不再承载宿主前端源码。
- 发布包 Web 静态资源只来自 `frontend/dist`：`build.cmd` 构建 Vite 产物后同步到 `release/wwwroot/`。

## 每页横向核对

| 检查项 | 结论 |
|---|---|
| loading / empty / network error 三态 | 各 feature 页面均实现（见上表 Error/Empty 列） |
| destructive confirmation | Users 删除用户（输入名确认）、Scripts/Queues 删除确认、Plugins uninstall 事务 |
| keyboard / Escape / locked modal | `NxpModal` 统一 Escape 关闭与 locked 语义；配置编辑事务为 locked modal |
| 360 / 768 / 1280 | 各页面响应式断点；`codex ui` 视觉契约固定视口 |
| help / tooltip | 字段级 `data-help`，由 `platform/tooltip.ts` 渲染延迟气泡；路径选择按钮与数字步进按钮不继承字段提示，`platform/tooltip.test.ts` + `ui/register.test.ts` 覆盖 |
| page leave 清理 | 轮询与请求代际（`platform/page-state.ts`）、listener/observer（`app/App.vue`）、object URL（History）、插件 slot dispose |

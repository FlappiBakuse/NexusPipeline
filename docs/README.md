# NexusPipeline 文档门户

本目录按任务提供当前规范、代码入口和验证域。先选择主题，再从专题中的代码定位与测试域进入实现；产品使用方式仍以根目录 README 为入口，已发布历史以 CHANGELOG 为准。

## 按任务进入

| 任务 | 当前规范 | 代码与验证方向 |
|---|---|---|
| 配置编辑、快照或恢复 | [配置与恢复](architecture/configuration.md)、[恢复与保全](architecture/recovery.md) | `src/Services/ConfigSwap*`、`src/Persistence`；config/persistence/System |
| 排查取消、超时或资源租约 | [执行](architecture/execution.md) | `src/Services/Execution`、`src/Services/RunSession.cs`；execution/System |
| 修改调度去重或 occurrence | [调度](architecture/scheduling.md) | `src/Services/Scheduling`；scheduling/System |
| 新增公共控件或复合元件 | [前端架构](architecture/frontend.md)、[UI 目录](reference/ui/README.md) | `frontend/src/ui`、`frontend/src/ui/register.ts`；ui/bridge |
| 修改插件 slot 或 Frontend API | [Frontend API](reference/plugin-api/frontend.md) | `frontend/src/plugin-bridge`；bridge/plugin-contract |
| 新增 managed 能力 | [Managed API](reference/plugin-api/managed.md) | `src/NexusPipeline.Plugin.Abstractions`、`src/Plugins`；plugins/plugin-contract |
| 修改插件安装恢复 | [插件运行时](architecture/plugins.md)、[恢复与保全](architecture/recovery.md) | `src/Services/Plugin*`、安装服务；plugins/System |
| 修改自动更新 | [更新](architecture/update.md) | `src/Services/Update`；update/System |
| 运行指定门禁 | [测试命令](testing/commands.md)、[测试域](testing/domains.md) | `tests/run.mjs`、`tools/ci-domains.mjs` |
| 查当前未完成事项 | [状态](STATUS.md) | 证据分级和待处理风险 |
| 追溯已发布版本 | [CHANGELOG](../CHANGELOG.md) | [历史说明](decisions/README.md)与 Git 历史 |

## 规范入口

- [架构专题](architecture/frontend.md)：模块边界、运行、配置、调度、判定、持久化、更新和插件运行时。
- [公共 UI 参考](reference/ui/README.md)：公开 `nxp-*` 元件、选型和交互边界。
- [插件 API 参考](reference/plugin-api/frontend.md)：Frontend API、managed API、manifest 和 slot 生命周期。
- [开发流程](development/setup.md)：环境、构建、调试和双仓库协作入口。
- [测试规范](testing/README.md)：测试层级、完整命令、影响域和夹具边界。
- [决策记录](decisions/README.md)：v0.16.6 当前公共化与测试治理决定。

机器路由保存在 [map.json](map.json)。本地可执行 `node tools/docs-index.mjs find 配置恢复 --json`，默认只返回当前主题。

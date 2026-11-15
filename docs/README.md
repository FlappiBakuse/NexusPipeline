# 文档导航

本仓拥有平台与公开 SDK；插件业务和上游支持范围归 [Plugins 文档](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/next/docs/README.md)。当前未完成事项集中于 [status](status.md)，实际运行结果由绑定源码与产物的回执记录。

## 按任务进入

| 读者或任务 | 入口 |
|---|---|
| 使用、配置、备份软件 | [用户指南](user/README.md) |
| 第一次从源码启动 | [开发入门](getting-started/development.md) |
| 开发流程与提交审查 | [贡献指南](../CONTRIBUTING.md) |
| AI 定位代码和验证层 | [AGENTS](../AGENTS.md) → [任务地图](agents/README.md) |
| 理解组件与数据流 | [系统架构](architecture/overview.md) |
| 选择测试、浏览器场景或性能测量 | [验证](development/validation.md)、[前端验证](development/frontend-testing.md)、[性能](development/performance.md) |
| 安装、更新、发行或排障 | [维护生命周期](operations/installation.md)、[发行](operations/release.md)、[诊断](operations/diagnostics.md) |

## 主题与权威来源

表中页面维护解释与约束；字段、默认值、限额及生成内容最终由链接的源码、schema 和检查器核对。

| 主题 | 主要维护位置 | 代码或机器权威 |
|---|---|---|
| 系统分层、模块边界 | [架构](architecture/overview.md) | `apps/host/Composition`、`src/core`、`src/platform` |
| 用例与跨域调用 | [Storage owner](architecture/storage-owners.md) | `apps/host/Control`、`src/platform/Storage` |
| 配置来源、活动进度、同步 | [配置生命周期](architecture/configuration.md) | `src/core/Configuration`、`Storage/Configuration` |
| 普通重试和冷恢复 | [状态与重试](architecture/state-and-retry.md) | `src/core/Execution`、`Storage/Execution` |
| 启动、历史按需加载 | [启动](architecture/startup.md) | `apps/host/Composition`、`Storage/Sqlite` |
| GUI、托盘和启动反馈 | [桌面启动](architecture/desktop-startup.md) | `apps/host/Desktop`、`apps/desktop` |
| 进程拥有权 | [进程归属](architecture/process-ownership.md) | `src/platform/Processes` |
| 页面和公共组件边界 | [前端架构](architecture/frontend.md) | `apps/web/src` |
| 定义、顺序与交换 | [自动化](reference/automation.md) | `src/core/Automation`、`Storage/Automation` |
| 八库、事务、journal、备份与 GC | [存储](reference/storage.md) | `Storage/Sqlite`、`Storage/Journals` |
| 活动恢复额度的检索入口 | [恢复容量](reference/recovery-capacity.md) | 额度与冻结规则由[存储准入](reference/storage.md#启动与恢复准入)维护 |
| 快照、CAS、补丁与原生编辑 | [配置存储](reference/configuration-storage.md) | `Storage/Configuration` |
| 执行计划、日志与截图 | [执行](reference/execution.md) | `Storage/Execution` |
| worker、项目输入与目标控制 | [执行 provider](reference/execution-providers.md) | `Storage/Extensions`、`sdk/dotnet` |
| 调度、通知与完成动作 | [调度](reference/scheduling.md) | `Storage/Scheduling`、`Storage/Notifications` |
| 插件信任、安装与生命周期 | [扩展](reference/extensions.md) | `src/core/Extensions`、`Storage/Extensions` |
| 插件前端和数据端口 | [扩展前端](reference/extension-frontend.md) | SDK 与 Host frontend bridge |
| UI 属性、事件与 slot | [前端 ABI](reference/frontend-abi.md) | [ui-elements](../contracts/ui-elements.json)及其生成物 |
| 草稿、历史窗口与媒体释放 | [Web 状态](reference/frontend-state.md) | `apps/web/src/app`、`control`、`pages` |
| 原生会话、签名与退出 | [桌面授权](reference/desktop-authorization.md) | `apps/host/Control/Http/DesktopControl.cs`、`apps/desktop` |
| 设置、即时保存与系统效果 | [设置](reference/settings.md) | `src/core/Settings`、`Storage/Settings` |
| 秘密持久化与撤销 | [秘密](reference/secrets.md) | `Storage/Secrets` |
| HTTP、CLI、MCP 访问边界 | [控制面](reference/control-http.md)、[CLI 菜单](reference/cli-menu.md) | `apps/host/Control`、[OpenAPI](../contracts/openapi.json) |
| schema 和公开协议 | [契约](reference/contracts.md)、[SDK](reference/sdk.md) | `contracts/schemas`、`contracts/sdk-exports.json`、`sdk` |
| 项目编译与公开预览 | [项目编译](reference/compilation.md) | `Storage/Extensions`、`sdk` |
| 判定脚本与探测 | [判定](reference/judgement.md) | `src/platform/Execution/Judgement` |
| Android 目标 | [模拟器目标](reference/emulator-targets.md) | `Storage/Emulation`；厂商规则归 Plugins |
| 工具参数与依赖锁 | [命令参考](reference/commands.md) | `tooling`、`global.json`、原生锁 |
| 命名与例外 | [命名规范](development/naming.md) | Python / C# / TS AST 检查器 |

## 决策、版本与历史

现有决策保留可核对背景：[Electron 选型](decisions/desktop-shell.md)、[资产准备](decisions/asset-preparation.md)、[秘密准备](decisions/secret-preparation.md)、[留存续办](decisions/retention-resume.md)。当前版本摘要见 [0.18.0](releases/0.18.0.md)。[历史索引](archive/README.md)只用于了解旧阶段范围及证据。

`docs/users` 保留旧安装链接导航；更新屏障仍引用该路径。`tooling/application-readme.md` 是成品随包说明，`apps/desktop/README.md` 是桌面模块入口，`metadata/README.md` 说明旧客户端发现协议，各自具有独立消费位置。

## 维护规则

- README 只保留产品定位、最短使用路径、技术概览和导航。贡献流程归 CONTRIBUTING；AGENTS 只保留持续有效的执行约束和模块路由。
- 入门教学放 `getting-started`；架构与数据流放 `architecture`；操作和验证放 `development`；接口与格式解释放 `reference`；安装、排障与发行放 `operations`。用户操作留在 `user`，不要求用户先阅读内部协议。
- 修改公共接口检查对应参考和生成权威；修改模块边界检查架构与 owner；修改命令检查入门和开发指南；修改部署/维护检查 operations；修改用户行为检查对应任务页。只更新被影响主题。
- 长期改变职责、信任、持久格式或恢复策略且存在实际取舍时，记录 `decisions/<主题>.md`，写明背景、选择、原因、后果和当前依据。已有决定按记录保留，缺失日期或原因不补造。
- 一个模块具有独立任务、契约或复杂生命周期时增加模块页，并在本索引和任务地图链接；只有独立执行规则才增加子 AGENTS。
- schema、SDK API 清单、UI 元数据、指纹和产物清单由现役 producer 生成；手写文档解释用途和边界，不复制字段全集或测试方法计数。
- 新页面先检查现有权威位置，其他页面用相对链接引用。迁移同时更新仓内和配套仓链接；程序引用的兼容路径保留简短导航。
- 当前事实给出代码/配置依据；设计意图归决策；未验证假设明确标注；未完成项只在 status 维护。历史报告保留原源码身份，新增结果不覆盖原失败。

分类采用 [Diátaxis](https://diataxis.fr/) 的读者需求；入口与贡献职责参考 [GitHub Docs](https://github.com/github/docs)、[VS Code](https://github.com/microsoft/vscode) 和 [Kubernetes Community](https://github.com/kubernetes/community)，目录按本项目实际内容设置。

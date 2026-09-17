# 架构与当前行为

[文档门户](README.md) · [架构索引](architecture/README.md)

本文件保留为宿主架构的兼容入口。当前完整规则按职责分布在[架构专题](architecture/README.md)；产品边界、核心概念和接受的约束位于[产品边界与模块](architecture/overview.md)，运行、配置、插件和更新分别由对应专题维护。

## 当前阅读入口

| 需要了解 | 当前权威专题 |
|---|---|
| 产品定位、核心概念、模块关系和已接受行为 | [产品边界与模块](architecture/overview.md) |
| 脚本运行、取消、超时、进程和资源租约 | [执行与资源生命周期](architecture/execution.md) |
| 配置快照、编辑隔离、交换和同步 | [配置编辑与交换](architecture/configuration.md) |
| 崩溃恢复、事务身份和现场保全 | [恢复与现场保全](architecture/recovery.md) |
| 队列 occurrence、去重和调度恢复 | [调度与去重](architecture/scheduling.md) |
| 完成判定、日志增量和超时 | [完成判定与日志监控](architecture/judgement-logs.md) |
| 历史、运行状态和文件布局 | [历史与持久化](architecture/persistence-history.md) |
| 通知、代理和外部网络出口 | [通知与可观测性](architecture/observability.md) |
| Web、CLI、MCP 和实时事件 | [控制面架构](architecture/control.md) |
| 插件发现、安装事务和生命周期所有权 | [插件运行与安装](architecture/plugins.md) |
| 更新发现、校验、替换和重启 | [自动更新](architecture/update.md) |
| Vue 分层、公共 UI 和插件桥接 | [前端架构](architecture/frontend.md) |

## 代码定位

- 后端组合根与启动：`src/Application`、`src/RuntimeContext.cs`、`src/Bootstrap.cs`。
- 运行、调度和配置：`src/Services/Execution`、`src/Services/Scheduling`、`src/Services/ConfigSwap`、`src/Persistence`。
- 控制面与插件：`src/Web`、`src/Cli`、`src/Mcp`、`src/Plugins`、`src/NexusPipeline.Plugin.Abstractions`。
- 前端与公开元件：`frontend/src/app`、`frontend/src/platform`、`frontend/src/plugin-bridge`、`frontend/src/ui`。
- 测试入口、影响域和文档索引：`tests/run.mjs`、`tools/ci-domains.mjs`、`tools/docs-index.mjs`。

完整命令与测试层级见[测试索引](testing/README.md)。

## 旧链接承接

以下显式锚点用于承接历史入口。它们只把读者转到当前专题，不复制旧版目录正文。

<a id="1-设计理念"></a>
<a id="2-核心概念"></a>
<a id="3-核心运行流程"></a>
<a id="4-配置交换机制"></a>
<a id="5-完成判定机制"></a>
<a id="6-日志监控机制"></a>
<a id="7-通知与数据落盘"></a>
<a id="8-已知行为与边界"></a>
<a id="10-架构与模块定位开发者导航"></a>

历史链接对应关系见 [migration-map.json](migration-map.json)。已发布版本的历史事实见[历史索引](history/README.md)，当前未完成事项见 [STATUS.md](STATUS.md)。

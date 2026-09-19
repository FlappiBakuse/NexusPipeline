# 架构专题

本索引汇总当前产品机制与模块边界。修改运行时行为、持久化协议、控制面或前端分层时，先从对应专题进入；代码路径和测试域以专题中的当前说明及 [测试索引](../testing/README.md) 为准。

## 专题导航

| 专题 | 范围 | 代码与验证方向 |
|---|---|---|
| [产品边界与模块](overview.md) | 产品定位、核心概念、已接受约束、模块结构和数据流 | `src/Host`、`src/Host/Composition`、`frontend/src/app`；core/control |
| [执行与资源生命周期](execution.md) | 冻结计划、准入、尝试、取消、超时、进程清理和租约 | `src/Modules/Execution`；execution/System |
| [配置编辑与交换](configuration.md) | 快照、配置形态、运行前后交换、编辑隔离和运行结果同步 | `src/Modules/Configuration`、`src/Platform/Storage`；config/persistence/System |
| [恢复与现场保全](recovery.md) | 会话标记、事务身份、启动恢复、延迟重试和人工处理边界 | `src/Modules/Configuration/Recovery`、`src/Modules/Plugins/Repository`；persistence/plugins/update |
| [调度与去重](scheduling.md) | occurrence、调度 fence、队列执行和重试窗口 | `src/Modules/Scheduling`、`src/Platform/Windows/TaskRegistration.cs`；scheduling/System |
| [完成判定与日志监控](judgement-logs.md) | 判断脚本、关键字、日志文件形态和超时 | `src/Modules/Execution/Judgement`、`src/Modules/Execution/Monitoring`；judgement/execution |
| [历史与持久化](persistence-history.md) | 运行历史、日志、运行状态目录和文件布局治理 | `src/Modules/History`、`src/Platform/Storage`；persistence/observability |
| [通知与可观测性](observability.md) | 通知渠道、代理出口、诊断和敏感信息边界 | `src/Modules/Notifications`、`src/Platform/Networking`、`src/Modules/Diagnostics`；observability/control |
| [控制面架构](control.md) | Web、CLI、MCP、实时事件与重启通道 | `src/ControlPlane`、`src/Modules/Execution/Realtime`；control/System |
| [插件运行与安装](plugins.md) | 插件发现、能力注册、pending 安装事务和生命周期所有权 | `src/Modules/Plugins`、`src/NexusPipeline.Plugin.Abstractions`；plugins/System |
| [自动更新](update.md) | 更新源、策略、下载校验、替换、重启与闲时应用 | `src/Modules/Updates`、`update-policy.json`；update/System |
| [前端架构](frontend.md) | Vue 分层、宿主平台、桥接、公共元件和插件消费边界 | `frontend/src/app`、`frontend/src/platform`、`frontend/src/ui`；ui/bridge |

专题之间的关系：配置专题描述正常事务，恢复专题描述中断现场；执行专题负责运行编排，判定与日志专题负责一次尝试的输入；插件 API 和公共 UI 的可消费契约分别见[插件 API 索引](../reference/plugin-api/README.md)与[UI 参考](../reference/ui/README.md)。

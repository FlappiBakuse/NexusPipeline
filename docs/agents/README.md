# Agent 任务地图

读取顺序：[根指令](../../AGENTS.md) → 本表相关行 → 领域文档 → 生产 owner、直接调用者及相关测试。共同开发流程归 [CONTRIBUTING](../../CONTRIBUTING.md)，验证命令归[验证规范](../development/validation.md)。不要求一次读取全部 docs。

| 任务 | 首读 | 代码起点 | 验证方向 |
|---|---|---|---|
| 定义、账号、绑定、队列 | [自动化](../reference/automation.md) | `src/core/Automation`、`Storage/Automation`、`apps/host/Control/Automation` | 顺序、修订、引用及真实控制入口 |
| 首次快照、进度、同步与编辑 | [配置](../architecture/configuration.md)、[配置存储](../reference/configuration-storage.md) | `Storage/Configuration`、`Storage/Execution/ExecutionAdmissionPort.cs` | 首次建立、重试接续、CAS、原现场恢复 |
| 普通失败、冷恢复或预算 | [状态与重试](../architecture/state-and-retry.md) | `src/core/Execution`、`Storage/Execution` | 所改分类与一条实际消费链 |
| 数据库、备份与回收 | [存储](../reference/storage.md) | `Storage/Sqlite`、`Storage/Journals` | 跨库回滚、身份、完整备份及引用保护 |
| 日程、通知或系统完成动作 | [调度](../reference/scheduling.md) | `Storage/Scheduling`、`Storage/Notifications` | occurrence 去重、Unknown 核对与授权效果 |
| HTTP、CLI、MCP | [控制面](../reference/control-http.md) | `apps/host/Control` | 同一用例、权限、错误和 operation 身份 |
| 页面、草稿与即时设置 | [前端](../architecture/frontend.md)、[Web 状态](../reference/frontend-state.md) | `apps/web/src/app`、`pages`、`control`、`components` | 真实表单、原操作、可见性与资源释放 |
| 公共控件和插件 UI | [前端 ABI](../reference/frontend-abi.md)、[扩展前端](../reference/extension-frontend.md) | `apps/web/src/ui`、`sdk/frontend` | AST/类型与实际插件消费者 |
| GUI、托盘、退出与重启 | [桌面启动](../architecture/desktop-startup.md)、[桌面授权](../reference/desktop-authorization.md) | `apps/host/Desktop`、`apps/desktop` | 同源原生入口、失败与超时释放 |
| 安装、更新和卸载 | [维护](../operations/installation.md) | `apps/installer`、`apps/maintenance`、`apps/uninstaller` | 受影响实际成品事务 |
| 插件目录和激活 | [扩展](../reference/extensions.md) | `Storage/Extensions`、`src/platform/Extensions` | 初始化、撤销、离线恢复与目录变化 |
| SDK 和项目编译 | [SDK](../reference/sdk.md)、[编译](../reference/compilation.md) | `contracts`、`sdk`、Host producer → Plugins import | 生成一致性、独立消费者与必要运行边界 |
| 性能与读写归因 | [性能](../development/performance.md)、[诊断](../operations/diagnostics.md) | 实际热点所属 owner | 同源输入、绝对读写、CPU/内存及回退 |
| 文档 | [文档导航](../README.md) | 对应主题的唯一维护位置 | 链接、锚点、源码路径、命令和状态 |

表中的 `Storage/` 位于 `src/platform/Storage`。测试入口分布于 `tests/unit`、`tests/integration`、`tests/frontend`、`tests/windows` 和 `tests/tooling`；以 runner 实际选择能力确定范围。

Host 拥有公共契约与权限，Plugins 拥有上游参数、任务和日志语义。只有实际契约改变或直接消费者受影响时才扩大跨仓范围。同源 SDK 使用真实 producer 回执；固定上游规则从 Plugins 对应项目页和锁读取。

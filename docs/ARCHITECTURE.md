# NexusPipeline 架构说明

本文件是开发者的定位指南：模块边界、依赖方向、如何定位功能、如何扩展插件。
核心设计理念与运行流程见 [DESIGN.md](DESIGN.md)；版本历史见 [CHANGELOG.md](../CHANGELOG.md)。

> 产品行为以 [DESIGN.md](DESIGN.md) 为唯一详细来源；版本演化进入 [CHANGELOG.md](../CHANGELOG.md)，当前未解决风险进入 [KNOWN_ISSUES.md](KNOWN_ISSUES.md)，协作与文档治理规则见 [CONTRIBUTING.md](../CONTRIBUTING.md)。
> 命名治理：以 `NexusPipeline.*` 命名的目录只用于独立 .NET project，目录名应与 csproj / assembly identity 保持一致；普通领域目录使用 `Application` / `Services` / `Web` / `Cli` 等语义名称。现有 Plugin API、测试工程和 fixture assembly identity 保持不变。

## 总体结构

```
NexusPipeline/
├── src/                C# 后端（.NET 8，WinForms 托盘 + HttpListener）
│   ├── Application/    应用宿主、启动流程与业务端口：ProgramEntry/ApplicationHost/StartupPipeline/RuntimeInitializer/Abstractions/Repositories
│   ├── *.cs            组合根基础设施：Bootstrap/RuntimeContext/TrayApp
│   ├── Models/         领域模型（NexusPipeline.Models）
│   ├── Services/       服务层（NexusPipeline.Services，按 Execution/Configuration/Judgement/Scheduling/History/Notification 分域）
│   ├── Persistence/    持久化层（NexusPipeline.Persistence）
│   ├── Utilities/      工具层（NexusPipeline.Utilities）
│   ├── Extensibility/  宿主内部数据插件 capability 契约（NexusPipeline.Extensibility，internal）
│   ├── Web/            HTTP 层（NexusPipeline.Web）
│   ├── Cli/            命令行层（NexusPipeline.Cli）
│   └── Plugins/        数据化/managed-code 插件发现、加载与 capability 注册（NexusPipeline.Plugins）
├── src/NexusPipeline.Plugin.Abstractions/  独立 public Plugin API v1（无宿主业务引用）
├── wwwroot/            前端（零构建 ES modules，浏览器直接加载）
│   ├── app.js          路由 + 事件委托（唯一入口）
│   ├── core/           平台层（与业务无关的通用能力）
│   ├── views/          业务视图（一域一文件）
│   └── effects/        独立视觉效果
├── .nxp/               安装目录内的内部运行状态（runtime 标记与 state 持久状态）
├── tests/
│   ├── NexusPipeline.Tests/  xUnit 单元测试（通过 InternalsVisibleTo 访问 internal 契约）
│   ├── system/               Windows 真实进程 System Smoke（runtime/judge/execution-resilience/emulator/update）
│   ├── e2e/                  Playwright 端到端测试（黑盒，@playwright/test 框架）
│   ├── documentation/        Node 内建模块文档一致性检查
│   ├── support/               Windows 进程、版本解析与测试公共设施
│   └── legacy/                历史考据与专项诊断资产（不进入 CI/发布门禁）
├── tools/source-hash.mjs      Node 源码指纹计算（排除 bin/obj）
└── tests/run.mjs              统一测试调度入口
```

## 后端分层（src/）

### 依赖方向（只允许向下依赖）

```
NexusPipeline（根：Application/Program/Bootstrap/RuntimeContext 组合根）
   └── Models（领域模型）← Services（服务）← Persistence（持久化）← Utilities（工具，被一切依赖）
        ↑           ↑            ↑
NexusPipeline.Web（HTTP 适配层）
NexusPipeline.Cli（命令行适配层）
NexusPipeline.Extensibility（中立 capability/profile 契约）
NexusPipeline.Plugins（插件发现、注册与内置实现）
```

- **核心域不得引用 Web/Cli**（例外：`RuntimeContext` 组合根持有 `PluginManager` 实例——组合根允许）。
- **Web/Cli 只调用核心域服务，不做业务逻辑**，只做参数解析与响应组装。
- **Plugins 通过数据化 manifest 或独立 Plugin API v1 交互**；`NexusPipeline.Plugin.Abstractions` 不引用宿主业务模型，managed-code 插件由 collectible `AssemblyLoadContext` 隔离加载；跨模块的宿主内部 capability/profile 契约位于 `Extensibility/`，数据化专项插件（`DataSpecializedPlugin`）仍为纯数据驱动。
- **依赖方向顺沿命名空间**：Models 无依赖；Services 依赖 Models/Persistence/Utilities；Persistence 依赖 Utilities。
- **已知偏差（如实记录）**：执行核心、调度器和配置编辑的能力消费通过显式端口连接，运行期数据读取通过 `Application/Abstractions/` 仓储完成；`ConfigSwapRecovery` 的损坏标记兼容恢复通过构造注入的 `IConfigRecoveryDataSource` 获取数据，不反向查找组合根。`Utilities/Logger` 读取 `RuntimeContext.Instance.Settings`（Utilities → 根命名空间）是保留的最小例外。新服务不得新增这类依赖。

### 关键类职责

| 类 | 位置 | 职责 |
|---|---|---|
| `Program` | src/Application/ProgramEntry.cs | 进程入口，仅转交 `ApplicationHost.Run(args)` |
| `ApplicationHost` | src/Application/ApplicationHost.cs | 进程级初始化后的命令分发与 CLI 处理 |
| `RuntimeInitializer` | src/Application/RuntimeInitializer.cs | 管理员权限、旧配置迁移、约束/设置/数据加载；不启动服务 |
| `StartupPipeline` | src/Application/StartupPipeline.cs | 常驻服务、网页模式与重启的单实例互斥、恢复、Web/托盘生命周期 |
| `RuntimeStateLayout` | src/Persistence/RuntimeStateLayout.cs | 取得 service ownership 后创建 `.nxp` 目录、迁移旧运行状态、保存冲突现场和提供旧端口兼容读取 |
| `Bootstrap` | src/Bootstrap.cs | 服务启动/停止编排、Web 端口重试 |
| `RuntimeContext` | src/RuntimeContext.cs | 组合根：内部 ServiceProvider 注册各领域服务和 `Application/Abstractions/` 运行时适配器，外部访问方式不变；`Resolve<T>()` 服务解析出口 |
| `IScriptRepository` / `IQueueRepository` / `IUserRepository` / `IExecutionSnapshotProvider` | src/Application/Abstractions/、src/Application/Repositories/ | 执行/调度域读取脚本、队列、启用用户及同一数据锁内的执行输入快照；运行时适配器保留现有共享列表、锁和深拷贝快照语义 |
| `ISettingsProvider` / `IHistoryStore` | src/Application/Abstractions/、src/Application/Repositories/、src/Services/History/ | 设置读取与历史写入端口，避免服务直接反向查组合根或具体历史文件实现 |
| `IExecutionService` / `INotificationService` / `IPluginCapabilityResolver` | src/Application/Abstractions/ | Web、Scheduler、执行域和插件能力消费端口；具体实现仍由现有 `ExecutionCommands`、`NotificationDispatcher`、`PluginManager` 提供 |
| `ExecutionCommands` | src/Application/Commands/ExecutionCommands.cs | Web、Scheduler 与常驻服务 CLI 通道共享的启动/取消应用命令入口 |
| `DataStore` | src/Persistence/DataStore.cs | 持久化仓储（scripts/queues JSON 读写） |
| `DispatchCenter` | src/Services/DispatchCenter.cs | 兼容执行门面：获取冻结计划、提交准入登记、取消和入口参数编排；不承载后台运行流程 |
| `ExecutionPlanBuilder` | src/Services/Execution/ExecutionPlanBuilder.cs | 从脚本/队列/用户仓储快照构建脚本与队列执行计划，固定任务引用、用户顺序、资源和完成操作；运行时通过 `IExecutionSnapshotProvider` 获取队列与脚本的原子输入 |
| `ExecutionValidator` | src/Services/Execution/ExecutionValidator.cs | 脚本/队列存在性、用户门禁、长时混排、进程预检和任务计数校验 |
| `ExecutionAdmissionPolicy` | src/Services/Execution/ExecutionAdmissionPolicy.cs | 纯逻辑比较 EmulatorOnly/Standard 矩阵、重复目标、资源冲突、完成操作兼容性和 pending 阻断，并标注瞬时/永久失败 |
| `ExecutionRunner` | src/Services/Execution/ExecutionRunner.cs | 脚本/队列后台生命周期、队列内用户串行、历史落盘、通知和完成意图提交 |
| `SystemActionExecutor` | src/Services/Execution/SystemActionExecutor.cs | 运行组空闲后的完成操作 arm、pending 倒计时和取消语义 |
| `ExecutionCoordinator` | src/Services/Execution/ExecutionCoordinator.cs | 一次运行级编排：用户顺序、重试循环、配置事务和运行收尾；后台任务与历史/通知外层边界由 `ExecutionRunner` 承载 |
| `RunSession` | src/Services/RunSession.cs | 一次运行的状态对象：元数据、预算、日志收集、配置事务状态和回调；不再拥有 `RunAsync` 流程 |
| `AttemptRunner` | src/Services/Execution/AttemptRunner.cs | 单次尝试执行入口；协调器通过该边界调用前/后置脚本与监控执行 |
| `RetryPolicy` / `ResultCollector` | src/Services/Execution/ | 普通失败重试判定、日志容量/按尝试分段收集 |
| `CleanupManager` / `RunAttemptFinalizer` | src/Services/Execution/ | 执行域清理门面与 Windows 进程/游戏清理基础设施 |
| `ExecutionStateStore` | src/Services/Execution/ExecutionStateStore.cs | 线程安全管理运行中/已结束任务、准入 profile 资源租约、运行组 `Open/Closing/ActionPending` 状态、完成意图与待执行系统操作，并为配置 CRUD 提供租约协调 |
| `RunningExecution` | src/Services/Execution/RunningExecution.cs | 单次运行的可观察状态、并发安全记录/日志写入与一致快照 |
| `RunBudget` | src/Services/Execution/RunBudget.cs | 统一整个运行（含重试、前置/后置脚本）的 elapsed/remaining/命令超时上限；保留 `NEXUS_TIME_SCALE` 语义 |
| `ConfigRunSession` | src/Services/Configuration/ConfigRunSession.cs | 运行期间配置事务的收尾编排：固定同步、替换还原、script 清理和现场恢复顺序 |
| `ConfigurationTransaction` | src/Services/Configuration/ConfigurationTransaction.cs | 配置 prepare/retry/sync/replace/rollback 原语边界，兼容现有 `ConfigSwap` 磁盘协议 |
| `RunAttemptFinalizer` | src/Services/Execution/RunAttemptFinalizer.cs | attempt 级脚本进程树、游戏/模拟器清理基础设施；承载失败/取消/强制关闭策略，不改变既有清理时序 |
| `SessionJudge` | src/Services/Judgement/SessionJudge.cs | 完成判定策略状态机：判断脚本/关键字两模式，维护判定状态与输入 |
| `JudgeScriptRunner` | src/Services/Judgement/JudgeScriptRunner.cs | 判断脚本执行器：构造脚本字段、用户、config（只读）、script（可读写）和**本次尝试日志段**输入；提供 Jint/Python 执行、30 秒超时和 stdout 尾行 JSON 解析（含 `replaceConfigs`） |
| `LogMonitor` | src/Services/LogMonitor.cs | 日志增量读取器：追加/截断/替换三形态；替换使用 FileId 与创建时间回退检测，忽略运行前已有内容 |
| `UserConfigManager` | src/Services/UserConfigManager.cs | 配置储存对外门面，实现分层见 `ConfigSwapPrimitives`/`ConfigSwapSession`/`ConfigSwapPaths`；转发自动更新配置同步 |
| `ConfigSwapPrimitives` | src/Services/ConfigSwapPrimitives.cs | 配置交换文件原语层：安全移动/原子替换/重试/跨进程互斥/形态判断 |
| `ConfigSwapSession` | src/Services/ConfigSwapSession.cs | 配置交换兼容 façade：replaceConfigs、自动更新配置事务镜像与公共会话入口；恢复职责转交 `ConfigSwapRecovery` |
| `ConfigSwapRecovery` | src/Services/ConfigSwap/ConfigSwapRecovery.cs | `.session` 自愈、启动扫描、孤儿进程延迟重试、模板/原配置还原；按当前全局用户绑定建立 UserId 恢复白名单；脚本/用户读取经注入的 `IConfigRecoveryDataSource` |
| `ConfigSessionMark` / `EditSession` | src/Services/ConfigSwap/ | 配置会话持久化标记与 Web 编辑会话状态模型 |
| `ConfigSwapPaths` | src/Services/ConfigSwapPaths.cs | 配置数据目录管理：data/{脚本Id}/{UserId} 子目录定位、受限迁移与清理 |
| `LogPattern` | src/Persistence/LogPattern.cs | 日志路径格式解析（日期占位符/通配符严格匹配，无格式外猜测） |
| `Scheduler` | src/Services/Scheduling/Scheduler.cs | 定时/启动时触发队列；瞬时准入冲突进入 pending 触发并在后续 tick 重试，永久校验失败消费本次触发；通过队列仓储、历史、设置、执行端口和 `ExecutionValidator` 工作 |
| `HistoryService` | src/Services/History/HistoryService.cs | 历史记录读写与清理 |
| `NotificationDispatcher` | src/Services/Notification/NotificationDispatcher.cs | 宿主内置 Webhook/SMTP 通知领域服务；脚本、队列和 Plugin API v1 DTO 均从此入口发送 |
| `WebServer` | src/Web/WebServer.cs | HTTP 骨架：监听、静态文件安全头、特性路由表（[ApiRoute] 反射扫描注册）和远程令牌校验 |
| `HttpHelper` | src/Web/HttpHelper.cs | 通用 HTTP 辅助（写 JSON/404/405/解析请求体） |
| `ApiXxxHandler` | src/Web/ | 每资源一个 handler，`[ApiRoute("资源名")]` 标注，路由表自动注册 |
| `MainMenu` + 菜单类 | src/Cli/ | 命令行交互（主菜单/脚本/队列/调度/历史/插件/设置/通知渠道） |
| `PluginCapabilityRegistry` | src/Plugins/PluginCapabilityRegistry.cs | capability 的类型化注册/查询与数据插件 key 注册；`LoadAll` 清空后重建，避免重复能力 |
| `PluginManager` | src/Plugins/PluginManager.cs | 插件发现/加载/开关和兼容 façade；通用 capability 查询委托 registry，元数据投影不携带业务能力字段 |
| `PluginContracts` | src/Extensibility/PluginContracts.cs | 数据插件的 `IPluginCapability`/profile 契约与 `ScriptProfile`；全部 internal；外部代码插件契约位于独立 Plugin API 项目 |
| `Logger` | src/Utilities/Logger.cs | 分级日志（DEBUG/INFO/WARN/ERROR/FATAL），阈值过滤，控制台着色 |

### public / internal 约定

- 主程序程序集仍只向自身暴露 `Program`（入口）与领域模型；外部代码插件只引用独立的 `NexusPipeline.Plugin.Abstractions` public API v1。宿主内部的 `IPluginCapability`/`ScriptProfile` 不属于外部插件契约，Plugin API 不暴露宿主 DI 或领域模型。
- 其余全部 `internal`：新增类型默认 internal，除非它属于契约清单。

### 新增 API 的落点

- HTTP 路由：在 `src/Web/` 新增或扩展 `ApiXxxHandler`，类上标注 `[ApiRoute("资源名")]`（子路由标注在方法上，如 `cancel`）；`WebServer` 启动时反射扫描自动注册，**无需改路由表**。
- 命令行菜单：在 `src/Cli/` 对应菜单类加 case。
- 业务服务：核心域 `Services/` 新增服务类，注册到 `RuntimeContext`（组合根）后经 `Resolve<T>()` 或属性访问。

## 前端分层（wwwroot/）

### 依赖方向

```
app.js → views/* → core/*（api/state/ui/modal/forms/dom/format）
views/* 互不引用（跨域数据只经 core/state.js 缓存共享）
```

### 模块职责

| 模块 | 职责 |
|---|---|
| `app.js` | 路由表 + 各视图 `actions` 注册表合并分发 + 全局 input 委托。**不加业务逻辑** |
| `views/scripts.js` | 脚本实例页（紧凑列表行 + 新建卡片组 + 通用/专用弹窗，草稿为模块变量） |
| `views/users.js` | 全局用户页兼容导出层（转发 `global-users.js`） |
| `views/global-users.js` | 全局用户卡片、头像、脚本绑定、排序、通知和删除确认（`#/users`） |
| `views/queues.js` | 调度队列页 + 定时/任务弹窗 |
| `views/dispatch.js` | 调度中心（2 秒轮询，只更新运行面板 DOM） |
| `views/history.js` | 历史列表 + 详情弹窗 |
| `views/plugins.js` | 数据化专项插件与 managed-code 插件列表、启停状态 |
| `views/settings.js` | 系统设置页 + Webhook/SMTP 内置通知渠道 |
| `views/dashboard.js` | 仪表盘（3 秒轮询） |
| `core/api.js` | 请求封装（JSON/错误/AbortController 生命周期联动） |
| `core/dom.js` | `$` / `$$` 查询 |
| `core/format.js` | 格式化/转义/徽章模板 |
| `core/forms.js` | 共享表单模板（pageHeader/valueField/selectField） |
| `core/modal.js` | 单模态弹窗（焦点陷阱/Esc/焦点恢复） |
| `core/ui.js` | 页面渲染/导航/Toast/主题/倒计时 |
| `core/state.js` | 路由生命周期（enterPage/isCurrent/schedule/trackController）+ 跨域缓存（scripts/queues/users/settings） |
| `core/limits.js` | 跨视图共享的约束警告层：加载 `/api/limits`、忽略状态持久化、alertdialog 警告层与「知道了/不再提醒」分发 |
| `core/dnd.js` | 通用拖拽排序组件（无业务依赖）：`initDndList(container, { onDrop(ids) })`——容器内 `[data-dnd-id]` 项 + `.drag-handle` 把手，Pointer Events 统一鼠标/触屏；拖拽结束 DOM 重排后回调视图提交全量顺序；插入位置判定不得跳过带 `.dnd-drop-before` 标记的项 |

### 新增交互的落点

1. 在对应域视图新增导出函数 + 加入该视图的 `actions` 对象（`data-action` 名与处理器映射）。
2. 视图模板使用 `data-action` + 稳定的 `data-testid`（e2e 契约）。
3. 需要路由的新页面：视图导出 `pageXxx(token)`，在 `app.js` 的 `routes` 表注册一行；二级路由在 `route()` 特判分支转发。
4. 列表拖拽排序：渲染容器 + `[data-dnd-id]` 项 + `.drag-handle` 把手 → `initDndList(container, { onDrop })` → 视图把可见项重排进全量列表后提交 `PUT /api/{scripts|queues}/order`（body `{ ids: [...] }`，全量名单一致校验）或用户沿用 `PUT /api/scripts/{id}/users/order`（`{ names }`）；**弹窗内（队列编辑弹窗的定时列表/任务列表）**：onDrop 按 `data-dnd-id`（渲染下标）重排 `queueDraft` 数组，任务卡重排时同步重设 `index`，sync 按元素携带下标（`data-ts-idx`/`data-task-idx`）写回原数组项。

## 插件扩展指南

数据化专项插件采用 `plugins/<名称>/plugin.json + data/`，managed-code 插件采用同一目录下的 `plugin.json + entryAssembly`。通知和模拟器属于宿主内置基础设施，不再拥有插件身份。数据化 capability 通过 `plugin.json` 的 `capabilities` 数组登记；旧 `supportsEmulator: true` 自动映射为 `emulator` key。

### 插件分类

| 类别 | 形态 | 职责 | 启用语义 |
|---|---|---|---|
| managed-code 插件 | 独立项目 + `NexusPipeline.Plugin.Abstractions` API v1 + manifest | 通过 Logger/Config/Secrets/Notifications/Scheduler 实现联网、签到等主动任务 | 默认禁用；启用后重启加载，API 不兼容或初始化失败会进入对应运行态 |
| 数据化专项插件 | `plugins/<名称>/plugin.json + data/`（`DataSpecializedPlugin` 扫描注册） | 接管专项脚本实例配置：`Resolve(rootPath)` 按 `data/resolve.json` 推导主程序/参数/配置/日志/判断脚本 | 默认启用；偏好写入 `AppSettings.PluginPreferences`，重启后应用 |

> **通知通道**：Webhook/SMTP 由宿主 `NotificationDispatcher` 并行发送；代码插件通过 `IPluginNotificationService` 提交 `PluginNotification` DTO，不能访问宿主设置或 sender。单个通道异常仅记警告，不影响其余通道。

### Capability 扩展约束

- 数据插件 capability 通过 key 登记；managed-code 插件只通过 API v1 服务端口工作，宿主不把后台任务 capability 当作专项脚本选择器。
- 数据化插件可在 `plugin.json` 增加 `capabilities: ["..."]`；未知 key 由宿主登记但不自动赋予业务语义。现有 `supportsEmulator` 仍兼容并映射为 `emulator`。
- `PluginSummary` 只描述展示/发现所需的元数据；Web 状态接口继续单独生成 `supportsEmulator`，因此不会破坏现有前端响应结构。
- Plugin API v1 只提供显式 `IPluginHostContext` 服务端口；配置与 DPAPI 密钥分文件存储于 `config/plugins/`，managed-code 插件停止时后台任务统一取消。

### 编写插件

插件的 manifest、`resolve.json`、判断脚本、配置还原描述和默认配置模板组成独立契约。详细字段、示例、路径模板、判断脚本输入输出、配置还原 DSL 与部署约束统一维护在 [plugins/README.md](../plugins/README.md)；本文件只说明宿主模块边界和代码定位。

- managed-code 插件实现独立 API 项目的 `INexusPlugin` 生命周期，并通过 `IPluginHostContext` 使用宿主提供的日志、配置、密钥、通知和后台任务端口。
- 数据化专项插件由 `plugins/<名称>/plugin.json + data/` 描述，`DataSpecializedPlugin` 负责发现和注册，宿主在保存脚本实例时固化解析结果。
- 通知、模拟器和执行准入属于宿主能力；插件通过明确 capability 或公开 API 端口接入，不直接访问宿主组合根、领域模型或 Web 层。

## 功能定位指南（找代码）

| 想找什么 | 去哪里 |
|---|---|
| 某 API 路由的实现 | `src/Web/ApiXxxHandler.cs`（`[ApiRoute]` 特性注册，见 `WebServer.Routes`） |
| 命令行某菜单 | `src/Cli/` 对应菜单类 |
| 脚本运行流程/重试/日志监控 | `src/Services/Execution/ExecutionCoordinator.cs`、`src/Services/RunSession.cs`（状态）、`src/Services/Execution/AttemptRunner.cs`、`src/Services/Execution/RetryPolicy.cs`、`src/Services/Execution/RunBudget.cs`、`src/Services/Execution/RunAttemptFinalizer.cs`、`src/Services/LogMonitor.cs`（日志增量读取/替换检测）、`src/Persistence/LogPattern.cs`（日志路径格式解析） |
| 自定义完成标志（关键字/判断脚本） | `src/Services/Judgement/SessionJudge.cs`（判定状态机）、`src/Services/Execution/AttemptRunner.cs`（尝试执行/触发时机）、`src/Services/Judgement/JudgeScriptRunner.cs`（脚本执行器）、`src/Utilities/TextRules.cs`（`KeywordRule`） |
| 判断脚本边界与配置替换 | `src/Services/UserConfigManager.cs`（门面）、`src/Services/Configuration/ConfigRunSession.cs`（运行配置生命周期）、`src/Services/Configuration/ConfigurationTransaction.cs`（事务原语）、`src/Services/ConfigSwapSession.cs`（替换/同步 façade）、`src/Services/ConfigSwap/ConfigSwapRecovery.cs`（恢复）、`src/Services/Judgement/JudgeScriptRunner.cs`（`ResolveWithin` 防逃逸） |
| 队列调度触发 | `src/Services/Scheduling/Scheduler.cs` |
| 通知发送（Webhook/SMTP） | `src/Services/Notification/NotificationDispatcher.cs`、`src/Services/Notification/NotificationFormatter.cs`、`src/Services/WebhookSender.cs`、`src/Services/SmtpSender.cs` |
| 页面渲染/表单 | `wwwroot/views/` 对应域文件 |
| 前端交互绑定 | 视图 `actions` 对象 → `app.js` 合并分发 |
| 配置读写/加密 | `src/Persistence/ConfigStore.cs`、`src/Persistence/SecretStore.cs` |
| 历史记录格式 | `src/Services/History/HistoryService.cs`、`src/Models/RunRecord.cs` |

## 数据流速览

```
Web 请求 → WebServer → ApiXxxHandler → ExecutionCommands/核心服务 → DataStore/Logger
CLI 菜单 / Scheduler → Application Command → DispatchCenter → ExecutionPlanBuilder → ExecutionValidator → ExecutionAdmissionPolicy/ExecutionStateStore → ExecutionRunner
运行结束 → ExecutionRunner → INotificationService → NotificationDispatcher → Webhook/SMTP；managed-code 插件 → IPluginNotificationService → NotificationDispatcher；同时向 ExecutionStateStore 提交完成意图
```

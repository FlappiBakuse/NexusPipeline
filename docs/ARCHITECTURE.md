# NexusPipeline 架构说明

本文件是开发者的定位指南：模块边界、依赖方向、如何定位功能、如何扩展插件。v0.2.0 起生效。
核心设计理念与运行流程见 [DESIGN.md](DESIGN.md)；版本历史见 [CHANGELOG.md](../CHANGELOG.md)。

> v0.7.9 扩展性治理：运行总预算、配置交换运行作用域、attempt 收尾和插件 capability 注册均有独立的 internal 边界；本轮不新增用户可见业务能力，也不改变现有 API、磁盘格式或数据化插件旧字段语义。
> v0.8.0 后端架构强化：应用入口/启动流程、运行状态存储、配置交换恢复分别收敛到 `Application/`、`Services/Execution/`、`Services/ConfigSwap/`；本轮仍保持现有 API、磁盘布局和运行语义兼容。
> v0.8.1 后端领域边界收敛：`RunSession` 仅保存一次运行状态，`ExecutionCoordinator` 负责运行级编排，`AttemptRunner`/`RetryPolicy`/`CleanupManager`/`ResultCollector` 分别承载尝试执行、重试、资源清理和结果收集；配置事务、通知/模拟器 capability 与 Application Command 均有独立 internal 边界，保持现有外部行为兼容。
> v0.8.2 后端架构第三次优化：`DispatchCenter` 收敛为执行门面，`ExecutionValidator`、`ExecutionRunner`、`SystemActionExecutor` 分别承载门禁校验、后台生命周期和系统完成操作；脚本/队列/用户/设置/历史/执行/通知/插件能力通过 `Application/Abstractions/` 显式端口连接，保留共享列表和旧兼容入口。

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
│   ├── Extensibility/  中立扩展契约与宿主服务（NexusPipeline.Extensibility，internal）
│   ├── Web/            HTTP 层（NexusPipeline.Web）
│   ├── Cli/            命令行层（NexusPipeline.Cli）
│   └── Plugins/        插件契约与内置插件（NexusPipeline.Plugins）
├── wwwroot/            前端（零构建 ES modules，浏览器直接加载）
│   ├── app.js          路由 + 事件委托（唯一入口）
│   ├── core/           平台层（与业务无关的通用能力）
│   ├── views/          业务视图（一域一文件）
│   └── effects/        独立视觉效果
├── tests/
│   ├── NexusPipeline.Tests/  xUnit 单元测试（通过 InternalsVisibleTo 访问 internal 契约）
│   └── e2e/                  Playwright 端到端测试（黑盒，@playwright/test 框架）
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
- **Plugins 通过宿主内置契约接口（`IPlugin` / `INotifyChannel` / `PluginContext`）交互**；跨模块的 capability/profile 契约位于 `Extensibility/`，数据化专项插件（`DataSpecializedPlugin`）为纯数据驱动，宿主只读其目录文件。
- **依赖方向顺沿命名空间**：Models 无依赖；Services 依赖 Models/Persistence/Utilities；Persistence 依赖 Utilities。
- **已知偏差（如实记录，见 KNOWN-ISSUES.md KN-49）**：v0.8.2 已将执行核心、调度器和配置编辑的插件能力消费改为显式端口，并将大部分运行期数据读取改为 `Application/Abstractions/` 仓储；`ConfigSwapRecovery` 的损坏标记兼容恢复仍保留 `RuntimeContext` 查找脚本，属于启动/恢复兼容路径。`Utilities/Logger` 读取 `RuntimeContext.Instance.Settings`（Utilities → 根命名空间）也保持不变。新服务不得新增这类依赖。

### 关键类职责

| 类 | 位置 | 职责 |
|---|---|---|
| `Program` | src/Application/ProgramEntry.cs | 进程入口，仅转交 `ApplicationHost.Run(args)` |
| `ApplicationHost` | src/Application/ApplicationHost.cs | 进程级初始化后的命令分发与 CLI 处理 |
| `RuntimeInitializer` | src/Application/RuntimeInitializer.cs | 管理员权限、旧配置迁移、约束/设置/数据加载；不启动服务 |
| `StartupPipeline` | src/Application/StartupPipeline.cs | 常驻服务、网页模式与重启的单实例互斥、恢复、Web/托盘生命周期 |
| `Bootstrap` | src/Bootstrap.cs | 服务启动/停止编排、Web 端口重试 |
| `RuntimeContext` | src/RuntimeContext.cs | 组合根（壳式 DI，v0.5.0+）：内部 ServiceProvider 注册各领域服务和 `Application/Abstractions/` 运行时适配器，外部访问方式不变；`Resolve<T>()` 服务解析出口 |
| `IScriptRepository` / `IQueueRepository` / `IUserRepository` | src/Application/Abstractions/、src/Application/Repositories/ | 执行/调度域读取脚本、队列和启用用户的显式端口；运行时适配器保留现有共享列表、锁和深拷贝快照语义 |
| `ISettingsProvider` / `IHistoryStore` | src/Application/Abstractions/、src/Application/Repositories/、src/Services/History/ | 设置读取与历史写入端口，避免服务直接反向查组合根或具体历史文件实现 |
| `IExecutionService` / `INotificationService` / `IPluginCapabilityResolver` | src/Application/Abstractions/ | Web、Scheduler、执行域和插件能力消费端口；具体实现仍由现有 `ExecutionCommands`、`NotificationDispatcher`、`PluginManager` 提供 |
| `ExecutionCommands` | src/Application/Commands/ExecutionCommands.cs | Web、Scheduler 与常驻服务 CLI 通道共享的启动/取消应用命令入口 |
| `DataStore` | src/Persistence/DataStore.cs | 持久化仓储（scripts/queues JSON 读写） |
| `DispatchCenter` | src/Services/DispatchCenter.cs | 兼容执行门面：执行门禁、状态登记、取消和入口参数编排；不再承载后台运行流程 |
| `ExecutionValidator` | src/Services/Execution/ExecutionValidator.cs | 脚本/队列存在性、用户门禁、长时混排、进程冲突和任务计数校验 |
| `ExecutionRunner` | src/Services/Execution/ExecutionRunner.cs | 脚本/队列后台生命周期、用户串行、历史落盘、通知和完成操作调度 |
| `SystemActionExecutor` | src/Services/Execution/SystemActionExecutor.cs | sleep/reboot/shutdown 完成操作的 pending 单槽位、倒计时和取消语义 |
| `ExecutionCoordinator` | src/Services/Execution/ExecutionCoordinator.cs | 一次运行级编排：用户顺序、重试循环、配置事务和运行收尾；后台任务与历史/通知外层边界由 `ExecutionRunner` 承载 |
| `RunSession` | src/Services/RunSession.cs | 一次运行的状态对象：元数据、预算、日志收集、配置事务状态和回调；不再拥有 `RunAsync` 流程 |
| `AttemptRunner` | src/Services/Execution/AttemptRunner.cs | 单次尝试执行入口；协调器通过该边界调用前/后置脚本与监控执行 |
| `RetryPolicy` / `ResultCollector` | src/Services/Execution/ | 普通失败重试判定、日志容量/按尝试分段收集 |
| `CleanupManager` / `RunAttemptFinalizer` | src/Services/Execution/ | 执行域清理门面与 Windows 进程/游戏清理基础设施 |
| `ExecutionStateStore` | src/Services/Execution/ExecutionStateStore.cs | 线程安全管理运行中/已结束任务与待执行系统操作，保留原子防重入和 100 条历史上限 |
| `RunningExecution` | src/Services/Execution/RunningExecution.cs | 单次运行的可观察状态、记录快照和日志尾部 |
| `RunBudget` | src/Services/Execution/RunBudget.cs | 统一整个运行（含重试、前置/后置脚本）的 elapsed/remaining/命令超时上限；保留 `NEXUS_TIME_SCALE` 语义 |
| `ConfigRunSession` | src/Services/Configuration/ConfigRunSession.cs | 运行期间配置事务的收尾编排：固定同步、替换还原、script 清理和现场恢复顺序 |
| `ConfigurationTransaction` | src/Services/Configuration/ConfigurationTransaction.cs | 配置 prepare/retry/sync/replace/rollback 原语边界，兼容现有 `ConfigSwap` 磁盘协议 |
| `RunAttemptFinalizer` | src/Services/Execution/RunAttemptFinalizer.cs | attempt 级脚本进程树、游戏/模拟器清理基础设施；承载失败/取消/强制关闭策略，不改变既有清理时序 |
| `SessionJudge` | src/Services/Judgement/SessionJudge.cs | 完成判定策略状态机（v0.5.0 拆分）：判断脚本/关键字两模式，判定状态与输入 |
| `JudgeScriptRunner` | src/Services/Judgement/JudgeScriptRunner.cs | 判断脚本执行器：输入 JSON 生成（脚本字段+用户+config（只读）与 script（可读写）目录全递归文件清单+**本次尝试日志段**（v0.5.2+，超过 4MB 截断尾部并置 logTruncated））、JS 内置 Jint 引擎（注入 `__NEXUS_INPUT__`/`nexus.readFile`（限 config/script 范围 2MB）/`nexus.writeFile`（限 script 目录防逃逸）/`nexus.listFiles()`/`console.log`）、Python 系统解释器进程、30 秒超时、stdout 尾行 JSON 解析（含 `replaceConfigs`） |
| `LogMonitor` | src/Services/LogMonitor.cs | 日志增量读取器：追加/截断（v0.6.9+：部分截断从新尾续读、归零从头读）/替换（FileId 对比 `GetFileInformationByHandle` 卷序列号+文件索引，v0.5.2+ 根治句柄残留）三形态；忽略运行前已有内容（末尾读） |
| `UserConfigManager` | src/Services/UserConfigManager.cs | 配置储存对外门面（v0.5.0 拆分），实现分层见 `ConfigSwapPrimitives`/`ConfigSwapSession`/`ConfigSwapPaths`；自动更新配置同步（`SyncConfigToStore`，v0.7.6）转发 |
| `ConfigSwapPrimitives` | src/Services/ConfigSwapPrimitives.cs | 配置交换文件原语层：安全移动/原子替换/重试/跨进程互斥/形态判断 |
| `ConfigSwapSession` | src/Services/ConfigSwapSession.cs | 配置交换兼容 façade：replaceConfigs、自动更新配置事务镜像与公共会话入口；恢复职责转交 `ConfigSwapRecovery` |
| `ConfigSwapRecovery` | src/Services/ConfigSwap/ConfigSwapRecovery.cs | `.session` 自愈、启动扫描、孤儿进程延迟重试、模板/原配置还原；不改变原磁盘布局 |
| `ConfigSessionMark` / `EditSession` | src/Services/ConfigSwap/ | 配置会话持久化标记与 Web 编辑会话状态模型 |
| `ConfigSwapPaths` | src/Services/ConfigSwapPaths.cs | 配置数据目录管理：data/{脚本Id}/{用户名} 子目录定位与清理 |
| `LogPattern` | src/Persistence/LogPattern.cs | 日志路径格式解析（日期占位符/通配符严格匹配，无格式外猜测） |
| `Scheduler` | src/Services/Scheduling/Scheduler.cs | 定时/启动时触发队列；通过队列仓储、历史、设置、执行端口和 `ExecutionValidator` 工作 |
| `HistoryService` | src/Services/History/HistoryService.cs | 历史记录读写与清理 |
| `NotificationDispatcher` | src/Services/Notification/NotificationDispatcher.cs | 通过 `INotificationChannelProvider` 分发脚本/队列通知，隔离具体插件实现 |
| `WebServer` | src/Web/WebServer.cs | HTTP 骨架：监听、静态文件（v0.6.9+：nosniff/Referrer-Policy/CSP 安全头）、特性路由表（[ApiRoute] 反射扫描注册，v0.5.0+）；远程令牌校验 v0.6.9+ 改常量时间比较 |
| `HttpHelper` | src/Web/HttpHelper.cs | 通用 HTTP 辅助（写 JSON/404/405/解析请求体）；v0.6.9+ 移除 `ReadLogTail`（`/api/logs` 孤儿 API 删除） |
| `ApiXxxHandler` | src/Web/ | 每资源一个 handler，`[ApiRoute("资源名")]` 标注，路由表自动注册（v0.5.0+） |
| `MainMenu` + 菜单类 | src/Cli/ | 命令行交互（主菜单/脚本/队列/调度/历史/插件/设置/通知渠道） |
| `PluginCapabilityRegistry` | src/Plugins/PluginCapabilityRegistry.cs | capability 的类型化注册/查询与数据插件 key 注册；`LoadAll` 清空后重建，避免重复能力 |
| `PluginManager` | src/Plugins/PluginManager.cs | 插件发现/加载/开关和兼容 façade；通用 capability 查询委托 registry，元数据投影不携带业务能力字段 |
| `PluginContracts` | src/Extensibility/PluginContracts.cs | `IPluginCapability`、通知/profile/模拟器能力契约、`ScriptProfile` 与显式 `PluginHostServices`；全部 internal |
| `Logger` | src/Utilities/Logger.cs | 分级日志（DEBUG/INFO/WARN/ERROR/FATAL），阈值过滤，控制台着色 |

### public / internal 约定

- 仅以下为 **public**（对外契约）：`Program`（入口）与领域模型 `AppSettings`/`ScriptInstance`/`ScriptUser`/`DispatchQueue`/`QueueTask`/`QueueTimeSet`/`RunRecord`/`RunAttempt`。v0.6.3 起插件契约均为宿主内置（`IPlugin`/`INotifyChannel`/`PluginContext`/`ScriptProfile`/`IPluginCapability` 一律 internal），不再对外提供 DLL 插件契约。
- 其余全部 `internal`：新增类型默认 internal，除非它属于契约清单。

### 新增 API 的落点

- HTTP 路由：在 `src/Web/` 新增或扩展 `ApiXxxHandler`，类上标注 `[ApiRoute("资源名")]`（子路由标注在方法上，如 `cancel`）；`WebServer` 启动时反射扫描自动注册，**无需改路由表**（v0.5.0+）。
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
| `views/users.js` | 用户管理二级页（`#/scripts/{id}/users`） |
| `views/queues.js` | 调度队列页 + 定时/任务弹窗 |
| `views/dispatch.js` | 调度中心（2 秒轮询，只更新运行面板 DOM） |
| `views/history.js` | 历史列表 + 详情弹窗 |
| `views/plugins.js` | 插件列表 + `#/plugins/{name}` 配置二级页（密钥字段并入「保存设置」提交，仅非空提交） |
| `views/settings.js` | 系统设置页 |
| `views/dashboard.js` | 仪表盘（3 秒轮询） |
| `core/api.js` | 请求封装（JSON/错误/AbortController 生命周期联动） |
| `core/dom.js` | `$` / `$$` 查询 |
| `core/format.js` | 格式化/转义/徽章模板 |
| `core/forms.js` | 共享表单模板（pageHeader/valueField/selectField） |
| `core/modal.js` | 单模态弹窗（焦点陷阱/Esc/焦点恢复） |
| `core/ui.js` | 页面渲染/导航/Toast/主题/倒计时 |
| `core/state.js` | 路由生命周期（enterPage/isCurrent/schedule/trackController）+ 跨域缓存（scripts/queues/settings） |
| `core/dnd.js` | 通用拖拽排序组件（v0.6.8+，无业务依赖）：`initDndList(container, { onDrop(ids) })`——容器内 `[data-dnd-id]` 项 + `.drag-handle` 把手，Pointer Events 统一鼠标/触屏；拖拽结束 DOM 重排后回调视图提交全量顺序；插入位置判定不得跳过带 `.dnd-drop-before` 标记的项（否则落位震荡） |

### 新增交互的落点

1. 在对应域视图新增导出函数 + 加入该视图的 `actions` 对象（`data-action` 名与处理器映射）。
2. 视图模板使用 `data-action` + 稳定的 `data-testid`（e2e 契约）。
3. 需要路由的新页面：视图导出 `pageXxx(token)`，在 `app.js` 的 `routes` 表注册一行；二级路由在 `route()` 特判分支转发。
4. 列表拖拽排序（v0.6.8+，弹窗内 v0.6.10+）：渲染容器 + `[data-dnd-id]` 项 + `.drag-handle` 把手 → `initDndList(container, { onDrop })` → 视图把可见项重排进全量列表后提交 `PUT /api/{scripts|queues}/order`（body `{ ids: [...] }`，全量名单一致校验）或用户沿用 `PUT /api/scripts/{id}/users/order`（`{ names }`）；**弹窗内（队列编辑弹窗的定时列表/任务列表）**：onDrop 按 `data-dnd-id`（渲染下标）重排 `queueDraft` 数组即可（时间卡无 Index 字段、任务卡重排时同步重设 `index`），sync 按元素携带下标（`data-ts-idx`/`data-task-idx`）写回原数组项，DOM 顺序与数组顺序脱钩后仍正确。

## 插件扩展指南

v0.6.3 起专项插件为**数据化目录形态**（`plugins/<名称>/plugin.json + data/`），无需编译；内置 C# 插件包含 NotifyPlugin（通知推送）与 EmulatorAdapterPlugin（模拟器适配）。v0.7.9 起插件身份/元数据与 capability 分离：C# capability 通过 `PluginCapabilityRegistry` 按接口查询，数据化 capability 通过 `plugin.json` 的 `capabilities` 数组登记；旧 `supportsEmulator: true` 自动映射为 `emulator` key。

### 插件分类

| 类别 | 形态 | 职责 | 启用语义 |
|---|---|---|---|
| 通用插件 | 内置 C#（`IPlugin`/能力接口/`PluginContext`） | 为程序添加能力（内置「通知推送」和模拟器适配，`PluginManager.DiscoverBuiltIn` 注册） | 内置白名单 `EnabledPlugins`（默认 notify），只可禁用不可删除 |
| 数据化专项插件 | `plugins/<名称>/plugin.json + data/`（`DataSpecializedPlugin` 扫描注册） | 接管专项脚本实例配置：`Resolve(rootPath)` 按 `data/resolve.json` 推导主程序/参数/配置/日志/判断脚本 | 外部默认启用，显式禁用记入 `DisabledPlugins`（重启后仍禁用） |

> **通知通道（v0.4.4+，v0.8.1 边界收敛）**：`INotifyChannel` 为**多通道并存**语义——`NotificationDispatcher` 通过 `INotificationChannelProvider` 分发至全部已启用通道（NotifyPlugin 内部按 Webhook/SMTP 独立开关并行双发）。单个通道异常仅记警告，不影响其余通道；具体插件类型不再由 `DispatchCenter` 直接引用。

### Capability 扩展约束（v0.7.9）

- 新增 C# 能力时新增 `IPluginCapability` 子接口并由插件实现；加载器只做一次通用注册，消费者通过 `PluginManager.GetCapabilities<T>()` 或 `HasCapability` 查询，不在 `PluginManager` 增加新的类型分支。
- 数据化插件可在 `plugin.json` 增加 `capabilities: ["..."]`；未知 key 由宿主登记但不自动赋予业务语义。现有 `supportsEmulator` 仍兼容并映射为 `emulator`。
- `PluginSummary` 只描述展示/发现所需的元数据；Web 状态接口继续单独生成 `supportsEmulator`，因此不会破坏现有前端响应结构。
- `PluginContext` 通过显式宿主服务读取设置、重载设置和已注册服务；插件配置/密钥文件路径与存储格式不变。

### 编写数据化专项插件（示例：`plugins/bettergi/`）

```
plugins/bettergi/
├── plugin.json               # 根文件：元数据 + 引用 data 文件（初始化专项插件）
└── data/
    ├── resolve.json          # 推导配置：require 校验 + paths 模板
    ├── judge.js              # 判断脚本（.js = Jint / .py = 系统 python.exe）
    └── config-template/      # 可选：默认配置模板目录（编辑会话生成用）
        └── NexusPipeline.json
```

```json
// plugin.json
{
  "name": "bettergi",
  "displayName": "BetterGI",
  "gameName": "原神",
  "description": "BetterGenshinImpact 专项脚本实例配置接管",
  "version": "0.1.0",
  "resolve": "data/resolve.json",
  "judgeScript": "data/judge.js",
  "configTemplate": "data/config-template"
}
```

```json
// data/resolve.json（March7th 示例：管理端 Launcher + 执行端 Assistant 上级目录搜索）
{
  "require": [
    { "var": "launcher", "file": "March7th Launcher.exe" },
    { "var": "assistant", "file": "March7th Assistant.exe", "searchUpward": true }
  ],
  "paths": {
    "mainExe": "{launcher}",
    "args": "{rel:assistant}",
    "configPath": "config.yaml",
    "logPath": "logs/{YYYY-MM-DD}.log"
  }
}
```

- `require` 全部满足才推导成功；`searchUpward: true` 时逐级向上搜索（最多 4 层）；`{var}` = 绑定文件绝对路径、`{rel:var}` = 相对脚本根目录的相对路径；无占位符的路径字段按相对脚本根目录拼接、`args` 原样返回。
- 宿主在保存专用脚本实例时调用 `Resolve` 固化快照（POST/PUT 时覆盖 MainExe/Args/ConfigPath/LogPath/JudgeScript 与语言，`ConfigTemplateDir` 仅编辑会话现取不落盘）；前端简化弹窗通过 `POST /api/scripts/probe` 预校验。
- `config-template/` 目录在编辑用户配置会话中 ConfigPath 不存在时整体复制到配置位置，复制清单随 `.session` 标记持久化（cancel/重启恢复按清单清理）。
- 完整 schema 见 `plugins/README.md`。

> **MaaEnd 专项要点（v0.6.1，`plugins/maaend/`）**：主程序 `MaaEnd.exe`（MXU 客户端改名）以 `--autostart --quit-after-run` 启动（任务运行完成时进程自动退出）；配置目录 `config/`（`mxu-MaaEnd.json` 为实例/任务核心配置），v0.6.4 起**提供默认配置模板**（`data/config-template/` 含 `mxu-MaaEnd.json` 与 `maa_option.json`，编辑用户配置会话时 config 目录不存在则整体复制生成——目录型 ConfigPath 模板复制到 ConfigPath 本身，恢复按相对父目录清单精确清理）；日志 `debug/{YYYY-MM-DD}-*.log`（前端写入，文件名带 `-n` 自增序号、启动时自动清理旧文件，通配取最新修改 = 当前会话）。判断脚本按「最后一个启用任务的任务完成/任务失败判定行」收尾（MXU 无运行记录机制、无天然选择性补做），失败任务改写 `mxu-MaaEnd.json`（全部 `enabled=false`、失败任务 `enabled=true`）经 `replaceConfigs` 触发选择性重试；启用任务判定**只按 `enabled===true`**（与 MXU 运行分发一致，`enabledByController` 仅 UI 缓存不参与分发）。**v0.7.6 还原描述**：判断脚本首次触发时读取 config 提取初始任务启停映射（array 型 `instances[{index}].tasks`）写 `script/config-restore.json`（跨尝试只写一次），宿主收尾同步快照前按描述还原启停（初始启停 + 运行后计数保留）。

> v0.6.3 起插件契约为宿主内置（`IPlugin`/`INotifyChannel`/`PluginContext`/`ScriptProfile` 均 internal），不再对外提供 DLL 插件契约与 `ISpecializedScriptPlugin`。

## 功能定位指南（找代码）

| 想找什么 | 去哪里 |
|---|---|
| 某 API 路由的实现 | `src/Web/ApiXxxHandler.cs`（`[ApiRoute]` 特性注册，见 `WebServer.Routes`） |
| 命令行某菜单 | `src/Cli/` 对应菜单类 |
| 脚本运行流程/重试/日志监控 | `src/Services/Execution/ExecutionCoordinator.cs`、`src/Services/RunSession.cs`（状态）、`src/Services/Execution/AttemptRunner.cs`、`src/Services/Execution/RetryPolicy.cs`、`src/Services/Execution/RunBudget.cs`、`src/Services/Execution/RunAttemptFinalizer.cs`、`src/Services/LogMonitor.cs`（日志增量读取/替换检测）、`src/Persistence/LogPattern.cs`（日志路径格式解析） |
| 自定义完成标志（关键字/判断脚本） | `src/Services/Judgement/SessionJudge.cs`（判定状态机）、`src/Services/Execution/AttemptRunner.cs`（尝试执行/触发时机）、`src/Services/Judgement/JudgeScriptRunner.cs`（脚本执行器）、`src/Utilities/TextRules.cs`（`KeywordRule`） |
| 判断脚本边界与配置替换 | `src/Services/UserConfigManager.cs`（门面）、`src/Services/Configuration/ConfigRunSession.cs`（运行配置生命周期）、`src/Services/Configuration/ConfigurationTransaction.cs`（事务原语）、`src/Services/ConfigSwapSession.cs`（替换/同步 façade）、`src/Services/ConfigSwap/ConfigSwapRecovery.cs`（恢复）、`src/Services/Judgement/JudgeScriptRunner.cs`（`ResolveWithin` 防逃逸） |
| 队列调度触发 | `src/Services/Scheduling/Scheduler.cs` |
| 通知发送（Webhook/SMTP） | `src/Services/Notification/NotificationDispatcher.cs`、`src/Services/WebhookSender.cs`、`src/Services/SmtpSender.cs`、`src/Plugins/NotifyPlugin.cs` |
| 页面渲染/表单 | `wwwroot/views/` 对应域文件 |
| 前端交互绑定 | 视图 `actions` 对象 → `app.js` 合并分发 |
| 配置读写/加密 | `src/Persistence/ConfigStore.cs`、`src/Persistence/SecretStore.cs` |
| 历史记录格式 | `src/Services/History/HistoryService.cs`、`src/Models/RunRecord.cs` |

> **v0.5.0 分层变更**：核心域按子域重组（`Models/`、`Services/`、`Persistence/`、`Utilities/` 对应命名空间）；Web 路由改特性路由；`RuntimeContext` 引入壳式 DI（`ServiceProvider` + `Resolve<T>()`，外部访问方式不变）；`RunSession` 判定策略拆出 `SessionJudge`；`UserConfigManager` 拆为门面 + 原语/会话恢复/数据目录三层。public 契约清单不变，extensions 三插件工程对齐后仍可编译。v0.6.3 起专项插件数据化（extensions/ 工程移除，见「插件扩展指南」）。
>
> **v0.5.1 变更**：插件级配置（`PluginContext.GetConfig/SetConfig/GetSecret/SetSecret`，落盘 `config/plugins/<插件名>.json`，DPAPI `enc:` 前缀）；e2e 迁移 @playwright/test（tests/ 按域 7 文件 46 用例，旧 test.mjs 移除）；`core/limits.js` 归位 `views/limits.js`。
>
> **v0.5.2 变更**：日志监控文件替换检测改 FileId（`LogMonitor.FileReplaced`，根治 move+重建场景句柄残留）；初始监控严格 fresh（`LastWriteTime ≥ attemptStart`）；判断脚本输入按尝试切片（本次尝试日志段，跨尝试不污染判定）；RunAsync finally 还原顺序调整（先配置替换还原、后配置交换还原）。

## 数据流速览

```
Web 请求 → WebServer → ApiXxxHandler → ExecutionCommands/核心服务 → DataStore/Logger
CLI 菜单 / Scheduler → Application Command → DispatchCenter → ExecutionValidator → ExecutionRunner
运行结束 → ExecutionRunner → INotificationService → INotificationChannelProvider → INotifyChannel 实现 → Webhook/SMTP
```

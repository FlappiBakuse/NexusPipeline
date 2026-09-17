# 产品边界与模块

## 1. 设计理念

NexusPipeline 定位为**本地游戏自动化脚本管家**：一个常驻托盘的 Windows 服务，代替用户按计划启动/重试/关闭任意外部脚本（exe / bat / cmd 等），并管理多账号配置、判定脚本运行结果、推送通知。核心理念：

- **本地优先、少外部依赖**：所有产品能力内置于单个 exe（.NET 8 WinForms 托盘 + HttpListener + 构建后的静态 Web UI）。Node/npm 只属于开发和发布环境，运行机器不需要前端构建环境；发布物为框架依赖的单文件，运行机器需安装 .NET 8 Desktop Runtime。
- **直接接管脚本进程**：宿主以管理员身份创建进程、捕获输出、监控日志、强制清理进程树，脚本自身无需任何改造；bat 经 `cmd /d /s /c` 包装以规避 ShellExecute 弹窗陷阱。
- **多用户配置隔离（配置交换）**：全局用户通过脚本绑定参与多个脚本实例；每个绑定各存一份配置快照，运行前把绑定快照交换到 configPath，运行后还原现场。数据保全序：**original（原配置）> config（运行时生效）> store（用户快照，可重建）**。
- **判定交给用户**：运行结果由「完成判定」驱动——优先判断脚本（用户自写 JS/Python，专用插件判定由当前 profile 指向的插件脚本驱动），其次成功/失败关键字；判断脚本允许返回 `success / partial / failed`，其中 `partial` 只能由判断脚本主动产生，未配置任何判定时按「进程自行退出」判成功。判定输入为**本次尝试日志段**，跨尝试互不污染。
- **日志是判定输入**：宿主可靠监控脚本**日志文件**，将新增日志提供给关键字判定和判断脚本；日志文本、退出码、尝试次数及重试本身不拥有覆盖最终业务状态的权力。日志监控对文件「重建/截断/追加」三种形态都必须可靠；同路径文件替换使用**文件身份（FileId）检测**，避免旧句柄继续指向已归档文件。
- **失败可重试、崩溃可自愈**：每次尝试失败按 `MaxAttempts` 自动重试；判断脚本可返回 `replaceConfigs` 替换配置后再试；配置交换用 `.session` 标记 + swap-backup 双保险，宿主启动时或后台延迟自动还原。
- **可扩展插件**：managed-code 插件通过独立 `NexusPipeline.Plugin.Abstractions` Plugin API v1.8 使用宿主通用用户数据、声明式 UI、作用域数据、二进制资产存储、历史展示、插件 Web API、用户列表徽章、用户运行事件、HTTP、日志、通知、调度、本地化、模拟器支持 provider 与 SMTP 收件人覆盖端口；启用且精确匹配的插件可通过独立 Frontend API 1.5 加载构建后的 ES module/CSS，扩展页面路由、导航、slot、通用外观表面、运行画面 sidecar 和插件词典，并通过 `nxp-*` Native Custom Elements 复用宿主公共控件；专项插件继续采用**数据化目录形态**（`plugin.json` + `data/` 推导配置与判断脚本），数据 capability 通过 `capabilities` key 登记。
- **插件分发与运行解耦**：插件仓库以固定官方 `catalog.json` 提供版本和 SHA256，安装包在本地完成校验后以 pending 事务跨重启交换；宿主更新只替换宿主文件，用户插件目录持续保留。
- **宿主网络出口可控**：外部 HTTP 请求统一经过可即时读取设置的网络出口，支持无代理、系统代理和自定义 HTTP/HTTPS 代理；本机控制面、MCP、SMTP 与插件子进程保持原有网络边界。



## 2. 核心概念

| 概念 | 说明 |
|---|---|
| 脚本实例（ScriptInstance） | 一次可运行的脚本单元：主程序/参数/根目录/配置路径/日志路径/游戏配置/运行设置；参与运行的用户由全局用户绑定解析 |
| 全局用户（NexusUser） | 具有稳定 `UserId` 的账号实体：可改用户名、全局优先级、头像和插件用户设置；可绑定多个脚本实例 |
| 用户脚本绑定（UserScriptBinding） | 用户与脚本的运行关系：参与运行开关、运行天数、每日成功次数上限、配置快照、前置/后置脚本、用户通知开关和 SMTP 收件人覆盖 |
| 调度队列（DispatchQueue） | 按 Index 顺序链式执行一组脚本实例（每实例内仍按用户串行）；可定时/启动时自动运行，结束可执行完成操作 |
| 尝试（RunAttempt） | 一次尝试 = 一次完整的进程启动→监控→判定→清理；失败按 MaxAttempts 重试 |
| 运行（RunRecord） | 一次「脚本实例 × 全局用户绑定」的完整运行（含全部尝试），落盘历史（.json 纯状态 + 按尝试分批日志） |
| 运行状态（RunSession） | 一次运行的状态/元数据对象；不再承担完整流程，流程由 `ExecutionCoordinator` 编排 |
| 执行计划（ScriptExecutionPlan / QueueExecutionPlan） | 从仓储快照构建并冻结本次脚本/队列的任务、脚本、用户、资源和完成操作描述；运行期间不回读共享仓储 |
| 执行准入 profile（ExecutionAdmissionProfile） | 描述脚本/队列的并行分类、资源集合和完成操作；队列分类在计划创建时固定 |
| 执行门禁（ExecutionValidator） | 执行前的脚本/队列/用户/进程冲突与限制校验；不创建运行任务 |
| 执行准入策略（ExecutionAdmissionPolicy） | 纯逻辑比较资格矩阵、重复目标、资源冲突、完成操作兼容性和待执行系统操作 |
| 执行状态存储（ExecutionStateStore） | 在同一临界区完成准入检查、活动运行登记、profile 资源租约释放和完成意图协调 |
| 执行运行器（ExecutionRunner） | 负责后台脚本/队列生命周期、用户串行、历史落盘、通知和完成意图提交 |
| 系统操作执行器（SystemActionExecutor） | 负责运行组空闲后的完成操作 arm、真实 60 秒倒计时与取消 |
| 实时事件总线（RealtimeEventBus） | 以全局序列号和有界订阅队列扇出运行状态、日志、系统操作与宿主状态；慢连接丢弃旧事件并要求状态重同步 |
| 尝试执行 | `ExecutionCoordinator` 直接承接前/后置脚本、脚本监控、判定和资源清理调用 |
| 完成判定（SessionJudge） | 判断脚本/关键字两模式的判定状态机，每尝试独立实例 |
| 运行预算（RunBudget） | 贯穿一次完整运行的总超时预算；重试、前置/后置脚本和命令超时共享剩余时间 |
| 配置交换（ConfigSwap） | 运行前 configPath ↔ 用户快照的交换机制（见第 4 节） |
| 配置运行作用域（ConfigRunSession） | 编排一次运行的事务动作并固定最终收尾顺序 |
| 日志监控（LogMonitor） | 对脚本日志文件的增量读取器，支持追加/截断/替换三种文件形态（见第 6 节） |
| 插件 capability | 与插件身份/元数据分离的可查询能力；C# 按接口注册，数据化插件按 key 登记 |
| 插件仓库 catalog | 固定官方源发布的插件索引；客户端校验 schema、名称、受限版本、宿主兼容性、包 URL、大小和 SHA256 |
| 插件 pending 事务 | 插件包下载并校验后写入 staging 与 `pending.json`，下次启动在插件扫描前完成安装、更新或卸载 |
| 宿主外部 HTTP 出口 | 依据 `ProxyMode` 选择无代理、系统代理或自定义代理；外部请求读取最新设置，loopback 强制直连 |
| 执行应用端口（IExecutionService / IFrozenQueueExecutionService） | Web、Scheduler 与常驻服务 CLI 通道共享的启动/取消入口，由 `DispatchCenter` 直接实现 |
| 控制面（Control API） | 常驻服务拥有运行时数据与执行状态；Web、CLI、manage 通过本机 HTTP 控制 API 提交查询与变更 |



## 8. 已知行为与边界

以下行为属**设计语义**（如实记录，非缺陷）：

1. **配置交换清除运行产物**：运行结束时 `DoRestore` 清空 configPath 再还原现场，**运行期间脚本写入 configPath 内的文件（含脚本日志文件）会被删除**。日志文件的安全保存依赖宿主历史落盘（.json + 按尝试分批 .log），脚本自身文件请避免放在 configPath 内。自动更新配置开启时，脚本写入的任务完成记录/运行计数/新增任务会在收尾同步进用户快照 store；配置本体仍还原为运行前现场，但快照内容延续到下次运行（详见 4.5）。
2. **同一用户尝试间的日志残留**：配置还原只在**整个运行结束**时执行，尝试之间 log.txt 保留（监控已按末尾读+严格 fresh 处理，无害）。
3. **配置 JSON 无事务锁**：服务运行期间不建议另一个实例同时修改配置。
4. **定时触发为每分钟秒级检测**：服务在该分钟内处于运行状态即可触发，错过整点不补跑；触发时通过统一计划与准入流程。重复目标、标准队列占用、资源冲突、pending 系统操作和运行组收尾等瞬时准入冲突进入待重试触发，资源释放后继续尝试；计划校验失败和完成操作不兼容等永久错误消费本次触发并记录失败。
5. **生产与测试权限分层**：正式版构建使用 requireAdministrator，普通用户启动正式程序返回权限错误（exit 2）；开机自启为计划任务（onlogon + highest）。`codex` UI/System Smoke 使用 `NexusTestHost=true` 的隔离 Test Host，服务、API、进程与更新事务以本地反馈语义运行；`admin` UI/System Smoke 使用生产 release，并在 Administrator / High Integrity 或 System Integrity 下由 GitHub CI 验证真实门禁。两种模式共享业务断言，运行模式由统一测试入口显式选择。
6. **远程访问**：默认仅绑定 `127.0.0.1`；开启后绑定 `http://+:{port}/`（禁止 `0.0.0.0`），远程请求须 `Authorization: Bearer <token>`，自动添加防火墙入站规则；局域网设备须用本机局域网 IP 访问。
7. **进程名检测的权衡**：`IsExeRunning` 按进程名（不含扩展名）检测，同名无关进程可能误报（防重复启动的保守权衡）；bat 经 cmd 包装无法按名检测，直接放行。
8. **判断脚本输入为本次尝试日志段**：跨尝试的失败/成功行不进入判定输入；如确需跨尝试信息，请通过 `script` 目录的持久文件自行记录。



### 8.1 已接受的设计约束

以下行为经审计确认为**既定语义**，保持现状并以文档/测试锁死，不按缺陷修复：

| 项 | 语义 | 锁死方式 |
|---|---|---|
| 判断脚本（尤其 Python）可读写边界无法技术强制 | 信任边界：config 只读 + script 可读写以契约约束，宿主不把解释器当沙箱（见 §5.3） | 文档 §5.3 |
| 定时触发为秒级 tick，跨整点/休眠错过不补跑 | 该分钟内处于运行状态即可触发，错过即错过 | 文档 §8 第 4 条 + L1 测试 `ScheduledTrigger_DoesNotBackfillMissedOccurrence` |
| `IsExeRunning` 按进程名检测同名进程可能误报 | 保守优先：宁可误报防重复启动 | 文档 §8 第 7 条 |
| 通知单通道失败仅告警不阻断 | 一通道异常不影响其余通道与运行流程 | 文档 §7.1 + 既有测试 |
| 首次配置同步在运行开始约 15 秒后执行一次 | 关闭自动更新时也执行首次检测；收尾同步仅在自动更新开启时执行 | `RunSession.ShouldRunFirstSync` 与配置同步回归测试 |
| 快速失败可能错过首次配置同步 | 自动更新开启时由收尾同步兜底；关闭时不产生收尾快照 | 运行时序约束与配置同步测试 |



## 10. 架构与模块定位（开发者导航）



### 10.1 总体结构

```
NexusPipeline/
├── src/                C# 后端（.NET 8，WinForms 托盘 + HttpListener/Kestrel）
│   ├── Application/    应用宿主、启动流程、查询、状态与业务端口：ProgramEntry/ApplicationHost/StartupPipeline/RuntimeInitializer/HostedRuntimeInitializer/Queries/State/Abstractions/Repositories
│   ├── *.cs            组合根基础设施：Bootstrap/RuntimeContext/TrayApp
│   ├── Models/         领域模型（NexusPipeline.Models）
│   ├── Services/       服务层（NexusPipeline.Services，按 Execution/Configuration/Judgement/Scheduling/History/Notification/Networking/Update/Diagnostics 分域）
│   ├── Persistence/    持久化层（NexusPipeline.Persistence）
│   ├── Utilities/      工具层（NexusPipeline.Utilities）
│   ├── Extensibility/  宿主内部数据插件 capability 契约（NexusPipeline.Extensibility，internal）
│   ├── Web/            HTTP 层（NexusPipeline.Web）
│   ├── Cli/            命令行层（NexusPipeline.Cli）
│   ├── Mcp/            MCP Streamable HTTP 适配层（NexusPipeline.Mcp）
│   └── Plugins/        数据化/managed-code 插件发现、加载与 capability 注册（NexusPipeline.Plugins）
├── src/NexusPipeline.Plugin.Abstractions/  独立 public Plugin API v1.8（无宿主业务引用）
├── frontend/           Vue/TypeScript/Vite 前端源码、路由、状态和 Nexus UI 组件
│   ├── src/app/        App shell、启动编排、令牌提示和插件 route 生命周期
│   ├── src/platform/   宿主平台服务（i18n、API、页面状态、外观、shell、工具提示等）
│   ├── src/plugin-bridge/ Frontend API 1.5 插件桥接实现与宿主依赖边界
│   ├── src/features/   按业务域组织的 Vue 页面、feature 组件、composable、service 与 utils
│   ├── src/ui/         Nexus UI primitives 与公开 nxp-* Custom Elements
│   ├── src/styles/     设计 token、基础元素样式与布局/shell 样式
│   ├── src/stores/     Pinia 全局状态
│   └── public/i18n/    宿主 zh-CN/en-US 词典（唯一资源源）
├── release/wwwroot/    Vite 构建后的发布静态 Web 资源
├── .nxp/               安装目录内的内部运行状态（runtime 标记与 state 持久状态）
├── tests/
│   ├── NexusPipeline.Tests/  xUnit 单元测试（通过 InternalsVisibleTo 访问 internal 契约）
│   ├── system/               Windows 真实进程 System Smoke（mcp/runtime/judge/execution-resilience/emulator/update）
│   ├── e2e/                  Playwright 端到端测试（黑盒，@playwright/test 框架）
│   ├── documentation/        Node 内建模块文档一致性检查
│   ├── support/              Windows 进程、版本解析、测试运行时公共设施
│   └── stress/               压力与专项诊断资产（不进入默认 CI/发布门禁）
├── tools/source-hash.mjs      Node 源码指纹计算（排除 bin/obj）
└── tests/run.mjs              统一测试调度入口
```



### 10.2 后端分层与依赖方向（只允许向下依赖）

```
NexusPipeline（根：Application/Program/Bootstrap/RuntimeContext 组合根）
   └── Models（领域模型）← Services（服务）← Persistence（持久化）← Utilities（工具，被一切依赖）
        ↑           ↑            ↑
NexusPipeline.Web（HTTP 适配层）
NexusPipeline.Cli（命令行适配层）
NexusPipeline.Mcp（MCP 适配层）
NexusPipeline.Extensibility（中立 capability/profile 契约）
NexusPipeline.Plugins（插件发现、注册与内置实现）
```

- **核心域不得引用 Web/Cli**（例外：`RuntimeContext` 组合根持有 `PluginManager` 实例——组合根允许）。
- **Web/Cli 只调用核心域服务，不做业务逻辑**，只做参数解析与响应组装。
- **Plugins 通过数据化 manifest 或独立 Plugin API v1.8 交互**；`NexusPipeline.Plugin.Abstractions` 不引用宿主业务模型，managed-code 插件由 collectible `AssemblyLoadContext` 隔离加载；跨模块的宿主内部 capability/profile 契约位于 `Extensibility/`，数据化专项插件（`DataSpecializedPlugin`）仍为纯数据驱动。
- **依赖方向顺沿命名空间**：Models 无依赖；Services 依赖 Models/Persistence/Utilities；Persistence 依赖 Utilities。
- **边界约束**：执行核心、调度器和配置编辑的能力消费通过显式端口连接，运行期实体读取通过 `Application/Queries/` 或 `Application/Abstractions/` 端口完成；实体内存所有权与同步集中在 `Application/State/RuntimeEntityState`，`ConfigSwapRecovery` 的会话恢复通过构造注入的脚本查找与用户快照委托获取数据，不反向查找组合根。`Utilities/Logger` 由设置加载/保存流程显式配置日志等级，不反向读取 `RuntimeContext`。



### 10.3 关键类职责

| 类 | 位置 | 职责 |
|---|---|---|
| `Program` | src/Application/ProgramEntry.cs | 进程入口，仅转交 `ApplicationHost.Run(args)` |
| `ApplicationHost` | src/Application/ApplicationHost.cs | 进程级初始化、服务生命周期入口和正式命令分发 |
| `RuntimeInitializer` | src/Application/RuntimeInitializer.cs | 生产管理员权限校验、Test Host 编译分支、约束加载和只读设置快照；不加载或修复运行时实体、不启动服务 |
| `HostedRuntimeInitializer` | src/Application/HostedRuntimeInitializer.cs | 取得单实例所有权后的权威设置加载、实体加载、历史数据修复、配置交换恢复、工作目录维护、配置恢复与任务注册 |
| `RuntimeDataReconciler` | src/Application/RuntimeDataReconciler.cs | 宿主所有权建立后的失效绑定清理、实体名称消歧与修复结果持久化 |
| `StartupPipeline` | src/Application/StartupPipeline.cs | 常驻服务、网页模式与重启的单实例互斥、共享启动/关闭不变量、Web/托盘生命周期 |
| `RuntimeStateLayout` | src/Persistence/RuntimeStateLayout.cs | 创建当前 `.nxp` 运行状态目录并提供 service.pid、web.port 和 scheduler-state 路径 |
| `Bootstrap` | src/Bootstrap.cs | 服务启动/停止编排、Web 端口重试 |
| `HostRestartCoordinator` | src/Services/HostRestartCoordinator.cs | 统一 Web/MCP/CLI 间接重启生命周期；原子取得维护租约、延迟拉起子进程、处理失败释放与旧进程退出延迟 |
| `RuntimeContext` | src/RuntimeContext.cs | 组合根：内部 ServiceProvider 注册各领域服务、查询和运行时适配器；设置生命周期与服务解析出口，不拥有实体集合 |
| `RuntimeEntityState` | src/Application/State/RuntimeEntityState.cs | Scripts/Queues/Users 的唯一内存所有权、同步边界、查找、深拷贝快照、原子执行输入快照与状态替换；不承载业务规则或持久化 |
| `ScriptQueries` / `QueueQueries` / `UserQueries` | src/Application/Queries/ | 为控制面提供脚本、队列、用户读取用例与业务读取模型；集中有效脚本、调度时间、绑定覆盖和锁状态计算 |
| `IScriptRepository` / `IQueueRepository` / `IUserRepository` / `IExecutionSnapshotProvider` | src/Application/Abstractions/、src/Application/Repositories/ | 执行/调度域读取脚本、队列、启用用户及同一实体状态同步边界内的执行输入快照；运行时适配器直接依赖 `RuntimeEntityState` |
| `ISettingsProvider` / `IHistoryStore` | src/Application/Abstractions/、src/Application/Repositories/、src/Services/History/ | 设置读取与历史写入端口，避免服务直接反向查组合根或具体历史文件实现 |
| `IExecutionService` / `IFrozenQueueExecutionService` / `INotificationService` / `IPluginCapabilityResolver` | src/Application/Abstractions/ | Web、Scheduler、执行域和插件能力消费端口；执行端口由 `DispatchCenter` 直接实现，其他端口由 `NotificationDispatcher`、`PluginManager` 提供 |
| `ScriptCommands` / `QueueCommands` / `UserCommands` / `SettingsCommands` / `ConfigEditCommands` | src/Application/Commands/ | 脚本、队列、全局用户、绑定、头像、设置和配置编辑生命周期的校验、租约协调、持久化和副作用收尾；Web 只负责请求解析与展示投影 |
| `OperationResult<T>` | src/Application/Contracts/OperationResult.cs | 与 HTTP/CLI 无关的成功、错误分类和候选目标结果契约 |
| `TargetResolver` | src/Application/TargetResolver.cs | 统一执行 ID 优先、唯一名称匹配和歧义候选返回 |
| `DataStore` | src/Persistence/DataStore.cs | 持久化仓储（scripts/queues JSON 读写） |
| `DispatchCenter` | src/Services/DispatchCenter.cs | 执行应用端口门面：获取冻结计划、提交准入登记、取消和入口参数编排；不承载后台运行流程 |
| `ExecutionPlanBuilder` | src/Services/Execution/ExecutionPlanBuilder.cs | 从脚本/队列/用户仓储快照构建脚本与队列执行计划，固定任务引用、用户顺序、资源和完成操作；运行时通过 `IExecutionSnapshotProvider` 获取队列与脚本的原子输入 |
| `ExecutionExplainService` | src/Services/Execution/ExecutionExplainService.cs | 基于真实冻结计划和共享准入评估生成只读 dry-run 投影；汇总用户限制、资源、任务、警告和稳定失败原因，不创建运行副作用 |
| `ExecutionValidator` | src/Services/Execution/ExecutionValidator.cs | 脚本/队列存在性、用户门禁、长时混排、进程预检和任务计数校验 |
| `PluginAvailability` | src/Services/PluginAvailability.cs | 根据插件身份、数据化专项类型和运行态统一判断脚本实例是否仍可使用专项插件 |
| `ExecutionAdmissionPolicy` | src/Services/Execution/ExecutionAdmissionPolicy.cs | 纯逻辑比较 EmulatorOnly/Standard 矩阵、重复目标、资源冲突、完成操作兼容性和 pending 阻断，并标注瞬时/永久失败 |
| `ExecutionRunner` | src/Services/Execution/ExecutionRunner.cs | 脚本/队列后台生命周期、队列内用户串行、历史落盘、通知和完成意图提交 |
| `SystemActionExecutor` | src/Services/Execution/SystemActionExecutor.cs | 运行组空闲后的完成操作 arm、pending 倒计时和取消语义 |
| `ExecutionCoordinator` | src/Services/Execution/ExecutionCoordinator.cs | 一次运行级编排：用户顺序、重试循环、配置事务和运行收尾；后台任务与历史/通知外层边界由 `ExecutionRunner` 承载 |
| `RunSession` | src/Services/RunSession.cs | 一次运行的状态对象：元数据、预算、日志收集、配置事务状态和回调；不再拥有 `RunAsync` 流程 |
| `RetryPolicy` / `ResultCollector` | src/Services/Execution/ | 普通失败重试判定、日志容量/按尝试分段收集 |
| `ExecutionStateStore` | src/Services/Execution/ExecutionStateStore.cs | 线程安全管理运行中/已结束任务、准入 profile 资源租约、运行组 `Open/Closing/ActionPending/Maintenance` 状态、完成意图与待执行系统操作，并为执行、dry-run、编辑、宿主配置 CRUD 提供租约协调 |
| `RunningExecution` | src/Services/Execution/RunningExecution.cs | 单次运行的可观察状态、并发安全记录/日志写入与一致快照 |
| `EmulatorDetector` / `PluginEmulatorProbeService` | src/Services/EmulatorDrivers.cs | 按 MuMu、已启用的 managed-code provider 和 Generic ADB 探测并冻结目标；provider 明确报错、冲突或超时会 fail closed |
| `PluginEmulatorSupportRegistry` / `PluginEmulatorDriverAdapter` | src/Plugins/Runtime/PluginEmulatorSupportRegistry.cs、src/Services/EmulatorDrivers.cs | 注册表按插件生命周期撤销 provider；驱动适配器对启动、前台查询、截图、应用停止和实例关闭统一施加宿主超时与取消边界 |
| `GenericAdbEmulatorDriver` / `MuMuEmulatorDriver` | src/Services/EmulatorDrivers.cs | 宿主保留通用 ADB 与 MuMuManager 驱动；雷电、夜神和 BlueStacks 的厂商实现由官方 `EmulatorSupport` 插件负责 |
| `RunBudget` | src/Services/Execution/RunBudget.cs | 统一整个运行（含重试、前置/后置脚本）的 elapsed/remaining/命令超时上限；保留 `NEXUS_TIME_SCALE` 语义 |
| `ConfigRunSession` | src/Services/Configuration/ConfigRunSession.cs | 运行期间配置事务的收尾编排：固定同步、替换还原、script 清理和现场恢复顺序 |
| `RunAttemptFinalizer` | src/Services/Execution/RunAttemptFinalizer.cs | attempt 级脚本进程树、游戏/模拟器清理基础设施；承载失败/取消/强制关闭策略，不改变既有清理时序 |
| `SessionJudge` | src/Services/Judgement/SessionJudge.cs | 完成判定策略状态机：判断脚本/关键字两模式，维护判定状态与输入 |
| `JudgeScriptRunner` | src/Services/Judgement/JudgeScriptRunner.cs | 判断脚本执行器：构造脚本字段、用户、config（只读）、script（可读写）和**本次尝试日志段**输入；提供统一 Jint 宿主、Python/Jint 30 秒超时、截图与只读判定探针 API，以及 stdout 尾行 JSON 解析（含 `replaceConfigs`/`notifyScreenshotId`） |
| `RunScreenshotStore` / `RecentScreenshotCache` / `JudgeRuntimeBridge` | src/Services/Execution/RunScreenshot.cs、src/Services/Execution/RecentScreenshotCache.cs、src/Services/Judgement/JudgeRuntimeBridge.cs | 按 Attempt 隔离的 8 张 FIFO 原分辨率截图池、PC 最近有效帧缓存、截图与进程/窗口/HTTP 只读探针共用的 Python 判断脚本临时 loopback 桥接 |
| `LogMonitor` | src/Services/LogMonitor.cs | 日志增量读取器：追加、截断后追加和替换三种形态；同长度重写通过已观察内容 checkpoint 定位截断边界，替换使用 FileId 与创建时间回退检测，忽略运行前已有内容 |
| `UserConfigManager` | src/Services/UserConfigManager.cs | 配置储存对外门面，实现分层见 `ConfigSwapPrimitives`/`ConfigSwapSession`/`ConfigSwapPaths`；编辑会话（normal/fresh/reuse）与隐藏配置管理 |
| `ConfigSwapPrimitives` | src/Services/ConfigSwapPrimitives.cs | 配置交换文件原语层：安全移动/原子替换/重试/跨进程互斥/形态判断 |
| `ConfigSwapSession` | src/Services/ConfigSwapSession.cs | 配置交换 façade：replaceConfigs、自动更新配置事务镜像与公共会话入口；恢复职责转交 `ConfigSwapRecovery` |
| `ConfigSwapRecovery` | src/Services/ConfigSwap/ConfigSwapRecovery.cs | `.session` 自愈、启动扫描、孤儿进程延迟重试、fresh 生成物/原配置还原；按当前全局用户绑定建立 UserId 恢复白名单；脚本/用户读取经注入的委托 |
| `ConfigStoreDiff` | src/Services/Configuration/ConfigStoreDiff.cs | 扫描外部 config 与权威 store，按文件内容生成 added/changed/deleted/preserved 差异计划；避免按完整快照重复复制 |
| `ConfigStoreTransaction` / `ConfigStoreTransactionRecovery` | src/Services/Configuration/ConfigStoreTransaction.cs | manifest/stage/rollback/commit 增量事务、generation 元数据提交与崩溃回滚；无法确认事务状态时隔离现场并阻断后续写入 |
| `ExtraConfigSync` / `ExtraConfigStoreTransaction` | src/Services/Configuration/ExtraConfigSync.cs、src/Services/Configuration/ExtraConfigStoreTransaction.cs | 附加配置路径的形态校验、准备/还原和带 manifest 的 stage/backup/commit 快照事务；准备失败 fail closed，未提交现场由恢复流程处理 |
| `ConfigStoreMetadata` | src/Services/ConfigStoreMetadata.cs | store 归属、定位/形态指纹与 generation 管理；严格读取当前元数据协议 |
| `ConfigSessionMark` / `EditSession` | src/Services/ConfigSwap/ | 配置会话持久化标记与 Web 编辑会话状态模型 |
| `ConfigSwapPaths` | src/Services/ConfigSwapPaths.cs | 配置数据目录管理：data/{脚本Id}/{UserId} 子目录定位与清理（持久层在用户目录顶层，会话事务目录收敛于 work/） |
| `ConfigWorkDirMaintenance` | src/Services/ConfigWorkDirMaintenance.cs | 当前 work/ 空闲目录、runtime 和 staging 启动清扫 |
| `LogPattern` | src/Persistence/LogPattern.cs | 日志路径格式解析（日期占位符/通配符严格匹配，无格式外猜测） |
| `Scheduler` | src/Services/Scheduling/Scheduler.cs + `SchedulerTriggerPlanner.cs` / `SchedulerRetryQueue.cs` / `SchedulerStateFence.cs` / `SchedulerStateStore.cs` | 门面协调定时/启动触发；`SchedulerTriggerPlanner` 负责 next-trigger、扫描窗口、occurrence 枚举/匹配和 trigger key；`SchedulerRetryQueue` 负责 pending、attempting、running、重试调度和状态转换；`SchedulerStateFence` 负责 occurrence/replay fence、快照/恢复编排，持久化 I/O 仍由 `ISchedulerStateStore` 承担 |
| `HistoryService` | src/Services/History/HistoryService.cs | 历史记录读写与清理 |
| `NotificationDispatcher` | src/Services/Notification/NotificationDispatcher.cs | 宿主内置 Webhook/SMTP 通知领域服务；脚本、队列和宿主通知 API DTO 均从此入口发送 |
| `WebServer` | src/Web/WebServer.cs | HTTP 骨架：生产 HttpListener / Test Host 托管 loopback 监听、静态文件安全头、特性路由表（[ApiRoute] 反射扫描注册）和远程令牌校验 |
| `ApiDiagnosticsHandler` | src/Web/ApiDiagnosticsHandler.cs | 提供只读诊断快照与 loopback 脱敏支持包导出，不承载自动修复 |
| `WebTransport` | src/Web/WebTransport.cs | Test Host 的普通权限 HTTP 请求解析、响应流和 HttpListener/托管 transport 共用上下文适配 |
| `HttpHelper` | src/Web/HttpHelper.cs | 通用 HTTP 辅助（写 JSON/404/405/解析请求体） |
| `ApiXxxHandler` | src/Web/ | 每资源一个 handler，`[ApiRoute("资源名")]` 标注，路由表自动注册；只做协议解析、应用用例调用与 HTTP 响应 |
| `ConfigEditHttpAdapter` | src/Web/ConfigEditHttpAdapter.cs | 配置编辑请求解析和响应组装；脚本与用户路由共用，不让 handler 横向调用 |
| `McpHost` | src/Mcp/McpHost.cs | 同进程内嵌的 Kestrel Streamable HTTP MCP 宿主；固定 loopback 监听、启动/停止和工具注册；端口冲突不漂移且不影响 Web/Control API |
| `McpSecurity` | src/Mcp/McpSecurity.cs | MCP Host、Origin 和请求体边界检查；MCP 端点与 Web 远程访问设置隔离 |
| `McpToolContext` | src/Mcp/McpToolContext.cs | MCP 适配层组合根；提供快照、ID/唯一名称解析、状态/历史/设置投影，调用 Application Commands 或核心服务 |
| `McpReadOnlyTools` / `McpMutationTools` | src/Mcp/ | 面向 Agent 的核心工具子集（含诊断与运行解释的只读能力 + 常规变更）；删除、密钥、插件安装等高风险操作走本地 CLI |
| `McpPolicy` / `McpToolResult` | src/Mcp/ | 行为级安全策略（队列完成操作复核）与统一结构化 `ok/errorCode/errorMessage/data` 结果映射 |
| `ControlApiContract` | src/Application/Contracts/ControlApiContract.cs | Control API 服务名与协议版本身份契约，供服务状态输出与 CLI 握手校验共用 |
| `CliArguments` / `CliCommandRouter` | src/Cli/ | noun/subcommand 参数解析和正式命令分派 |
| `CliApiClient` / `CliTransport` | src/Cli/ | CLI 到 owning service 的本机 HTTP 控制通道、身份握手、自动拉起、端口发现和按端点分层超时 |
| `CliOutput` / `CliExitCodes` | src/Cli/ | 人类输出、`--json` envelope、诊断流和稳定退出码 |
| `ControlMenu` / `MainMenu` | src/Cli/ | 交互菜单适配层；菜单查询与变更均复用正式 CLI/Control API |
| `PluginCapabilityRegistry` | src/Plugins/Runtime/PluginCapabilityRegistry.cs | capability 的类型化注册/查询与数据插件 key 注册；`LoadAll` 清空后重建，避免重复能力 |
| `PluginManager` | src/Plugins/Runtime/PluginManager.cs + `Runtime/PluginDiscovery.cs` / `Runtime/ManagedPluginRuntime.cs` / `Runtime/PluginManagementSnapshotCache.cs` | 门面负责插件开关、能力查询和生命周期编排；`PluginDiscovery` 负责本地 manifest 扫描/启用偏好；`ManagedPluginRuntime` 负责 managed-code 加载、生命周期和卸载；`PluginManagementSnapshotCache` 负责摘要与控制面管理投影缓存 |
| `PluginManagementView` | src/Plugins/Runtime/PluginManagementView.cs | 合并 manifest、运行态、展示元数据、商店归属和 pending 事务，供 Web、MCP、状态接口使用 |
| `PluginExtensionServices` | src/Plugins/PluginExtensionServices.cs | v1.6 UI、作用域数据、插件 Web API、历史贡献、本地化引用注册表与 DTO 校验；按插件生命周期撤销注册 |
| `PluginAssetStore` | src/Plugins/Managed/PluginAssetStore.cs | 插件二进制资产存储：按插件命名空间与 scope 隔离、内容寻址 Id、原子写入、路径逃逸防护与宿主级绝对上限 |
| `PluginUserGlobalSettingsService` | src/Plugins/PluginUserGlobalSettingsService.cs | 统一插件用户全局设置的读取、字段投影、secret 脱敏、输入校验和超时边界，供 Web 复用 |
| `PluginFrontendManifest` | src/Plugins/PluginFrontendManifest.cs | 校验 Frontend API 1.5 清单与 `web/` 资源路径，不向前端泄露插件目录 |
| `PluginRepositoryCatalog` | src/Plugins/Repository/PluginRepositoryCatalog.cs | 固定官方源的 catalog schema、artifact/名称/版本/URL/SHA/changelog/宿主兼容性校验；不执行网络请求 |
| `DataSpecializedPlugin` | src/Plugins/DataSpecialized/DataSpecializedPlugin.cs + `DataSpecialized/DataSpecializedPluginLoader.cs` / `DataSpecialized/DataSpecializedResolveParser.cs` / `DataSpecialized/DataSpecializedProfileResolver.cs` / `DataSpecialized/DataSpecializedInputResolver.cs` | 门面保留 `IProfileResolver` 与缓存/插件身份；`DataSpecializedPluginLoader` 负责 manifest、路径和脚本文件校验；`DataSpecializedResolveParser` 负责 resolve.json、输入和路径模板解析；`DataSpecializedProfileResolver` 负责 profile 推导；`DataSpecializedInputResolver` 负责输入声明与候选绑定 |
| `PluginRepositoryService` | src/Plugins/Repository/PluginRepositoryService.cs + `Repository/PluginRepositoryCatalogCache.cs` / `Repository/PluginStoreProjector.cs` / `Repository/PluginRepositoryOperations.cs` / `Repository/PluginReadmeService.cs` | 门面编排 catalog 刷新和详情读取；`PluginRepositoryCatalogCache` 负责内存/磁盘 catalog 缓存；`PluginStoreProjector` 负责合并本地状态的商店投影；`PluginRepositoryOperations` 负责安装/更新/卸载串行事务；`PluginReadmeService` 负责本地与官方 README 读取和缓存 |
| `PluginPackageService` | src/Plugins/Repository/PluginPackageService.cs | 通过统一外网出口下载插件包，校验大小/SHA/ZIP 路径/manifest 并写入 staging journal |
| `PluginInstallRecovery` | src/Plugins/Repository/PluginInstallRecovery.cs | 启动时在 `PluginManager.LoadAll` 前应用 pending 事务，负责交换、归属记录和失败恢复 |
| `DiagnosticsService` | src/Services/Diagnostics/DiagnosticsService.cs | 汇总稳定诊断检查，生成脱敏支持包并执行大小与敏感信息边界校验 |
| `JsonStore` | src/Persistence/JsonStore.cs | 读取插件配置、密钥和作用域 JSON；解析损坏时保留原文件并记录恢复现场 |
| `AppearanceLegacyMigration` | src/Services/AppearanceLegacyMigration.cs | 旧外观数据的一次性格式搬迁：资产导入原提供方插件的资产 scope，搬迁载荷写入作用域数据，成功标记落盘后可重试 |
| `OutboundHttpClientProvider` | src/Services/Networking/ProxyConfiguration.cs | 按最新设置创建外部 HTTP client；支持无代理/系统代理/自定义 HTTP(S) 代理，loopback 强制直连 |
| `PluginContracts` | src/Extensibility/PluginContracts.cs | 数据插件的 `IPluginCapability`/profile 契约与 `ScriptProfile`；全部 internal；外部代码插件契约位于独立 Plugin API 项目 |
| `Logger` | src/Utilities/Logger.cs | 分级日志（DEBUG/INFO/WARN/ERROR/FATAL），显式阈值配置，阈值过滤与控制台着色 |



### 10.4 public / internal 约定

- 主程序程序集仍只向自身暴露 `Program`（入口）与领域模型；外部代码插件只引用独立的 `NexusPipeline.Plugin.Abstractions` public API v1.8。宿主内部的 `IPluginCapability`/`ScriptProfile` 不属于外部插件契约，Plugin API 不暴露宿主 DI 或领域模型。
- 其余全部 `internal`：新增类型默认 internal，除非它属于契约清单。



### 10.5 新增 API 的落点

- HTTP 路由：在 `src/Web/` 新增或扩展 `ApiXxxHandler`，类上标注 `[ApiRoute("资源名")]`（子路由标注在方法上，如 `cancel`）；`WebServer` 启动时反射扫描自动注册，**无需改路由表**。
- 控制命令：先在 owning service 的 `ApiXxxHandler` 增加资源操作，再由 `CliCommandRouter` 添加参数与响应适配；交互菜单调用正式命令，不直接触碰 `RuntimeContext` 持久化集合。
- MCP 适配器：在 `src/Mcp/` 增加类型化工具和投影；只有面向 Agent 的核心子集才进入工具面，其余能力走 CLI；`McpHost` 负责 Streamable HTTP 生命周期，`McpSecurity` 负责 loopback/Host/Origin/体积边界，业务写入必须转入 Application Commands 或既有核心服务。
- 轻量控制面：`WebServerOptions.FromSettings` 保留 `/api/*`，关闭静态 Web UI 与远程绑定；Normal 模式继续按设置提供 Web UI/远程访问。
- 业务服务：核心域 `Services/` 新增服务类，注册到 `RuntimeContext`（组合根）后经 `Resolve<T>()` 或属性访问。



### 10.9 功能定位指南（找代码）

| 想找什么 | 去哪里 |
|---|---|
| 某 API 路由的实现 | `src/Web/ApiXxxHandler.cs`（`[ApiRoute]` 特性注册，见 `WebServer.Routes`） |
| MCP 工具、端点或安全策略 | `src/Mcp/McpHost.cs`、`src/Mcp/McpSecurity.cs`、`src/Mcp/Mcp*Tools.cs`；业务规则进入 Application Commands/核心服务 |
| 命令行某菜单 | `src/Cli/` 对应菜单类 |
| 脚本运行流程/重试/日志监控 | `src/Services/Execution/ExecutionCoordinator.cs`、`src/Services/RunSession.cs`（状态）、`src/Services/Execution/RetryPolicy.cs`、`src/Services/Execution/RunBudget.cs`、`src/Services/Execution/RunAttemptFinalizer.cs`、`src/Services/LogMonitor.cs`（日志增量读取/替换检测）、`src/Persistence/LogPattern.cs`（日志路径格式解析） |
| 自定义完成标志（关键字/判断脚本） | `src/Services/Judgement/SessionJudge.cs`（判定状态机）、`src/Services/Execution/ExecutionCoordinator.cs`（尝试执行/触发时机）、`src/Services/Judgement/JudgeScriptRunner.cs`（脚本执行器）、`src/Utilities/TextRules.cs`（`KeywordRule`） |
| 判断脚本边界与配置替换 | `src/Services/UserConfigManager.cs`（门面）、`src/Services/Configuration/ConfigRunSession.cs`（运行配置生命周期）、`src/Services/ConfigSwapSession.cs`（替换/同步 façade）、`src/Services/ConfigSwap/ConfigSwapRecovery.cs`（恢复）、`src/Services/Judgement/JudgeScriptRunner.cs`（`ResolveWithin` 防逃逸） |
| 插件仓库/安装恢复 | `src/Plugins/Repository/PluginRepositoryService.cs`、`src/Plugins/Repository/PluginRepositoryCatalogCache.cs`、`src/Plugins/Repository/PluginStoreProjector.cs`、`src/Plugins/Repository/PluginRepositoryOperations.cs`、`src/Plugins/Repository/PluginPackageService.cs`、`src/Plugins/Repository/PluginInstallRecovery.cs`、`src/Web/ApiPluginsHandler.cs` |
| 外部 HTTP/代理 | `src/Services/Networking/ProxyConfiguration.cs`、`src/Services/Update/UpdateService.cs`、`src/Services/WebhookSender.cs` |
| 队列调度触发 | `src/Services/Scheduling/Scheduler.cs`、`SchedulerTriggerPlanner.cs`、`SchedulerRetryQueue.cs`、`SchedulerStateFence.cs` |
| 通知发送（Webhook/SMTP） | `src/Services/Notification/NotificationDispatcher.cs`、`src/Services/Notification/NotificationFormatter.cs`、`src/Services/WebhookSender.cs`、`src/Services/SmtpSender.cs` |
| Vue 页面渲染/表单 | `frontend/src/features/` 对应域文件与 `frontend/src/ui/` 组件 |
| 页面前端交互绑定 | Vue props/emits、组件事件与 feature composable；现存 `data-action` 属性不再由全局运行时读取，新交互不得依赖它 |
| 配置读写/加密 | `src/Persistence/ConfigStore.cs`、`src/Persistence/ConfigLoadMode.cs`、`src/Persistence/SecretStore.cs`；公共初始化使用 `ReadOnly`，宿主所有权建立后使用 `Repair` |
| 历史记录格式 | `src/Services/History/HistoryService.cs`、`src/Models/RunRecord.cs` |



### 10.10 数据流速览

```
Web 请求      → WebServer → ApiXxxHandler → Application Query/Command → RuntimeEntityState/DataStore/Logger
CLI / manage  → CliApiClient → Control API → Application Command → DispatchCenter → ExecutionPlanBuilder → ExecutionValidator → ExecutionAdmissionPolicy/ExecutionStateStore → ExecutionRunner
MCP 请求      → McpHost → Mcp*Tools/McpToolContext → Application Command/核心服务 → DataStore/Logger
Scheduler     → Application Command → DispatchCenter → ExecutionPlanBuilder → ExecutionValidator → ExecutionAdmissionPolicy/ExecutionStateStore → ExecutionRunner
运行结束 → ExecutionRunner → INotificationService → NotificationDispatcher → Webhook/SMTP；managed-code 插件 → IPluginNotificationService → NotificationDispatcher；同时向 ExecutionStateStore 提交完成意图
```

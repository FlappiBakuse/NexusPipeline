# 产品边界与模块

本页说明系统分层、核心概念与跨模块约束。各领域的运行流程、失败处理和协议细节由[架构专题索引](README.md)指向唯一维护位置。

## 产品边界

NexusPipeline 是 Windows 本地自动化脚本管家。常驻 Host 提供托盘、HTTP 控制面和内嵌静态 Web UI，负责计划执行、多账号配置隔离、结果判定和通知；Electron 提供桌面入口，独立更新程序负责应用交接。完整载荷与运行依赖见[桌面客户端](../development/desktop-client.md)。Node/npm 仅用于开发构建。

外部脚本拥有自身业务逻辑；Host 拥有运行计划、受控进程、配置事务和历史记录。插件通过数据化声明或独立公开 SDK 扩展，不能接管宿主组合根或直接访问内部实体集合。公开版本与能力见[插件 API](../reference/plugin-api/README.md)。

## 核心概念

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
| 配置交换（ConfigSwap） | 运行前 configPath ↔ 用户快照的交换机制（见[配置专题](configuration.md)） |
| 配置运行作用域（ConfigRunSession） | 编排一次运行的事务动作并固定最终收尾顺序 |
| 日志监控（LogMonitor） | 对脚本日志文件的增量读取器，支持追加/截断/替换三种文件形态（见[判定与日志专题](judgement-logs.md#日志监控)） |
| 插件 capability | 与插件身份/元数据分离的可查询能力；C# 按接口注册，数据化插件按 key 登记 |
| 插件仓库 catalog | 固定官方源发布的插件索引；客户端校验 schema、名称、受限版本、宿主兼容性、包 URL、大小和 SHA256 |
| 插件 pending 事务 | 插件包下载并校验后写入 staging 与 `pending.json`，下次启动在插件扫描前完成安装、更新或卸载 |
| 宿主外部 HTTP 出口 | 依据 `ProxyMode` 选择无代理、系统代理或自定义代理；外部请求读取最新设置，loopback 强制直连 |
| 执行应用端口（IExecutionService / IFrozenQueueExecutionService） | Web、Scheduler 与常驻服务 CLI 通道共享的启动/取消入口，由 `ExecutionDispatcher` 直接实现 |
| 控制面（Control API） | 常驻服务拥有运行时数据与执行状态；Web、CLI、manage 通过本机 HTTP 控制 API 提交查询与变更 |

## 后端分层与依赖方向

```text
Host/Composition（唯一组合根）
   ├── ControlPlane（协议适配） → Modules/*（领域用例与 typed ports）
   ├── Modules/*（业务 owner） → Shared / Platform / 明确的跨域端口
   └── PluginSdk（独立公开契约） ← Modules/Plugins（插件运行时）
```

- **只有 `Host/Composition/HostCompositionRoot.cs` 创建、解析和释放 DI 容器**；业务模块和控制面不得反向定位组合根。
- **ControlPlane 只做协议适配**，调用 Modules 的 query/command/typed port，不直接拥有业务存储或实体集合。
- **Plugins 通过数据化 manifest 或独立 Plugin API 交互**；`NexusPipeline.Plugin.Abstractions` 不引用宿主业务模型，managed-code 插件由 collectible `AssemblyLoadContext` 隔离加载；跨模块的宿主内部 capability/profile 契约位于 `src/Modules/Plugins/Contracts`，数据化专项插件（`DataSpecializedPlugin`）仍为纯数据驱动。
- **Modules 之间的依赖必须显式且无环**；执行、调度、配置编辑和插件能力通过 contracts/typed ports 连接，实体内存所有权集中在 `Host/State/AutomationDefinitionState`。
- **边界约束**：配置恢复通过构造注入的脚本查找与用户快照委托获取数据，不反向查找组合根；共享层不读取宿主容器或具体业务规则。

## 启动与状态所有权

进程入口先完成环境与只读设置检查；取得安装实例所有权后，才加载权威实体、处理可识别的恢复现场并启动服务。Host 组合层负责跨 owner 接线，生命周期层负责启动、维护与停止。CLI 通过常驻服务提交操作，不创建第二套数据写入路径。

`AutomationDefinitionState` 是脚本、队列和用户的唯一内存与同步边界；执行计划从同一边界冻结输入。Settings 采用 clone、校验、原子保存成功后再发布新引用的顺序。业务服务使用显式构造依赖，Modules 不依赖 Host/ControlPlane，Platform 不拥有业务策略。

宿主内部类型默认 internal；对外插件契约由独立 Abstractions 程序集提供。新增 HTTP、CLI、MCP 入口调用对应领域用例，协议层不直接读写业务存储；新增业务在所属模块内定义端口，由组合根接线。

## 跨模块不变量

| 约束 | 权威说明 |
|---|---|
| 先冻结输入和原子准入，再登记运行；取消后先停止必要写入者，再恢复配置 | [执行生命周期](execution.md) |
| 用户快照与原生现场隔离；同步、还原和提交有固定顺序 | [配置事务](configuration.md) |
| 未知格式、身份不明或无法证明停止的现场不得猜测恢复 | [恢复与隔离](recovery.md) |
| 业务结果来自本次尝试的有效证据，不由重试次数或进程退出码补造 | [完成判定](judgement-logs.md) |
| 调度不补发错过的历史 occurrence；瞬时准入冲突与永久错误分别处理 | [调度](scheduling.md) |
| 插件安装归属、分发通道和运行加载分别校验 | [插件运行与安装](plugins.md) |
| 更新必须校验载荷、迁移路线和启动回执，失败保留可恢复现场 | [自动更新](update.md) |

## 代码与验证入口

模块目录、专题 owner 与检查入口见[架构索引](README.md)、[文档映射](../map.json)和[测试索引](../testing/README.md)。具体类名和私有实现以对应模块源码为准，不在总览另维护逐类目录。

# Storage 用例归属

`src/platform/Storage` 同时承载应用流程和持久化实现。目录名不能证明它只是数据库技术层。
Host Control use case 负责协议、当前权限和入口参数；下表的流程 owner 负责业务步骤，
Core 负责纯规则。SQL、文件、恢复记录与外部副作用仍由明确的 owner 管理。

| 用例入口 | 流程 owner | Core 规则 | SQL / 文件 | 恢复和副作用边界 |
|---|---|---|---|---|
| AutomationUseCases | AutomationDefinitionService | AutomationEdits、AutomationCatalogPolicy | AutomationDefinitionStore、AutomationRows | 单次 SQL 事务，无脚本启动 |
| ExchangeUseCases | AutomationImportService、AutomationExportService | 资产准备与定义规则 | 资产计划、ImmutableBlobStore | 持久受理后按资产恢复规则发布 |
| ExecutionUseCases | ExecutionControlService、ExecutionAdmissionService、ExecutionPlanBuilder | ExecutionSelectionPolicy、准入规则 | 来源行、冻结计划、runAttempt journal | 接受后计划冻结，资源租约约束副作用 |
| ExecutionRuntimeConsumer | AttemptRuntimeSession、ExecutionRuntimeStep | RetryDecision、执行与判定规则 | ExecutionAttemptProjection、writer / result rows | 实际 Job 停止并收集输出后恢复配置 |
| CompletionActionService | CompletionIntentSource、ExecutionRunFinality | 完成操作政策 | 完成 intent / receipt | 本地授权、drain、原生调用与宿主退出分别验证 |
| ValueUseCases | ValueService、NativeEditorService、RunCompletionService | 配置补丁、同步和恢复规则 | 配置修订、交换计划 | 验证 writer 停止及物理身份；业务成功不代替恢复证明 |
| Host 日程生命周期 | ScheduleRuntimeService、PersistentScheduleService | ScheduleMinutePolicy、StartupQueuePolicy | occurrence、watermark、已接受来源 | 未接受等待项可重验证；已接受计划冻结 |
| RecordUseCases | ContentRetirementService | 留存及 owned cleanup 规则 | RunRecordStore、引用与清理计划 | 退役和删除分离；删除前核对当前引用及精确对象 |
| NotificationUseCases | NotificationCatalogService、NotificationDeliveryService | NotificationRoutingPolicy | 任务、投递 intent / receipt | 网络结果未知时不自动重放 |
| ExtensionUseCases | ExtensionPackageService、ExtensionApplyCoordinator、InstalledActivationService | 扩展适用性与更新规则 | 已安装修订、候选、发布计划 | 使用期引用、激活门禁与 payload 恢复独立 |
| SettingsUseCases、秘密端点 | HostSettingsService、SecretFieldService、ControlTokenService | HostSettingsPolicy、秘密 keep / set / clear | 设置修订、密文、秘密准备记录 | 普通 DTO 无敏感明文；秘密发布及撤销独立验证 |
| HostMaintenanceController | HostUpdateService、MaintenancePendingStore | AutomaticUpdatePolicy | 可信 feed、维护操作、payload journal | drain、实际跨进程交接、候选验证 |

StateDatabase 单写门、InstanceLease、ResourceLeaseManager 和 RecoveryIndex 提供已有的公共约束，
不成为第二个业务状态 owner。普通启动由 StateDatabase.OpenCurrent 与 StartupJournalRecovery 处理身份、格式及当前恢复候选；显式完整诊断通过 JournalStartupAdmission 检查历史库存，
各 journal kind 的 typed owner 负责自己的计划与物理证据；未知副作用不自动重放。

执行 writer 和取消入口直接调用 ExecutionAttemptProjection，并传入当前 ContractReader。
ExecutionRunFinality 由 Platform 完成操作消费者直接使用；Core 保留纯决定数据及有消费者的
终态 observer 边界。配置解析、通知路由、插件使用期及实例租约仍是实际使用的独立边界。

当前队列主线：Host AutomationUseCases/ExchangeUseCases 校验敏感动作权限 → ExecutionAdmission 冻结输入 → BindingSkipStore 使用 Core RetryDecision 分类 → CompletionIntentSource 统一完成候选 → LocalCompletionAuthorization 核真实来源 → CompletionActionStore/Effects 持久倒计时及效果。
配置主线：ValueService 拥有输入并传类型 → ConfigurationPatchService 保留资源租约前后幂等查询/CAS → ConfigurationRevisionStore 持久发布。
调度主线：ScheduleRuntimeService → ScheduleOccurrenceStore（内部调用 ScheduleMinutePolicy）→ 等待准入；未知时区诊断仅影响该日程。
JournalStartupAdmission 在显式完整诊断的一次核验作用域复用同一只读 StateDatabase，检查全部历史与停止证明。普通恢复只核对当前候选，完成历史在对应读取、维护或诊断时验证。当前 schema 为唯一准入，旧或未知原字节只读拒绝。

## 运行配置绑定证据

本批收敛配置准入、进度与完成、重试接续、冷恢复共用的运行绑定。Execution 负责读取父 attempt、冻结执行计划与具体调用，提供已绑定的账号、步骤、writer 命令、调用计划引用和重试来源校验。Configuration 以这些事实核对自己的资源成员、journal 注册、修订、摘要与现场，继续负责进度、还原和发布。快照完成由 Execution 校验及追加父 journal 中的 intent，Configuration 校验配置会话与实际已还原现场。

实施顺序为集中执行证据的读取与复核，再接通普通调用、已核验父计划的重试读取和冷启动预检。父子绑定、调用计划覆盖、原有读取与写入时的复核继续在当前事务、实例租约及资源拥有期内执行。继续使用原有 writer 停止证明和完成资格合验，保持持久格式和公开协议。

| 验收 | 可观察结果 |
|---|---|
| 依赖边界 | 配置绑定、完成与恢复路径只消费类型化的执行事实和操作。Roslyn 检查这些入口的执行编解码器引用。 |
| 正常收尾 | 原有配置进度、原现场和终态回执正确；writer 未核验停止、成员或摘要错绑时拒绝发布。 |
| 重试接续 | 同一运行的下一调用读取上一轮已封存进度；新 attempt 使用已登记的来源；篡改调用归属时拒绝接续。 |
| 冷恢复 | 只读启动预检校验完整绑定；中断的 intent 由现有恢复 owner 接续，使用原操作身份和同一发布边界。 |

本批使用现役 Storage 定向方法覆盖配置收尾、重试进度、回执原子性和冷启动恢复，并在同一验证批次运行源码与架构边界检查。

### 配置收尾成员清单

Execution 以已核验的父 journal 和各调用的步骤、冻结配置计划引用发起收尾资格核对。Configuration 在现有只读事务内核对会话、主体与资源操作，再核对冻结计划的完整覆盖、登记状态和子 writer 停止证明，返回已核验的会话。父作用门、父 writer 停止、未进入执行时的现场租约、已执行时的采集证明和最后一次登记表修订核对由 Execution 继续负责。

| 验收 | 可观察结果 |
|---|---|
| 依赖边界 | Roslyn 拦截执行收尾入口对 Configuration 私有编解码器和会话行实现的引用，允许调用配置清单核验。 |
| 收尾成员 | 合法的已登记、已安装和已还原成员可进入现有收尾流程。外来主体、错误计划、缺失成员、隔离子项或未核验停止的 writer 仍被拒绝。 |
| 恢复与重试 | 已识别的原收尾意图按原操作 ID 恢复。损坏证据在只读预检中拒绝并保留原字节，已完成回执继续可重放。 |

使用现役 Storage 的收尾资格、停止证明、启动预检及收尾回执用例验证，并运行同源 Roslyn 检查及反例。

### 后继准入、重试和终态的配置现场

Execution 从已核验的调用历史提供步骤、冻结配置计划引用、调用序号和未派发判定。Configuration 在调用方原有的 SQLite 事务内核对还原登记和组完成状态，并通过同一个现场证据 owner 持有物理读租约。已登记子项沿用 journal、恢复现场和 writer 停止核验。允许未登记的子项仍需证明对应调用没有配置会话、journal 和工作区，原目标与冻结快照匹配。后继准入、重试追加和终态提交各自在原有作用域内保持租约。

| 验收 | 可观察结果 |
|---|---|
| 依赖边界 | 现场持有和登记核验通过 Configuration 的类型化入口。Roslyn 拦截执行侧两处入口重新引用私有编解码器或会话行。 |
| 后继与重试 | 现场已还原但前置组未提交时仍拒绝准入。完成后放行。普通重试接续已封存进度，最终还原原目标。 |
| 未派发与终态 | 真实取消、预算停止和冷恢复按现有证据处理未派发步骤。仅在核验登记与现场后发布终态，回执写入失败仍按原事务回滚。 |

复用执行顺序、普通进度、未派发取消、实际恢复及终态回执的定向用例。

## 调度来源与通知队列证据

Scheduling 核验定时与手动调用的来源、开始记录、执行预算、允许运行的通知任务和当前回调权限。通知提交通过调度提供的绑定借用原运行资源，并在原提交事务内复核调用仍然开放。持久来源读取提供已经核验的操作、运行和开始证据，供投递、回读与恢复复用，包括已结束的来源调用。

Notifications 构造提交入口，拥有通知目录、目标与凭据快照、投递成员、待投递索引和队列活动。入队在同一事务内发布来源与全部投递，提交后再用同一已核验来源校对运行身份和完整投递成员。调度与通知共用中立的 SQLite 命令构造和持久计划格式规则，沿用现有持久字节、资源租约、队列注册代次和回执协议。

| 验收 | 可观察结果 |
|---|---|
| 依赖边界 | 通知入口和回读只消费调度绑定与类型化证据。调度来源与队列读取通过自身 owner 和中立的持久化规则。Roslyn 检查两个方向的私有实现引用。 |
| 队列触发 | 定时与启动队列按原身份触发。等待中的队列重验来源，已受理调用保持冻结，重复扫描和重启沿用原有去重规则。 |
| 通知投递 | 定时与手动通知按原目标发布。篡改来源、失效回调或不完整扇出被拒绝，扇出失败整体回滚，提交后的队列活动保留到工作结清。 |
| 重试与去重 | 同一请求返回原受理结果，冲突请求拒绝。发布失败可沿原来源接续，确认已发送的回执重试不重复投递，网络结果未知保持未知。 |

统一运行现役 Storage 的队列触发、等待来源、定时／手动通知、原子扇出、恢复和投递去重用例，并装配同源完整 Test 软件。

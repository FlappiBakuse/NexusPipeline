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
不成为第二个业务状态 owner。恢复从 JournalStartupAdmission 和 StartupJournalRecovery 进入，
各 journal kind 的 typed owner 负责自己的计划与物理证据；未知副作用不自动重放。

执行 writer 和取消入口直接调用 ExecutionAttemptProjection，并传入当前 ContractReader。
ExecutionRunFinality 由 Platform 完成操作消费者直接使用；Core 保留纯决定数据及有消费者的
终态 observer 边界。配置解析、通知路由、插件使用期及实例租约仍是实际使用的独立边界。

当前队列主线：Host AutomationUseCases/ExchangeUseCases 校验敏感动作权限 → ExecutionAdmission 冻结输入 → BindingSkipStore 使用 Core RetryDecision 分类 → CompletionIntentSource 统一完成候选 → LocalCompletionAuthorization 核真实来源 → CompletionActionStore/Effects 持久倒计时及效果。
配置主线：ValueService 拥有输入并传类型 → ConfigurationPatchService 保留资源租约前后幂等查询/CAS → ConfigurationRevisionStore 持久发布。
调度主线：ScheduleRuntimeService → ScheduleOccurrenceStore（内部调用 ScheduleMinutePolicy）→ 等待准入；未知时区诊断仅影响该日程。
JournalStartupAdmission 在一次核验作用域复用同一只读 StateDatabase；仍检查全部历史与停止证明。当前 schema 为唯一准入，旧或未知原字节只读拒绝。

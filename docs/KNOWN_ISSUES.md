# 已知问题台账（Known Issues）

**建立日期**：2026-08-23（v0.9.1 审计基线）
**审计基线**：`98fb951b4da6a6177045b3bd93107cc65ab25c91`（v0.9.1）
**维护口径**：本台账记录《NexusPipeline 后续开发报告.md》确认的缺陷、边界风险和语义保留项；版本修复归属以报告的问题归属表为准。

> v0.9.2 开工前已完成代码路径核对和基线单元测试。v0.9.2 条目均能通过确定性的最小场景或当前调用链稳定命中；报告明确安排到 v0.9.3/v0.9.4 的项目保留在台账中，等待对应专项回归。已确认的产品契约不登记为缺陷，修复时必须保持。

## 复现状态说明

- **稳定复现**：可由固定输入、确定的调用顺序或最小测试场景重复命中，或代码路径已经具有无外部时序依赖的确定结果。
- **待专项复现**：报告已确认风险方向，但需要自重启、进程快照失败、真实远程模拟器或长时调度等专用环境；当前版本保守保留。
- **语义保留**：与用户确认的产品语义有关，保持现状并补充文档/测试，不按缺陷修复。
- v0.9.2 目标条目在实现过程中必须先转化为 regression test，再修改状态机或事务边界。

## v0.9.2：Correctness & Safety Hotfix

| 编号 | 已知问题 | 稳定复现/确认方式 | 代码位置 | 归属 |
|---|---|---|---|---|
| #37 | PreRun 失败后仍可能继续进入 PostRun，Attempt 边界不完整 | 固定失败 PreRun 脚本即可命中后续 PostRun 调用 | `src/Services/Execution/ExecutionCoordinator.cs` | v0.9.2 |
| #38 | `PreRunOnceOnly` 按 Attempt 1 判断，PreRun 首次失败后后续 Attempt 会被跳过 | Attempt 1 让 PreRun 返回失败、允许 Retry，Attempt 2 的现有条件稳定跳过 | `src/Services/Execution/ExecutionCoordinator.cs` | v0.9.2 |
| #39 | `PostRunOnFinalOnly` 使用 `attemptNo >= maxAttempts`，提前成功/致命结束时漏跑 PostRun | Attempt 1 success 或 fatal、最大次数设为 3，现有条件稳定不执行 FinalOnly PostRun | `src/Services/Execution/ExecutionCoordinator.cs` | v0.9.2 |
| #40 | PostRun 结果会替换整个 Main result，可能覆盖 Main fatal、Judge 结果和原因 | Main 返回 fatal、PostRun 返回普通 failed 时现有赋值顺序稳定降级结果 | `src/Services/Execution/ExecutionCoordinator.cs` | v0.9.2 |
| #41 | Config Prepare 失败可提前 return，绕过统一 FinalizeRun | 构造配置准备失败场景，`Prepare` 位于 `try/finally` 外，清理调用确定缺失 | `src/Services/Execution/ExecutionCoordinator.cs` | v0.9.2 |
| #42 | Coordinator 非预期异常不会生成 History | Runner 的异常分支只更新 `exec.Status` 和日志，未调用 History.Save | `src/Services/Execution/ExecutionRunner.cs` | v0.9.2 |
| #43 | 缺失脚本、无启用用户等 synthetic record 的 `FinalStatus` 为空 | 当前构造路径只写 `Status=failed`，序列化前字段默认值确定为空 | `src/Services/Execution/ExecutionRunner.cs` | v0.9.2 |
| #45 | Legacy History fallback 仍被保留并由 API 使用 | `HistoryService.ReadLegacyScriptLog` 与 `ApiHistoryHandler` 调用链可直接命中 | `src/Services/History/HistoryService.cs`, `src/Web/ApiHistoryHandler.cs` | v0.9.2 |
| #46 | 脚本级通知等待整个 Queue 完成，实例完成时点被延后 | Queue runner 完成所有 `RunUsersAsync` 后才统一遍历通知 | `src/Services/Execution/ExecutionRunner.cs` | v0.9.2 |
| #53 | SystemAction 取消先清理 Host 状态，再调用 OS 取消；OS 失败时 Admission 会重新开放 | `TryCancelPending` 在 `SystemActionExecutor.Cancel` 调用 OS 前已清空 pending | `src/Services/Execution/ExecutionStateStore.cs`, `SystemActionExecutor.cs` | v0.9.2 |
| #55 | Settings PUT 直接修改全局对象，后续字段失败后内存仍保留前面修改 | 一个合法字段后跟非法 `historyRetentionDays` 即可观察请求失败但对象已变 | `src/Web/ApiSettingsHandler.cs` | v0.9.2 |
| #56 | Settings 并发 PUT 共享可变对象和相同临时文件，事务边界不完整 | 两个并行 PUT 均在同一全局 Settings 上绑定/保存，当前代码无 mutation lock | `src/Web/ApiSettingsHandler.cs`, `src/Persistence/ConfigStore.cs` | v0.9.2 |
| #61 | Script/Queue POST 的限额检查和最终 Add/Save 分属两个锁区间 | 第一次检查后暂停并并发提交，第二次提交路径没有重新检查数量/Index | `src/Web/ApiScriptsHandler.cs`, `src/Web/ApiQueuesHandler.cs` | v0.9.2 |
| #62 | Script 删除先递归删除用户数据，再保存 metadata；metadata 保存失败会留下不可恢复状态 | `RemoveScriptData` 位于 `SaveScripts` 之前，注入保存失败即可稳定命中顺序缺陷 | `src/Web/ApiScriptsHandler.cs` | v0.9.2 |
| #64 | Scheduled process conflict 被当作永久失败，触发 occurrence 被消费 | `ExecutionPlanBuilder` 抛普通 `InvalidOperationException`，Scheduler generic catch 走永久失败 | `src/Services/Execution/ExecutionPlanBuilder.cs`, `Scheduler.cs` | v0.9.2 |
| #69 | 有 EditSession 时仍允许 restart | restart handler 只检查 `Center.Active`，编辑会话字典不在门禁条件内 | `src/Web/ApiSettingsHandler.cs` | v0.9.2 |
| #70 | Pending SystemAction 时仍允许 restart | restart handler 未检查 `Center.CurrentSystemAction` | `src/Web/ApiSettingsHandler.cs` | v0.9.2 |
| #74 | `KillAndConfirmExited` 返回值在运行收尾被丢弃 | `RunAttemptFinalizer.KillScript` 为 void，Coordinator 随后继续 replace/retry/finalize | `src/Services/Execution/RunAttemptFinalizer.cs`, `CleanupManager.cs`, `ExecutionCoordinator.cs` | v0.9.2 |
| #76 | 进程树部分清理失败只写 Warn，最终只按主进程名判断 | `KillTree` 无结构化结果，确认逻辑无法知道已枚举的剩余 PID | `src/Utilities/SystemActions.cs` | v0.9.2，并在 v0.9.4 深化 |
| #78 | Toolhelp 快照失败时 `/T` fallback 会跳过 Game exclusion | `FallbackKillTree` 无排除参数，快照异常路径会递归杀整棵树 | `src/Utilities/SystemActions.cs` | v0.9.2 |
| #81 | 远程 ADB endpoint 仅按端口进入本机 MuMuManager 控制，可能误关本机实例 | `ShutdownEmulatorAsync` 当前只解析端口，未先区分 loopback/远程 host | `src/Services/EmulatorSupport.cs` | v0.9.2 |
| #83 | ADB 外部命令收到取消时没有稳定清理 child process | `WaitForExitAsync(token)` 的取消异常不经过 `TryKill` 路径 | `src/Services/EmulatorSupport.cs` | v0.9.2 |
| #85 | Wildcard/directory 日志轮换后从头读取旧候选文件内容，污染当前 Attempt | 初始存在旧 A.log，运行后切换到 B.log；现有 rotation 分支显式 `readFromStart=true` | `src/Services/Execution/ExecutionCoordinator.cs`, `LogMonitor.cs` | v0.9.2 |

**v0.9.2 修复验证结果（2026-08-23）**：上述目标条目均已完成修复并转为回归保护。验证包含完整单元测试 178/178、管理员构建、加速 E2E 87/87、judge 150/150、chaos 166/166，以及真实计时 E2E 87/87、judge 150/150、chaos 166/166，均无失败；构建仅保留基线已有的 3 条 nullable 警告。#41 额外覆盖 `FinalizeRun()` 的重复调用幂等性。

## v0.9.3：Scheduler / Persistence / Lifecycle Consistency

| 编号 | 已知问题 | v0.9.3 发布状态 | 代码位置 | 归属 |
|---|---|---|---|---|
| #44 | History persistence failure 只写宿主日志，外部运行状态缺少提示 | 已修复 | `src/Services/History/HistoryService.cs`, `ExecutionRunner.cs` | v0.9.3 |
| #47 | Notification channel 可无限占用 completion | 已修复 | `src/Services/Notification/NotificationDispatcher.cs` | v0.9.3 |
| #48 | Scheduled occurrence 未在 trigger 时冻结执行计划 | 已修复 | `src/Services/Scheduling/Scheduler.cs` | v0.9.3 |
| #49 | 用户修改后 pending plan 不主动重新校验 | 已修复 | `src/Services/Scheduling/Scheduler.cs` | v0.9.3 |
| #50 | 同 minute restart 可能重复 trigger | 已修复 | `src/Services/Scheduling/Scheduler.cs` | v0.9.3 |
| #51 | Scheduler 短暂停顿跨过 minute 时可能 missed-fire | 已修复 | `src/Services/Scheduling/Scheduler.cs` | v0.9.3 |
| #52 | backlog 可持续积压 | 这是已确认的风险语义，报告要求允许 backlog，不做数量裁剪 | `src/Services/Scheduling/Scheduler.cs` | 语义保留 |
| #54 | ResultCollector 的“20MB”按 .NET chars 计数，实际字节数可能超过 20MB | 已修复 | `src/Services/Execution/ResultCollector.cs` | v0.9.3 |
| #57 | Plugin restart-only toggle 实际立即改变运行态 | 已修复 | `src/Plugins/PluginManager.cs` | v0.9.3 |
| #58 | InitFailed plugin 仍可能暴露 capability | 已修复 | `src/Plugins/PluginManager.cs` | v0.9.3 |
| #59 | unknown plugin toggle 返回成功 | 已修复 | `src/Plugins/PluginManager.cs`, `src/Web/ApiPluginsHandler.cs` | v0.9.3 |
| #60 | 当前不存在插件的用户偏好被 prune | 已修复 | `src/Plugins/PluginManager.cs` | v0.9.3 |
| #63 | Queue mutation 未纳入 Admission coordination，存在 TOCTOU | 已修复 | `src/Web/ApiQueuesHandler.cs` | v0.9.3 |
| #65 | Config EditSession 未作为 Admission resource，可能形成 ghost Active | 已修复 | `src/Web/ApiScriptsHandler.cs`, `ExecutionStateStore.cs` | v0.9.3 |
| #66 | Scheduler retry 重建当前 plan，可能丢失触发时状态 | 已修复 | `src/Services/Scheduling/Scheduler.cs` | v0.9.3 |
| #67 | Pending trigger 重启丢失 | 已修复 | `src/Services/Scheduling/Scheduler.cs` | v0.9.3 |
| #68 | Host exit 非 graceful，可能静默脱管 Active run | 已修复 | `src/Application/StartupPipeline.cs` | v0.9.3 |
| #71 | History JSON 直接写入，可能留下半个 JSON | 已修复 | `src/Services/History/HistoryService.cs` | v0.9.3 |
| #72 | RunRecord publish 后仍可能被 History persistence 修改 | 已修复 | `src/Services/History/HistoryService.cs`, `ExecutionRunner.cs` | v0.9.3 |
| #80 | Emulator cleanup 按当前 foreground app 关闭，可能关闭无关应用 | 已修复 | `src/Services/EmulatorSupport.cs` | v0.9.3 |
| #82 | 任意 adb command failure 被当作 emulator offline | 已修复 | `src/Services/EmulatorSupport.cs` | v0.9.3 |
| #84 | Cleanup 使用 `CancellationToken.None`，缺少独立 deadline | 已修复 | `src/Services/Execution/RunAttemptFinalizer.cs` | v0.9.3 |
| #86 | LogMonitor transient reopen 回到初始 offset，可能重读旧段 | 已修复 | `src/Services/LogMonitor.cs` | v0.9.3 |
| #87 | FileId 不可用时直接返回 false，creation-time fallback 未真正生效 | 已修复 | `src/Services/LogMonitor.cs` | v0.9.3 |

**v0.9.3 修复验证结果（2026-08-23）**：本节 v0.9.3 条目均已完成修复并转为回归保护。验证包含完整单元测试 184/184、管理员构建、加速 E2E 87/87、judge 150/150、chaos 166/166，以及真实计时 E2E 87/87、judge 150/150、chaos 166/166，均无失败；构建保留 3 条基线 nullable 警告。

## v0.9.4：Runtime Monitor & Process Ownership Hardening

| 编号 | 已知问题 | 当前确认状态 | 代码位置 | 归属 |
|---|---|---|---|---|
| #32 | Monitor、Judge、config sync、timeout、进程检查共用单循环，Judge/config sync 阻塞监控 | 已修复：独立 worker 与单飞判定/配置同步，并通过加速与真实计时回归 | `src/Services/Execution/ExecutionCoordinator.cs` | v0.9.4 |
| #36 | TotalTimeout 主要依赖主循环，Judge/config sync 阻塞期间不是严格 wall-clock watchdog | 已修复：独立 RunBudget watchdog，195/195 单测与全量回归通过 | `src/Services/Execution/ExecutionCoordinator.cs`, `RunBudget.cs` | v0.9.4 |
| #73 | 脚本强杀后短暂自重启/外部 watchdog 重新占用配置，恢复窗口尚未有稳定判据 | 已修复：稳定退出窗口覆盖 0ms、100ms、500ms、1s、3s、5s 重启时序 | `src/Services/ConfigSwap/ConfigSwapRecovery.cs`, `SystemActions.cs` | v0.9.4 |
| #75 | 瞬时无进程被当作稳定退出 | 已修复：稳定退出窗口与进程身份观察共同确认退出 | `src/Utilities/SystemActions.cs`, `ConfigSwapRecovery.cs` | v0.9.4 |
| #77 | PID 0 作为“按身份清理” sentinel，API 语义混合 | 已修复：拆分 owned PID、Job Object 和 identity cleanup 语义 | `src/Utilities/SystemActions.cs`, `ExecutionCoordinator.cs` | v0.9.4 |
| #79 | Root PID 与 GameExe 同名时会被 Game exclusion 排除 | 已修复：根进程始终保留，GameExe 排除仅作用于子进程 | `src/Utilities/SystemActions.cs` | v0.9.4 |
| #88 | Launcher 先退出后 detached child 脱离 Toolhelp 追踪 | 已修复：通过 Job Object 与进程身份模型追踪并清理归属进程 | `src/Utilities/SystemActions.cs` | v0.9.4 |

**v0.9.4 修复验证结果（2026-08-24）**：上述目标条目均已完成修复并转为回归保护。验证包含管理员构建、单元测试 195/195、加速与真实计时 E2E 87/87、judge 150/150、加速 chaos 167/167、真实 chaos 166/166，均无失败；构建保留 3 条基线 nullable 警告。

## v0.9.5：Host Notification / Emulator Drivers / Plugin API v1

| 编号 | 已知问题 | 当前确认状态 | 代码位置 | 归属 |
|---|---|---|---|---|
| #89 | 通知推送以宿主内置插件身份存在，配置、状态与业务能力边界交叉 | 已修复：Webhook/SMTP 归入宿主通知领域，代码插件通过公开通知契约调用 | `src/Services/Notification/NotificationDispatcher.cs`, `wwwroot/views/settings.js` | v0.9.5 |
| #90 | 通用 ADB 与 MuMuManager 命令混用，单次执行缺少稳定的模拟器路由 | 已修复：检测结果映射为独立 driver，并在运行期间冻结目标路由 | `src/Services/EmulatorDrivers.cs`, `ExecutionCoordinator.cs` | v0.9.5 |
| #91 | 插件契约依赖宿主内部类型，第三方 managed-code 插件缺少稳定 API 入口 | 已修复：新增独立 `NexusPipeline.Plugin.Abstractions` API v1 程序集 | `src/NexusPipeline.Plugin.Abstractions/PluginApi.cs` | v0.9.5 |
| #92 | 托管代码插件缺少隔离加载、版本兼容、配置、密钥和后台任务宿主边界 | 已修复：manifest、隔离加载、状态机、插件配置、DPAPI 密钥与调度服务已建立 | `src/Plugins/Managed/`, `src/Plugins/PluginManager.cs` | v0.9.5 |

**v0.9.5 修复验证结果（2026-08-24）**：上述目标条目均已完成并转为回归保护。验证包含管理员构建、单元测试 197/197、加速与真实计时 E2E 87/87、judge 150/150、chaos 166/166，均无失败；构建保留 3 条基线 nullable 警告。

## 已有保留项与语义确认

| 编号 | 内容 | 决策 |
|---|---|---|
| KN-09 | 日志 truncate 后立即写入时可能出现漏判窗口，文件系统缺少可靠截断点身份 | 保留现状；不通过截断后无条件从头读来换取表面修复 |
| KN-73 | Mutex 在 action 执行期间被 Dispose 的低概率窗口 | 当前未稳定复现，保持保守处理并纳入 v0.9.4 专项观察 |
| KN-80 | `AutoUpdateConfig=false` 仍执行首次约 15 秒同步，收尾不同步 | 用户已确认的产品语义，保持现状 |
| KN-81 | 快速失败脚本可能错过首次检测同步 | 用户已确认可接受；开关开启时由收尾同步兜底 |

## v0.9.2 修复门槛

v0.9.2 完成前，目标条目必须满足：

- 每个状态机、事务、清理和日志轮换问题都有可重复 regression test；
- 测试先固定报告中的产品契约：Retry、AutoUpdateConfig 首次同步、队列串行/并行、Judge、CompletionAction、失败继续队列和配置安全优先级；
- `KNOWN_ISSUES.md` 与代码、ROADMAP、CHANGELOG 的问题归属保持一致。

# 状态、普通重试与冷恢复

本轮恢复 main 已确认的产品逻辑，同时保留 next 的 engine、business、lifecycle、recovery 事实分离。具体恢复目标已由用户确定；不要通过扩大 Unknown 范围或把全部 Unverified 改成 Failed 来省略真正的条件判断。

## 三种后续动作

| 动作 | 触发与身份 | 允许依据 |
|---|---|---|
| 同次运行的普通失败重试 | 当前绑定生成下一次实际调用，保留原 run 与父 attempt | 普通失败、调用额度、总预算、停止与配置恢复 |
| 中断后的安全后继 | 新 attempt，保留原 run、业务身份与旧证据 | 冻结的安全声明、Host 批准、真实停止、恢复及可信预算 |
| 重新读取原操作结果 | 原 operationId，只查询 | 原主体和请求绑定，不重新发出业务效果 |

普通失败重试不要求脚本声明自己能在 Host 崩溃后安全重放。冷恢复的安全规则也不因恢复普通重试而放宽。两者的模型和编号不能互相借用。

## 本轮普通执行结果表

| 已观察条件 | 目标产品结果与后续 |
|---|---|
| 明确业务成功 | 保留成功事实，进入原收尾；不因之后展示或通知失败改为脚本失败 |
| 明确普通失败且额度允许 | 按原绑定策略重试；从上一调用有效活动配置继续 |
| 已配置判定规则，普通进程正常 exit 0，完整收集后仍没有完成或失败终态 | 恢复 main 的 Failed 结果与普通重试资格；原始“无业务完成证据”仍如实保留 |
| 没有配置判定规则 | 保留 main 与 next 已对应的按退出结束语义，不只凭 Engine.Failed 再增一条业务失败规则 |
| 取消、致命总超时、未确认停止或未完成恢复 | 不增加普通调用 |
| 受控 provider worker 缺有效终态、中断，或已有真实 Unknown 外部副作用 | 沿其现有 Interrupted/Unknown 路径处理，不把它们当成上述普通无标记退出 |

无标记分支只处理审查已证的适用普通执行。不能仅用 `Business == Unverified` 一个条件扩大重试；也不能为了通过测试伪造已经观察到业务成功或业务失败标记。结果分类、原始证据与重试资格分别表达，并在真实消费链中接通。

依据：[main 无标记退出](https://github.com/FlappiBakuse/NexusPipeline/blob/3fa803f3cb3ab59564bfad6fcf19c512dbfcd45d/src/Modules/Execution/AttemptTerminator.cs#L107-L133)、[main 普通重试](https://github.com/FlappiBakuse/NexusPipeline/blob/3fa803f3cb3ab59564bfad6fcf19c512dbfcd45d/src/Modules/Execution/RetryPolicy.cs#L1-L17)、[next 证据构建](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/src/platform/Storage/Execution/StepEvidenceFactory.cs#L63-L99)、[next 分类](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/src/core/Execution/RetryDecision.cs#L39-L65)。

## 普通重试的执行顺序

收集实际结果与活动配置，停止并确认全部本次 writer，按现有 owner 还原原现场，再判断剩余调用和预算。允许时，使用上一调用已封存有效输出准备下一配置输入，启动新的实际 Job；保留原绑定与冻结授权。

`MaximumAttempts` 限制实际调用总数，不能因为重试、等待、中断或重新打开页面补发额度。总时间预算跨普通调用共用；每次无输出等待按现役规则观察。已成功的前序绑定不因当前绑定失败全部重跑。

前置成功一次与后置仅最终调用的策略继续消费真实结果、停止和剩余额度。没有已证明的下一次调用时，不能为等待一个并不存在的重试而无限延后收尾。此处不重写 hook 协议。

配置输入与长期 publication 的详细约束见[配置生命周期](configuration.md)。

## 冷恢复

Host 丢失后，先证明旧 creator 与 writer 停止，再恢复或隔离对应资源。缺少可靠停止证据时，不能把进程 PID 不存在或 Job 名称相同当成恢复授权。

无法证明预算连续时，不从当前墙钟重建一份新的剩余时间。保存原运行、尝试和未知效果事实，不自动重新执行外部回调、发送通知、签到或系统动作。安全后继继续要求原声明及 Host 策略同时满足。

恢复将状态收敛到可解释结果；它不是“启动后把未成功的任务全部再跑一遍”。用户后续明确新建运行和原操作重放是不同动作。

## 诊断与最小验证

失败码必须由真实界面/诊断消费者使用，不能仅留在内存字段。捕获边界应表达当前已知结果；本地参数准备失败与“外部请求可能已提交”必须区分。

统一验证只选本次改变的判据及其消费链：无标记普通执行确实按额度再调用、未知真实效果不重放、进度接续但不增预算、已成功前序不重复。已有冷恢复或 hook 用例仅在相应行为被触及时补验。私有方法调用顺序、对象引用相同或枚举字符串数量不作为这些假设的替代。

精确协议仍见 [execution-outcomes](../../contracts/schemas/execution-outcomes.schema.json)、[执行](execution.md)及[调度](scheduling.md)。旧调度参考中涉及本轮已授权恢复的部分，以本页目标和最终实际证据更新；其他字段和副作用边界不变。

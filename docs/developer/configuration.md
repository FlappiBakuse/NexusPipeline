# 配置生命周期与恢复目标

本页是本轮配置行为的现行目标。用户已授权优先恢复 main 的普通重试接续、首次快照和同步缩减语义，不再为这些已证差异重复裁决。当前实现缺口见[状态表](../status.md)；新格式与 next 的数据保全约束继续生效。

## 四种事实要明确区分

| 事实 | 用途 | 不能替代什么 |
|---|---|---|
| 冻结来源与绑定身份 | 确定本次运行授权、配置合同和资源归属 | 不能强制每次普通重试重新使用最初字节 |
| 本次调用输入 | 进入当前脚本的实际配置 | 不能从后来修改的账号快照偷偷取新意图 |
| 上一调用活动输出 | 真实写者产生、已验证并封存的进度 | 不能用长期同步是否开启决定它是否存在 |
| 账号长期 publication | 后续独立运行使用的保存修订 | 不能代替外部原现场，也不能把操作重放再设成当前指针 |

运行或编辑前的外部原现场另有明确保全，结束时恢复它。原现场恢复与账号快照是否更新分别处理；同一事实只由现有配置会话及事务 owner 持有。

## 首次配置

**目标：**通用脚本第一次普通运行在尚无账号保存快照时，从该绑定已声明且能够核验的现场自动建立初始快照，恢复 main 的直接使用路径。已有快照时不自动再导入；不能把未知路径、缺失文件或空目录当成默认配置。

复用现有导入与资源准入能力，把首次建立接到受理/准备流程中的明确位置。绑定身份、脚本根、资源声明、预期修订、全部资源一致提交及操作幂等继续有效；不能增加任意路径读取 API，也不为绕开失败生成空 revision。

“读取现有配置”继续作为用户主动更新快照的独立动作。首次自动建立仅对无快照场景成立，不等于 0.17 数据导入或运行中随时重新认领现场。

依据：[main 首次建立](https://github.com/FlappiBakuse/NexusPipeline/blob/3fa803f3cb3ab59564bfad6fcf19c512dbfcd45d/src/Modules/Configuration/Exchange/ConfigExchangeService.cs#L77-L82)、[next 当前前置要求](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/src/platform/Storage/Configuration/ConfigurationRunPreparation.cs#L74-L82)。

## 普通重试接续

**目标：**同一运行内，普通失败后的下一调用继承上一调用的活动输出。例如初始 `checkpoint=0`，第一次写 1 并失败，第二次读取 1、写 2 后成功。自动长期同步开关开或关，都不应把下一调用重置回 0。

继续使用 next 的独立调用 Job、配置会话、采集与还原流程。可从已校验、封存且仍受拥有权保护的上一输出构造下一输入；不能为了保持旧语义而让旧 writer 继续写，不能跳过还原或把未确认字节作为输入。

冻结的原始意图继续限制绑定、权限、任务选择和预算。source identity、retry input 与 publication cursor 必须分开；专项 TaskRetry 只能改变它获准选择的业务任务，不能作为通用进度接续的唯一入口。

长期保存关闭时，活动输出仍可供同次普通重试；本次结束后是否推进账号保存修订按原设置。跨 run、独立队列重复项、冷恢复后继不能借这个规则自动继承未获授权的状态。

依据：[main 活动配置复用](https://github.com/FlappiBakuse/NexusPipeline/blob/3fa803f3cb3ab59564bfad6fcf19c512dbfcd45d/src/Modules/Configuration/Exchange/ConfigSwapSession.Synchronization.cs#L70-L101)、[next 普通/专项输入选择](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/src/platform/Storage/Configuration/ConfigurationRunPreparation.cs#L74-L97)。

## 自动同步

保留现有首次运行满 15 秒后的单次检查与结束同步；结束同步不因运行短于 15 秒而一概跳过。同步失败应保留旧快照并公开可诊断原因，不能只写无人消费的内存 failure code。

**恢复 main 的判据：**

- 缺失、空内容、声明内容无效或稳定性/身份无法确认时不提交。
- 单文件内容合法、非空时，不根据相对字节缩减比例单独拒绝；合法 JSON 紧凑化可以通过。
- 目录非空且内容有效；与已保存目录比较，文件数减少到一半及以下时拒绝，即 `candidateFiles * 2 <= savedFiles`。
- 删除 next 新增而没有产品依据的“总字节小于原来一半”门槛，不把它换成另一个任意阈值。

保留 next 的路径形态、声明权限、文件身份、停止/稳定性、事务与修订校验。精确同步策略比较的是适用的已保存快照，不引入一份永久最初基线阻止以后所有合法收缩。

依据：[main ValidForSync](https://github.com/FlappiBakuse/NexusPipeline/blob/3fa803f3cb3ab59564bfad6fcf19c512dbfcd45d/src/Modules/Configuration/Exchange/ConfigSwapSession.Synchronization.cs#L110-L152)、[next FileTreeDelta](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/src/core/Configuration/FileTreeDelta.cs#L40-L59)。

## 编辑、补丁与专项修复

已有绑定从“编辑配置”进入原生编辑事务，复用确定的程序与资源，不让用户重复拼装技术计划。只有无法推导的实际选择才在界面要求提供。编辑保存与取消都先证明写者停止并还原外部现场；保存发布获准输出，取消保留原快照。

受授权修复仍绑定候选摘要、当前安装/绑定/配置修订、准确字段范围与受保护字段。运行 blocking diagnostic 表示不能执行当前任务，不自动表示不能应用消除问题的修复。候选变化、授权不足和来源变化才按各自原因阻止修复，不能全部归为同一个“提案已变”。

结构补丁只改授权值，保留其他字段、未知任务与范围外原字节；格式与选择器细节由 [config-patch schema](../../contracts/schemas/config-patch.schema.json)和生产解析器定义。保留 JSON/JSONC/YAML 的显式声明，不新增猜格式 reader。

依据：[Host 当前修复入口](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/src/platform/Storage/Extensions/TaskAdapterService.cs#L127-L167)。

精确快照、CAS、批量事务和编辑协议见[配置存储参考](configuration-storage.md)。

## 最小产品验证

本页只定义要证明的假设，不要求现在逐条运行。全部选定代码完成后，把相同夹具合并进统一验证：普通脚本 0→1 失败→2 成功并区分长期同步；有/无首次快照不互相覆盖；合法紧凑化可提交、目录恰好减半仍拒绝；编辑取消恢复原现场；blocking 诊断下的合法修复可完成，越权仍拒绝。

已有恢复、跨卷和身份用例仅在本轮改变对应边界时补验，不因为配置目录被改过就自动运行全部 Storage。不要用“某私有方法被调用一次”或索引名称代替这些产品结果。

## 用户绑定首次选择

专项实例的目录识别只建立默认资源声明。用户首次编辑配置时选择目录中的候选，Host 在已安装适配器与来源租约内重新解析候选，将配置快照和绑定的来源、路径、启动参数一起提交到现有配置批次。`$script-source` 是该绑定的不可覆盖来源记录；已有快照继续使用原声明。原生编辑、任务发现和冻结运行计划消费同一绑定来源，导入不修改脚本文件。首次选择使用原 operationId 查询和重放，保留 installed、script、binding 版本及文件身份校验。

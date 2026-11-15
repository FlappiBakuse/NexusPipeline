# 配置生命周期

本页解释当前配置事实与流程；快照格式、CAS、补丁和原生编辑参数由[配置存储](../reference/configuration-storage.md)维护。未完成验收集中于[当前状态](../status.md)。

## 四类事实

| 事实 | owner 与用途 |
|---|---|
| 冻结来源与绑定 | 执行准入固定绑定、权限、任务意图与预算 |
| 当前调用输入 | 配置准备为本次实际进程提供确定内容 |
| 上一调用活动输出 | 真实 writer 产生，经停止、校验和封存后供同次重试接续 |
| 账号长期 publication | 配置修订保存后续独立运行使用的快照 |

运行或编辑前的外部原现场独立保全，结束后还原。长期保存开关决定账号 publication，活动输出接续由当前运行管理。

## 首次建立与主动读取

通用脚本尚无保存快照时，`ExecutionAdmissionPort.Identity` 在新操作准备中调用 `InitialConfigurationSnapshots.Establish`，从已声明且可核验的来源建立初始快照。已有操作先查原结果，已有快照保持原值。定义、资源、绑定修订和批量原子提交继续核验。

用户主动“读取现有配置”使用独立导入用例。它更新明确绑定的账号快照，不启动脚本、不写上游原文件；未知路径、缺失或空内容不会生成默认快照。专项的来源与 preflight 由自身声明约束。

代码：[准入入口](../../src/platform/Storage/Execution/ExecutionAdmissionPort.cs)、[首次快照](../../src/platform/Storage/Configuration/InitialConfigurationSnapshots.cs)、[主动导入](../../src/platform/Storage/Configuration/ConfigurationImportService.cs)。

## 普通重试接续

同一 run 内，普通失败后的下一 invocation 使用上一调用已封存的有效输出。例如 0→1 后失败，下一次从 1 继续并写入 2。每次调用使用独立实际 Job、配置会话、采集和还原；冻结意图仍限制任务、绑定、权限及预算。

长期保存关闭时，活动输出仍参与同次普通重试。本次结束是否推进账号修订按原策略处理；跨 run、独立队列项及冷恢复后继使用各自准入。

代码：[运行准备](../../src/platform/Storage/Configuration/ConfigurationRunPreparation.cs)。重试身份与分类见[状态与重试](state-and-retry.md)。

## 自动同步

初次检测在实际运行满 15 秒后尝试一次；结束同步使用独立触发，不因短于 15 秒直接跳过。同步失败保留旧 publication 并公开原因。

- 来源缺失、空内容、内容无效或对象身份/稳定性失效时拒绝发布。
- 合法非空单文件允许紧凑化；总字节缩减比例不作为单独拒绝条件。
- 目录文件数满足 `candidateFiles * 2 <= savedFiles` 时拒绝收缩；形态冲突保持拒绝。
- 当前文件身份、资源声明、停止事实、修订和事务均由原 owner 核验。

规则权威：[ConfigurationSynchronizationPolicy](../../src/core/Configuration/FileTreeDelta.cs)。

## 编辑、补丁与修复

已有绑定的“编辑配置”消费已绑定的程序与资源，保存和取消均等待实际 writer 停止并还原现场。保存发布授权输出，取消保留原快照。

专项修复绑定候选摘要、当前安装/绑定/配置修订和精确写范围。运行 blocking 诊断由当前运行准入处理；合法修复按候选重新核验，受保护字段和越权仍拒绝。结构补丁只修改授权值，其他字节保留。

专项首次选择通过已安装 adapter 解析候选，同一配置批次提交快照和绑定来源。`$script-source` 记录该绑定不可覆盖的来源；原生编辑、发现和计划消费同一来源。原 operationId 用于查询和重放。

代码：[TaskAdapterService](../../src/platform/Storage/Extensions/TaskAdapterService.cs)；格式权威：[config-patch schema](../../contracts/schemas/config-patch.schema.json)。

## 验证入口

按本次修改选择首次快照、0→1→2 接续、紧凑化、目录恰好减半、编辑取消和合法修复等产品假设，合并到[统一验证](../development/validation.md)。原生恢复与跨卷仅在对应边界受影响时加入。

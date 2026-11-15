# 历史工程记录

这些页面保留 P1 后端阶段的需求背景、用户选择及当时的定向证据。它们的范围、版本、方法数量和授权属于原记录；当前开发从[文档导航](../README.md)和[当前状态](../status.md)进入。

| 记录 | 保留内容 | 当前事实入口 |
|---|---|---|
| [业务补修](p1/business-preservation.md) | 并发、容量、留存、更新及通知的补修背景 | [执行](../reference/execution.md)、[存储](../reference/storage.md)、[调度](../reference/scheduling.md)、[维护](../operations/installation.md) |
| [补充开发](p1/p1-supplement.md) | 原任务范围与用户选择 | [状态与重试](../architecture/state-and-retry.md)、[自动化](../reference/automation.md) |
| [前端消费约定](p1/p2-contract.md) | P1→P2 交接时的消费语义 | [控制面](../reference/control-http.md)、[前端](../architecture/frontend.md) |
| [发现处置](p1/supplement-findings.md) | 原 28 项发现及证据批次 | 对应领域参考与原始回执 |

原有设计决定继续在 `decisions` 维护。原始测量、失败现场和账号数据保留于所属仓外验收材料，历史记录不成为构建或测试依赖。

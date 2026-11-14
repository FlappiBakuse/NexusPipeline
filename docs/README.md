# 文档导航与适用状态

本目录说明当前用户操作、领域实现和验证边界。用户步骤、开发契约与任务地图分别维护；未完成事项集中在[当前状态](status.md)，实际执行结果保存于对应验收回执。

## 三类入口

| 读者 | 从这里开始 | 要得到的结果 |
|---|---|---|
| 用户 | [第一次运行](user/quickstart.md) | 安装后完成一次可解释的运行并找到结果 |
| 开发者 | [本地开发](developer/README.md) | 找到业务 owner，完成选定任务并做最小有效验证 |
| LLM / Agents | [任务地图](agents/change-map.md) | 仅读取相关事实源，在明确边界内实现与验证 |

用户无需先学习数据库、journal、SDK 或构建回执。开发者需要理解的实现细节放在开发说明；机器执行约束放在 Agent 入口。不要在三处重复维护同一套完整规范。

## 规则的唯一位置

| 内容 | 权威位置 |
|---|---|
| 普通重试、结果判定、冷恢复 | [状态与重试](developer/state-and-retry.md) |
| 初始快照、编辑、同步、现场恢复 | [配置生命周期](developer/configuration.md) |
| 阶段进度、启动成本与容量范围 | [启动与容量](developer/startup-and-capacity.md) |
| 页面与公共组件 | [前端行为](developer/frontend.md) |
| 编辑草稿、恢复提示与敏感输入 | [Web 状态](developer/frontend-state.md#编辑草稿) |
| 即时设置与取消还原 | [宿主设置](developer/settings.md#即时设置与界面偏好) |
| 八库身份、事务、连接与备份 | [状态存储](developer/storage.md) |
| 默认插件初始化与外部目录变化 | [扩展](developer/extensions.md#首次初始化与目录变更) |
| 托盘退出、写入准备与签名许可 | [桌面授权](developer/desktop-authorization.md#退出与维护许可) |
| 先实现后验证、四类职责、失败后的最小补验 | [验证规范](developer/validation.md) |
| HTTP 字段与限额 | 仓库 contracts 中的现役 schema / OpenAPI |
| SDK 导出表面 | 仓库 sdk 与 contracts 中的现役定义 |
| 已实现、已验证、未运行 | [当前状态](status.md)及对应原始结果记录 |

## 文档维护

完成一个代码批次时，只修改实际受影响的用户步骤、对应开发规则和状态行。参数名或协议变化需要同步生成参考；界面文字变化只校正相关操作说明。没有新行为依据，不新增概念、教程分支或另一份字段清单。

有效命令、格式与恢复约束保留在对应现役参考；固定 Git 提交保存历史原文。其余精确技术参考继续使用。

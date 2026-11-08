# 本地开发入口

> 本页属于 2026-11-06 的本地开发对齐稿。用户已确定：优先恢复已证的 main 业务逻辑，保留 next 架构、现格式与代码风格；先完成选定任务的全部代码，再统一最小定向验证。实现状态见[当前状态](../status.md)。

## 选择一条路径

| 你修改的内容 | 先读 | 本批验证通常需要 |
|---|---|---|
| Web 页面、公共组件或集合 | [前端行为](frontend.md) | 相关 lint/typecheck、最小行为场景；需要产品资源时只构建对应资产 |
| Core 规则、配置或状态机 | [配置生命周期](configuration.md)、[状态与重试](state-and-retry.md) | 所选产品假设及其实际依赖项目；纯规则不自动启动 Web/桌面 |
| 启动、数据库、GUI launcher、安装入口 | [启动与容量](startup-and-capacity.md) | 最小数据边界、同源 Windows 产品及受影响原生入口 |
| SDK 或插件跨仓行为 | [SDK](sdk.md)及 Plugins 作者入口 | 同一 SDK 生产者与受影响真实消费者；只有实际契约变化才扩展消费者范围 |

共同执行顺序见[工作流](workflow.md)。具体命令与四类职责在[验证规范](validation.md)，不要从其他页面复制一份逐渐失效的命令大全。

## 环境与目录

完整原生开发使用 Windows 11 x64、仓库 `global.json` 锁定的 .NET SDK 10.0.402、Python 3.12+，以及前端原生锁文件允许的 Node/npm。只有当前任务需要相应组件时才准备它；不为纯文档任务安装整个原生栈。

源码、依赖缓存、构建槽、测试实例、报告和用户真实数据分开。构建与 runner 的输出根由命令显式传入，避免在仓库生成 node_modules、dist、数据库或诊断文件。复用已拥有的缓存与构建槽；报告指向本次新结果。

本地有两个仓库：Host 管理平台、控制面、Web、桌面和公共 SDK；Plugins 消费固定 SDK 实现各插件。两仓必须记录实际输入身份，不能把旧 Host SDK 配到新插件代码后称为同源成品。

## 从哪里定位业务

| 问题 | 起点 | 下一层 |
|---|---|---|
| 为什么当前绑定能运行或被拒绝 | `src/core/Execution`、Host 对应 use case | Platform 的冻结输入、配置会话与进程效果 |
| 为什么重试丢进度 | `ExecutionAttemptCoordinator`、`AttemptRuntimeSession` | `ConfigurationRunPreparation` 的 source / input / publication 选择 |
| 为什么配置不能保存或恢复 | Configuration use case | revision、资源租约、输出封存与原现场恢复 |
| 为什么界面多次请求或串账号 | 对应 page owner、typed control client | 服务端 query、记录身份与游标 |
| 为什么插件能力不可用 | 当前安装与激活事实 | SDK bridge、插件真实声明与 Host 准入 |

`Storage` 路径不等于所有业务都属于存储。新的策略只放在现有明确 owner；跨领域的流程组合留在应用编排边界，SQL、文件和 Windows 效果由适配器实现。[架构](architecture.md)说明本轮允许的局部拆分。

## 交付时必须能回答

这次改变了哪个产品假设？恢复依据是什么？哪些代码与文档实际修改？统一验证执行了哪些范围、哪些没有运行？输出属于哪个源码身份？

源代码为未提交状态时记录 `local-uncommitted`，commit/tree 为 null，参考 HEAD 单列。提交、推送和正式发布与本地实现是独立动作，不以“写好文档”或“已有候选”替代授权与真实产物。

详细命令与不可变构建来源见[命令参考](command-reference.md)。

## 精确参考

[控制面](control-http.md)、[SDK](sdk.md)、[存储格式](storage.md)、[秘密字段](secrets.md)、[进程拥有权](process-ownership.md)、[扩展](extensions.md)、[前端 ABI](frontend-abi.md)保留精确契约说明。旧阶段材料中的测试数量、历史 PASS 和暂缓安排不自动升级为本次任务要求。

依据：[固定 next 工具入口](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/tooling/nexus.py)、[SDK 锁定](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/global.json)。

# 今日概览卡片

Dashboard 拥有卡片目录与已提交布局，浏览器拥有编辑草稿和 renderer 生命周期。HTTP 并发契约见[布局 API](../reference/control-plane.md#今日概览布局)，插件注册见[Frontend API](../reference/plugin-api/frontend.md#卡片-renderer)。

## 目录与注册

`Modules/Dashboard/DashboardService` 拥有权威目录和 `config/dashboard-layout.json`。三个可编辑内置 ID 为 `core:status`、`core:running`、`core:history-duration`，分别表示系统状态、运行任务和运行历史统计。历史时长与次数属于同一个布局单元，共享一次摘要查询，内置目录提供用途说明，插件说明由登记元数据提供。

managed 插件经 `IPluginHostContext.DashboardCards.Register` 注册本地 ID，Host 从真实插件身份生成 `plugin:<name>:<id>`。重复或带冒号 ID 拒绝；插件上下文释放时注销全部句柄。组合层适配器只投影实际运行插件，并使用声明的本地化资源。

目录仅来自成功启动的 managed 登记，按 defaultOrder/cardId 排序；三张内置卡默认值为 0/100/200。首次出现按 defaultVisible 和目录顺序插入一次，包括默认隐藏卡；已有记录的相对顺序保留。不可用卡不显示，但记录、visible 和原位置保留。软件更新、默认值变化及重新启用不重新添加已知卡；已保存布局可通过恢复默认采用新的默认顺序。

## 持久化与提交

`DashboardService` 的同一事务门串行处理发现和保存；`DashboardLayoutStore` 校验 schemaVersion=1、GUID、UTC 时间、安全整数 revision、唯一 ID、运行与历史必要记录及最多 512 条记录。已有布局沿用 `core:history-duration` 的位置和显示偏好控制整组图表；磁盘中的 `core:history-counts` 原记录保留，但不进入可选目录或接受独立保存。缺少系统状态记录时按首次发现原子添加，不改变 layoutId 或其他记录。候选经同目录原子写入成功后才发布内存引用和通知；无变化不写盘、不增 revision。损坏、未知格式或外部字节变化返回 503 并保留原文件，恢复默认不修复损坏文件。

## 草稿与同步

今日概览的二级布局弹窗保存独立草稿，增删和拖动/键盘排序只更新编辑器列表；页面始终使用已提交布局，显式保存成功后才应用。完整 cardId 作为组件 key，重排不重挂 renderer。取消丢弃草稿；恢复默认只生成草稿。系统状态可以排序或隐藏；全部隐藏仍保留动作、告警及编辑入口。历史图表作为整体显示或隐藏，隐藏时暂停历史查询。

布局和目录变化经组合层转发到现役共享 SSE：dashboard.layout.changed 携 layoutId/revision，dashboard.catalog.changed 携 catalogRevision。通知只失效缓存，首次进入、重连、missed 和聚焦重新 GET。编辑中更新 committed 缓存并提示冲突，保留 draft；旧/重复通知不重挂卡。冲突时用户可确认载入最新，或将草稿有效项与新发现默认卡合并预览，明确保存。响应丢失先回读，比较此次影响的全部可用记录（包括隐藏记录顺序），不自动重发 PUT；回读失败保留待确认草稿。保存与服务恢复复用 page-writes/service-recovery 保护。

## 验证入口

行为回归位于 `tests/NexusPipeline.Tests/Dashboard`、`frontend/src/features/dashboard/layout-editor.test.ts`、`frontend/src/plugin-bridge/dashboard.test.ts` 和 `frontend/src/platform/navigation.test.ts`，使用现役 backend/frontend runner。单元响应和故障注入属于受控测试；实际 Host、插件启停、持久化及浏览器验收单独记录。

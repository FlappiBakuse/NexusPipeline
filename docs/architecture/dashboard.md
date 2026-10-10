# 今日概览卡片

`Modules/Dashboard/DashboardService` 拥有权威目录和 `config/dashboard-layout.json`。三个可编辑内置 ID 为 `core:status`、`core:running`、`core:history-duration`，分别表示系统状态、运行任务和运行历史统计。历史时长与次数属于同一个布局单元，共享一次摘要查询，宽屏内部各占半宽，窄屏纵向排列。内置目录提供用途说明，插件说明由登记元数据提供。

managed 插件经 `IPluginHostContext.DashboardCards.Register` 注册本地 ID，Host 从真实插件身份生成 `plugin:<name>:<id>`。重复或带冒号 ID 拒绝；插件上下文释放时注销全部句柄。组合层适配器只投影实际运行插件，并使用声明的本地化资源。

GET `/api/dashboard/layout` 一次返回 schemaVersion、catalogRevision、cards、layout、visibleCardIds。GET `/api/dashboard/cards` 返回当前目录。目录仅来自成功启动的 managed 登记，按 defaultOrder/cardId 排序；三张内置卡默认值为 0/100/200。首次出现按 defaultVisible 追加一次，包括默认隐藏卡；首次发现系统状态卡插入开头。不可用卡不显示，但记录、visible 和原位置保留。软件更新、默认值变化及重新启用不重新添加已知卡。

`DashboardService` 的同一事务门串行处理发现和保存；`DashboardLayoutStore` 校验 schemaVersion=1、GUID、UTC 时间、安全整数 revision、唯一 ID、运行与历史必要记录及最多 512 条记录。已有布局沿用 `core:history-duration` 的位置和显示偏好控制整组图表；磁盘中的 `core:history-counts` 原记录保留，但不进入可选目录或接受独立保存。缺少系统状态记录时按首次发现原子添加，不改变 layoutId 或其他记录。候选经同目录原子写入成功后才发布内存引用和通知；无变化不写盘、不增 revision。损坏、未知格式或外部字节变化返回 503 并保留原文件，恢复默认不修复损坏文件。

布局 GET 的强 ETag 是完整响应实际 compact UTF-8 字节的 SHA256，带 `Content-Language`、`Vary: Accept-Language`、`Cache-Control: no-store`。按 Accept-Language 使用宿主语言协商；不提供时沿用 X-Nexus-Locale。catalogRevision 包含 Host 实例与登记代次、完整元数据及词典，切换投影语言不改变目录代次，Host 重启会改变。

PUT `/api/dashboard/layout` 必须回传同语言 GET 的强 If-Match，正文只含 schemaVersion、catalogRevision 和有序唯一 visibleCardIds，空列表合法。缺前置条件返回 428，弱/通配 ETag 返回 400；先检查目录代次（409），再检查完整表示（412）。未知、不可用、固定区块及重复 ID 返回 400；请求体上限 256 KiB、条目超限返回 413。将选中可用记录按请求顺序置为显示，再按旧相对顺序放入未选隐藏记录，仅替换旧 entries 中可用的位置。不可用记录完全保留。写前再次核验来源代次与文件字节。成功 PUT 返回提交快照，不返回 ETag/Last-Modified；客户端随后 GET 取得下次编辑基线。

今日概览的二级布局弹窗保存独立草稿，增删和拖动/键盘排序只更新编辑器列表；页面始终使用已提交布局，显式保存成功后才应用。完整 cardId 作为组件 key，重排不重挂 renderer。取消丢弃草稿；恢复默认只生成草稿。系统状态可以排序或隐藏；全部隐藏仍保留动作、告警及编辑入口。历史图表作为整体显示或隐藏，隐藏时暂停历史查询。

布局和目录变化经组合层转发到现役共享 SSE：dashboard.layout.changed 携 layoutId/revision，dashboard.catalog.changed 携 catalogRevision。通知只失效缓存，首次进入、重连、missed 和聚焦重新 GET。编辑中更新 committed 缓存并提示冲突，保留 draft；旧/重复通知不重挂卡。冲突时用户可确认载入最新，或将草稿有效项与新发现默认卡合并预览，明确保存。响应丢失先回读，比较此次影响的全部可用记录（包括隐藏记录顺序），不自动重发 PUT；回读失败保留待确认草稿。保存与服务恢复复用 page-writes/service-recovery 保护。

Frontend API 1.7 的 `host.dashboard.registerCard(localId, renderer)` 只登记浏览器 renderer，不创建后端目录资格。surface 包含 element、完整 cardId、signal 与只读 context。普通状态更新保持同一 renderer；移除、路由离开和注销先 abort 再执行一次 cleanup，晚到异步 cleanup 立即释放。插件退出或激活失败由 Host 释放 owner 注册。

`host.navigation.openExternal(url)` 允许合法 HTTPS URL，拒绝用户信息和控制字符。桌面请求经可信 frame 的 preload/main IPC 交给系统默认浏览器；Web 用户点击创建独立标签，移除 opener 并设置 no-referrer。插件负责核实业务官方域和精确链接。调用失败返回可见错误，不在数据刷新时自动打开。

行为回归位于 `tests/NexusPipeline.Tests/Dashboard`、`frontend/src/features/dashboard/layout-editor.test.ts`、`frontend/src/plugin-bridge/dashboard.test.ts` 和 `frontend/src/platform/navigation.test.ts`，使用现役 backend/frontend runner。单元响应和故障注入属于受控测试；实际 Host、插件启停、持久化及浏览器验收单独记录。

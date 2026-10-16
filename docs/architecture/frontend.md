# 前端架构

本页维护宿主前端分层、页面生命周期与事务所有权。可消费 API、元素和视觉规则分别由参考文档及设计规范维护。

## 分层与责任

宿主前端以 Vue 页面组织业务，通过 platform 访问传输、认证和生命周期服务。插件通过公开 bridge facade 和宿主适配器交互；公开能力见[Frontend API](../reference/plugin-api/frontend.md)，组件选择见[UI 参考](../reference/ui/README.md)，布局与可访问性见[设计规范](../development/frontend-design.md)。

```text
frontend/src/app/App.vue → router / stores / features / ui
                         → app/bootstrap.ts → platform/*（平台服务）
                         → plugin-bridge/index.ts → plugin-bridge/*（插件运行时，经 host-adapter 使用 platform/*）
```

| 模块 | 职责 |
|---|---|
| `frontend/src/app/App.vue` | Vue shell、导航、响应式抽屉、Toast/通知容器与 `RouterView` 页面装配 |
| `frontend/src/router.ts` | 正式路由表：宿主页面按需加载，`/plugin/:pathMatch(.*)*` 交给插件 route 宿主，未知路径保持空 shell |
| `frontend/src/features/<domain>/<Domain>Page.vue` | 页面级请求调度、route/page 生命周期与高层数据编排；复杂事务进入同域 `components/`、请求进入 `services/`、纯转换进入 `utils/` |
| `frontend/src/ui/primitives/` | 类型化 Nexus UI primitives，并注册为插件可消费的 `nxp-*` Custom Elements；文本类元素（`nxp-button`、`nxp-badge`）通过 `label` 属性接收文案 |
| `frontend/src/stores/` | Pinia shell 状态（boot、导航抽屉、令牌提示） |
| `frontend/src/app/bootstrap.ts` | shell 启动编排：locale、主题、认证、外观、限制、particles 与插件运行时初始化 |
| `frontend/src/app/TokenPrompt.vue` | 远程访问令牌提示与运行期间 401 重新认证入口 |
| `frontend/src/app/PluginRouteHost.vue` | 插件 route 的 mount、leave、dispose 生命周期边界 |
| `frontend/src/features/settings/components/ServiceRestartNotice.vue` | 设置页面卡片上方的重启提示、重启入口、进行中禁用与超时手动重试 |
| `frontend/src/platform/i18n.ts` | 浏览器本地语言偏好、宿主词典、动态页面文案和日期/数字/列表格式化；唯一资源源为 `frontend/public/i18n/` |
| `frontend/src/platform/api.ts` | 宿主请求封装（bearer 头、`X-Nexus-Locale`、JSON/blob/SSE、错误码投影、AbortController 生命周期联动） |
| `frontend/src/platform/events.ts` | 页面范围 SSE 连接与可跨 chunk 的 event/data/id parser；断线退避、ready/missed 重同步和 route token 校验 |
| `frontend/src/platform/page-state.ts` | 页面 route token、定时器与在途请求的代际管理 |
| `frontend/src/platform/shell.ts` / `platform/toast.ts` | 顶部标题、导航态、主题切换以及 Toast、通知与字段错误状态 |
| `frontend/src/platform/appearance.ts` | 主题 token 校验、插件主题注册、通用背景表面与外观变更广播；背景地址由外观表面托管，替换或清除时回收上一个 Blob Object URL |
| `frontend/src/platform/service-restart.ts` | 服务重启编排：提交或复用重启交接信息、按实例身份与候选端口确认新实例、跳转到实际监听端口 |
| `frontend/src/platform/tooltip.ts` / `platform/auto-scroll.ts` | 延迟气泡提示与长文本滚动辅助 |
| `frontend/src/platform/auth.ts` | 访问令牌探测、令牌校验与重新认证入口 |
| `frontend/src/platform/limits.ts` | shell 启动时的约束警告层与完成操作提示卡片 |
| `frontend/src/platform/markdown.ts` / `platform/format.ts` / `platform/plugin-list.ts` | README 渲染、状态与结果码投影、插件浏览筛选排序 |
| `frontend/src/platform/execution-preview.ts` | 插件运行预览捕获（受控截图） |
| `frontend/src/plugin-bridge/index.ts` | 宿主侧桥接 facade：`renderPluginSlot`、`disposePluginSlot`、`initPluginRuntime` 与插件 route/nav/lifecycle 接入 |
| `frontend/src/plugin-bridge/runtime.ts` | Frontend API 1.7：同源模块加载、route/nav/slot/lifecycle 注册、插件 Web API（含二进制 API）、本地化、外观与运行预览宿主访问 |
| `frontend/src/plugin-bridge/slots.ts` / `controls.ts` / `plugin-fields.ts` | 稳定 slot 名称、批量贡献查询、Form/Badge/Card 通用渲染与清理；声明式表单控件直接实例化公开 `nxp-*` 元素，桥接层只负责属性映射、值收集、改动同步与必填校验 |
| `frontend/src/plugin-bridge/host-adapter.ts` | 桥接层唯一的宿主依赖边界；平台模块迁移只改这里的实现来源 |

## 路由与业务事务

RouterView 以 route.fullPath 标识页面代际。shell 启动完成后安装导航准备门禁，各 features 的 first-screen 模块读取目标页首屏数据；准备完成才提交路由，页面同步消费一次数据，不在挂载后重复读取。准备期间原页保留但业务操作不可用，侧边栏继续接受选择；超过 200 毫秒显示顶部进度条，30 秒未完成则保留原页并提供重试。失败与超时不提交目标路由，连续切换中被替代的请求和候选 surface 取消，晚到结果不得覆盖当前页面。

导航准备使用独立 AbortSignal，不占用当前页的请求代际。正式提交时才进入新的 page-state 代际并释放上一页轮询与请求。首屏结果仅保存在本次导航内存中，消费后释放，不持久化、不缓存编辑草稿或明文凭据；各页面继续管理后续刷新、事件与写入。历史深链接准备指定记录，非关键 slot、图片和次级详情不阻塞首屏。

插件 route 复用 Frontend API 的异步 handler，候选 element 尚未连接文档，handler 完成后由 PluginRouteHost 接管。取消先中止 surface.signal，再执行一次 cleanup；正式进入后才发送 page-enter/page-updated 通知。插件自己的轮询在 element 连接文档后启动；离开旧页的全局通知不能释放别的候选 surface。

新业务进入 features 对应领域，页面负责请求调度与高层编排，复杂事务放同域 components，请求放 services，纯转换放 utils。业务不得操作页面外 DOM；卸载时释放轮询、订阅和在途请求。app、features、ui 和 platform 通过 @bridge/index 使用插件能力，不引用桥接私有实现。

配置编辑事务由 App 持有并通过 Vue 注入交给用户页，路由切换不会卸载编辑事务界面。启动时从 Host 会话恢复，完成后通知用户页刷新草稿。事务语义见[配置编辑与交换](configuration.md)。

调度与历史展示冻结任务、证据和准入失败原因，不因插件升级改写既有历史。只读计划与配置诊断属于当前绑定预览；跨用户切换忽略旧预览响应。历史投影与当前预览不得互相代替。

## 当前页刷新与写入保护

应用壳持有每 tab 唯一的 `/api/events` 传输；业务订阅保留路由代际，离开即释放回调，设置和插件页也持续接收本机管理通知。服务恢复暂停传输，恢复后重连；管理事件不重放。`page-writes.ts` 统计所有非 GET/HEAD 请求及尚未发送的设置队列，自有 AbortSignal 不绕过统计。确认刷新前再次检查写入与路由代际；关闭许可在保存至进程退出之间阻止新写入。

访问令牌回读与清空使用本项代际及顺序保存链：已发送写入等待明确结果，未开始的旧令牌闭包失效，其他设置队列继续保存。明文只留在编辑组件内存；回读不触发保存或更新认证缓存。清空成功才移除浏览器认证值，未知结果禁止自动补写。

## 双栏浏览页面

运行历史与插件中心共用双栏浏览布局和列表卡片样式。日期使用公共折叠卡片，展开后在日期内选择用户；右栏记录卡片直接打开现役运行详情，日期范围与状态筛选重新读取日期、用户和记录，不提供常驻刷新按钮。日期用户请求和记录请求分别校验代际，筛选或离开时晚到结果不得恢复旧选择。

运行计划检查按 explain 的 scriptId 将账号资格和警告归属到计划任务，保持任务顺序；重复引用同一实例时去除重复账号投影。全局准入失败优先显示，账号 ready/skipped/blocked 保留不同含义；页面只解释快照，不改变执行资格或启动运行。

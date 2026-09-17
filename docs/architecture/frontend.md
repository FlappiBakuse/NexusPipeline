# 前端架构

### 10.7 前端分层

```
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
| `frontend/src/plugin-bridge/runtime.ts` | Frontend API 1.5：同源模块加载、route/nav/slot/lifecycle 注册、插件 Web API（含二进制 API）、本地化、外观与运行预览宿主访问 |
| `frontend/src/plugin-bridge/slots.ts` / `controls.ts` / `plugin-fields.ts` | 稳定 slot 名称、批量贡献查询、Form/Badge/Card 通用渲染与清理；声明式表单控件直接实例化公开 `nxp-*` 元素，桥接层只负责属性映射、值收集、改动同步与必填校验 |
| `frontend/src/plugin-bridge/host-adapter.ts` | 桥接层唯一的宿主依赖边界；平台模块迁移只改这里的实现来源 |

样式分层：`frontend/src/styles/tokens.css` 提供设计 token，`styles/app.css` 提供基础元素样式与 shell 过渡，`styles/shell.css` 提供布局、shell 与插件 surface 样式，Nexus UI 元件样式保留在组件 SFC 中。

新增交互的落点：

1. 新业务页面放在 `frontend/src/features/<domain>/`，通过 platform service、Pinia 或组件本地状态获取数据，不直接操作页面外部 DOM。
2. 可复用控件优先放入 `frontend/src/ui/primitives/` 或 composite 目录；视觉变体使用类型化 props，插件边界使用公开 `nxp-*` 元素。
3. Vue 页面使用明确的 props/emits、`data-testid` 业务定位和 `onBeforeUnmount` 清理轮询/订阅；路由使用 `router.ts` 的 hash URL 路由表。
4. 已迁移页面的业务逻辑必须落在对应 `frontend/src/features/<domain>/`，通过组件事件、service 和 page-local state 管理交互；不得把新的业务逻辑扩展到旧 `data-action` 注册表。
5. 宿主 `app/**`、`features/**`、`ui/**` 与 `platform/**` 不引用 `plugin-bridge/` 内部实现，也不引用 `wwwroot/`；插件能力经 `@bridge/index` facade 与 host-adapter 边界提供，不得恢复 HTML 字符串页面架构。

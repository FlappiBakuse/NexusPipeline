# 前端架构

## 前端分层与公共边界

今日概览使用历史摘要的每日数据绘制近七天 SVG 折线图。次数以历史记录为单位，异常为总次数减成功次数；累计时长按记录开始日期归属，并行时长分别累加。图表补齐无记录日期，默认显示七天时长或次数汇总，鼠标、键盘和触屏选择日期时切换为每日值，页面生命周期管理刷新与请求取消。

新建脚本类型卡片通过受保护的图标接口读取二进制；来源由插件 manifest 声明，宿主启动后后台预取，不直接由浏览器请求上游。插件导航的 `titleKey` 从插件词典读取并随界面语言切换更新。宿主与插件的紧凑文字共用公开 `nxp-overflow-text`；公共徽章和纯文本按钮内置该行为，长正文保持换行。插件详情中的名称、描述和元数据正常换行，徽章与动作使用公开的 `wrap` 属性，列表仍保持紧凑单行。

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
| `frontend/src/plugin-bridge/runtime.ts` | Frontend API 1.6：同源模块加载、route/nav/slot/lifecycle 注册、插件 Web API（含二进制 API）、本地化、外观与运行预览宿主访问 |
| `frontend/src/plugin-bridge/slots.ts` / `controls.ts` / `plugin-fields.ts` | 稳定 slot 名称、批量贡献查询、Form/Badge/Card 通用渲染与清理；声明式表单控件直接实例化公开 `nxp-*` 元素，桥接层只负责属性映射、值收集、改动同步与必填校验 |
| `frontend/src/plugin-bridge/host-adapter.ts` | 桥接层唯一的宿主依赖边界；平台模块迁移只改这里的实现来源 |

样式分层：`frontend/src/styles/tokens.css` 提供设计 token，`styles/app.css` 提供基础元素样式与 shell 过渡，`styles/shell.css` 提供页面布局、shell 与插件 surface 样式，`styles/workspace.css` 维护工作空间的尺寸、排版与响应式表面；Nexus UI 元件的 DOM、交互和内部样式由对应 SFC 维护，feature 只通过公开元素根节点和业务 wrapper 组织布局。页面操作区保持在标题右侧，桌面与说明区底部对齐，窄屏与标题顶部对齐。日期范围和选择框共用控件高度，随窄屏或触屏尺寸调整；历史筛选保留日期与状态的 2:1 宽度。插件中心在窄屏切换列表和详情，卡片仍受可用视口高度约束，由公开滚动元件承载内部滚动。

远程设置的访问令牌随输入提交独立密钥事务，空白保持原值，读取响应不回显明文。保存队列按编辑代次确认，较早响应不覆盖后续输入；设置页发起服务重启前等待队列完成，保存失败时保留草稿并阻止该次重启。地址仅展示一个优先含网关的非虚拟内网 IPv4 和一个实际公网 IPv4，端口使用当前监听值；NAT 出口不能从本机网卡推断，未检测到公网地址时说明域名与端口映射要求。

令牌保存不会重复更新防火墙或登录启动项。防火墙仅在开启远程访问、退出轻量模式或远程监听配置端口变化时同步；登录启动项只在开关变化时同步。

页面主标题沿用侧边栏对应导航名称，辅助标题保留该业务页的主题名称。Host 侧边栏通过 `RouterLink` 提交页面导航，活动链接随已提交路由更新。概览和运行中心的直接内容卡片由单一 grid 维护 16px 间距；设置中已展开的宿主及插件面板排在折叠面板前。桌面的 40 DIP 标题栏使用独立不透明表面，粒子与全屏业务遮罩仅覆盖标题栏下方的内容区。

设置分类从内置面板及 `settings.cards` 中公开折叠卡片的标题和 `data-settings-panel` 生成，按卡片的实际顺序排列，使用统一面板事件协调展开；页面观察卡片的挂载、移除、位置和标题变化，卸载时释放观察器。关闭二级表面透明度时，浮层范围同时重算宿主表面变量和公开 `--nx-color-surface`，主页面卡片及侧边栏继续沿用壁纸透明度。

`RouterView` 本身保持挂载，其 slot 以 `route.fullPath` 为页面容器 key。导航直接卸载上一页面并挂载目标页面，页面容器使用 CSS 入场动画，动画完成事件不控制路由挂载；连续切换仍以最后提交的路由为准。启动加载态独立于页面动画。

新增交互的落点：

1. 新业务页面放在 `frontend/src/features/<domain>/`，通过 platform service、Pinia 或组件本地状态获取数据，不直接操作页面外部 DOM。
2. 可复用控件优先放入 `frontend/src/ui/primitives/` 或 composite 目录；视觉变体使用类型化 props，插件边界使用公开 `nxp-*` 元素。
3. Vue 页面使用明确的 props/emits、`data-testid` 业务定位和 `onBeforeUnmount` 清理轮询/订阅；路由使用 `router.ts` 的 hash URL 路由表。
4. 已迁移页面的业务逻辑必须落在对应 `frontend/src/features/<domain>/`，通过组件事件、service 和 page-local state 管理交互；不得把新的业务逻辑扩展到旧 `data-action` 注册表。
5. 宿主 `app/**`、`features/**`、`ui/**` 与 `platform/**` 不引用 `plugin-bridge/` 内部实现，也不引用 `wwwroot/`；插件能力经 `@bridge/index` facade 与 host-adapter 边界提供，不得恢复 HTML 字符串页面架构。

专项任务预览、历史与实时进度复用 `TaskReportPanel`，仅展示冻结计划中已启用的任务。用户绑定的只读计划位于编辑配置下方，按需展开读取；调度中心使用横向步骤条变体。宿主状态、识别范围、重试风险和原因文案使用宿主词典，内置任务名称有中英文映射，用户自定义及上游动态名称保留原文。用户徽章区分未运行、旧记录、记录删除与已有运行但证据不完整；历史冻结结果不会因插件升级自动改写。

只读任务计划和历史报告通过 `TaskPlanItem` 按父子关系嵌套已启用任务，父任务不展示识别覆盖、重试风险和任务类型。计划与任务卡片复用 `NxpCollapsibleCard` 的箭头和动画，以现有表面变量按深、浅层级交替；说明集中在专项任务标题上方的 `NxpDismissibleNotice`，重新读取计划后恢复提示，刷新入口置于卡片右下角。两处任务卡片使用紧凑高度，历史报告的尝试结果与日志证据直接展示在对应任务卡片内；任务深链接会展开祖先卡片并定位到对应尝试。

调度中心按当前脚本实例 ID 和运行状态筛选专项任务报告，只展示当前实例的横向步骤条。步骤卡片等宽等高，仅保留序号、任务名称和完成状态，无父任务副标题或展开详情；详细证据保留在历史记录。

专项任务面板由单一 grid 管理可见区块间距，卡片及嵌套任务统一 8px。没有通知时不预留标题上方通知空间。配置检查展示当前预览结果，历史签名差异不使本次检查过期。

新建专项脚本实例默认启用强制关闭，编辑时保留用户明确关闭的选择；通用脚本与 provider 的默认值不变。专项任务的配置检查、阻断提示、事实和证据卡片与可展开任务卡片共享大圆角。

任务计划的配置检查不按页面停留时间失效。诊断说明统一放在任务列表底部、重新检查按钮上方；日常奖励选项汇总为一条包含开启、关闭、待确认数量的提示。逐次修复完成后重读计划，跨用户切换时忽略旧预览响应。

配置编辑流程由 App 持有并通过 Vue 注入交给用户页使用；路由切换不会卸载编辑事务界面。启动时从 Host 会话恢复，完成后通知用户页刷新当前草稿。调度与历史保留冻结任务、证据和准入失败原因，配置检查卡片只在用户配置的任务计划预览显示。调度进度条与专项任务区域保持独立间距。

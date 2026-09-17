# v0.15 版本历史

## v0.15.12（Pre-release）

### 更新与版本

- 宿主与插件工具统一支持正式版、`-beta.N` 和 `-rc.N` 版本，并按 beta、rc、正式版顺序比较。
- 增加 `update-policy.json` 更新策略校验；跨越破坏性版本屏障时保留更新发现结果，要求手动下载安装包并迁移配置。
- 更新状态、Web API、MCP 和自动更新服务同步暴露策略验证与手动迁移状态，内置下载和应用入口在屏障状态下统一拒绝。
- 更新策略读取使用独立 URI 安全域；仓库根目录策略由生产解析器校验，CI 强制 barrier 历史追加-only，既有记录不可删除、修改或重排。
- Release 的 `prerelease` 标记必须符合宿主项目发布策略；元数据矛盾的发布不会进入更新候选。
- 修复下次启动更新在宿主进程退出收尾时的 worker 竞态，确保 Windows 文件句柄释放后再完成程序文件交换。

### 插件兼容性

- 插件商店安装与更新继续校验 `minHostVersion`；运行时发现手动放入的不兼容插件时保留元数据显示并标记为不兼容，禁止解析、加载和激活。
- 插件管理页、详情页和控制面补充最低宿主版本与兼容性原因展示。
- 插件商店分别投影宿主版本、Plugin API 和版本数据错误，只有已安装插件存在更高且不兼容的新版本时显示“需升级宿主”状态。

## v0.15.11（Pre-release）

### 国际化与插件契约

- 补齐宿主前端、API 状态投影、插件判断脚本和官方专项插件资源的中英文语义覆盖；统一插件判断输入中的运行语言。
- 全局管理设置的同步校验返回稳定错误码，并按已启用的同步类别执行对应校验。
- MaaStellaSora 的机器 ID 更新为 `maas`，启动时迁移插件偏好、脚本绑定、作用域数据和 managed-code 配置文件，保留历史运行记录事实。

### 前端交互与布局

- 新增 `NxpScrollArea` 悬浮滚动区域并接入弹窗、下拉菜单、日志、历史、插件列表、详情和表格等滚动内容；原生滚动条隐藏，轨道支持拖拽、键盘和无障碍语义。
- 全局管理表单的必填字段在保存时标红并聚焦首个空字段，用户输入后即时清除错误状态。
- 专项徽章、插件名称裁切、运行计划与系统诊断布局、选择框箭头和 CustomWallpaper 拖拽预览完成统一调整。

### 官方插件

- 统一 BAAH、BetterGI、MaaEnd、MaaStellaSora、March7thAssistant 和 ZenlessZoneZeroOneDragon 的机器 ID、ArtifactName、DisplayName、商店文案与版本元数据。
- CustomWallpaper 升级到 0.2.3，拖拽把手会带动整张壁纸卡片进行排序。

## v0.15.10（Pre-release）

### 前端组件与交互

- 设置、用户管理、通知、脚本编辑、插件搜索和诊断操作统一使用宿主 Nexus UI primitives；开关卡片共享 `NxpSwitchSetting`，多列开关布局使用内部 `NxpSwitchGrid`。
- 新增 `NxpConfirmDialog`，更新应用、服务重启和运行取消使用统一的确认弹窗，移除浏览器原生确认框。
- 服务重启恢复在进入轮询间隔前立即探测新实例；确认交接标识、实例标识和实际端口后保留当前路径与 hash 并刷新或跳转。
- `/ui-lab` 仅在开发环境按需加载，生产路由表保持精简。

### 测试与协作治理

- 移除持久化 Visual Contract 截图测试及快照基线，UI Smoke 保留 10 个核心用户工作流，文档门禁自动拒绝截图匹配器、视觉回归套件和快照基线。
- 明确持久化 UI 测试的功能、ARIA、焦点、状态、提交、路由、API 效果和生命周期边界；临时浏览器验证使用操作系统临时目录并在完成后清理。
- 明确按功能边界拆分提交的协作规则，并同步宿主与官方插件仓库的开发约束。

## v0.15.9（Pre-release）

### 前端公共组件收口

- 新增结构组件 `NxpPageHeader`、`NxpSectionCard`、`NxpCollapsibleCard`、`NxpTabs`、`NxpDialogPopover`；`nxp-section-card` 与 `nxp-collapsible-card` 作为公开 Native Custom Elements 注册，插件可直接消费。
- Settings、Plugins、Dispatch、History 页面把重复的卡片、标签页、筛选浮层、运行计划、列表与详情结构下沉到同域组件：Plugins 24.4KB→11.9KB、Dispatch 22.5KB→11.2KB、History 19.8KB→9.6KB、Settings 31.6KB→15.2KB。
- 所有带取消语义的二级弹窗改由 `NxpModal` 渲染右上角关闭按钮，关闭与取消调用同一 handler；删除 feature 内重复的 header 与 `modal-close` markup，并移除会让锁定弹窗关闭按钮不可见的宿主样式规则。

### 插件契约与外观边界

- Plugin API 升级到 1.6：新增通用 `IPluginAssetStore`（插件命名空间隔离、内容寻址 Id、写入/读取/删除/枚举、路径逃逸防护、原子写入与宿主级绝对上限）与二进制 Web API 传输（原始请求体流、受限 Content-Type 的二进制响应、Content-Length 与取消）。
- Frontend API 升级到 1.5：`host.appearance` 收敛为通用外观表面（`setBackground`/`clearBackground`/`setTokens`/`clearTokens`/`registerTheme`/`applyTheme`），删除宿主 `wallpaperStore` 与 `derivePalette`；`host.api` 增加二进制 `blob` 与 `upload`。
- 宿主删除服务端壁纸实现：`AppearanceService`、`/api/appearance`、`/api/appearance-assets`、`/api/appearance-upload` 与相关 DTO 全部移除，环境仅保留 light/dark/system 主题、通用 token 校验、注册主题、通用背景表面与外观变更事件。
- CustomWallpaper 回归真正的插件实现：壁纸配置、单文件/总量/数量配额、文件头校验、SHA256 去重、排序、当前壁纸、按时间与启动轮换、配色推导、插件自有 Web API 与状态 revision 均由插件持有。
- 宿主提供一次性旧外观数据搬迁：把 `config/appearance.json`、`user-assets/appearance/wallpapers/` 与外观轮换游标写入原提供方插件的资产命名空间与作用域载荷，幂等、可重试、不删除旧文件，成功标记只在载荷落盘后写入；插件初始化时导入并消费该载荷。
- 官方 LiveScreenshot 与 CustomWallpaper 前端改用公开 `nxp-*` 元素、插件自有 class 命名空间与 `--nx-*` design token，不再引用宿主结构 class，也不再复制 Nexus UI 组件。

### 宿主插件知识清理

- 删除脚本视图中的官方专项插件展示名硬编码；插件展示名只取自当前插件元数据，插件缺失时回退到记录的 `pluginType` 原始 ID。
- 插件商店单插件安装、更新、卸载响应统一返回 `restartRequired`，与启用、禁用和批量更新保持一致。

### 服务重启与外观细节

- 重启提示回到设置页面卡片上方：`ServiceRestartNotice` 由设置页渲染，插件安装、更新、卸载、启用、禁用与批量更新成功后进入设置页即可重启，其他页面不再固定显示该提示条；重启进行中只显示进度文案，恢复成功后页面自动刷新。
- 重启恢复按实例身份确认：`/api/status` 暴露进程实例标识 `instanceId` 与本次重启交接标识 `restartHandoffId`，重启接口返回 `handoffId`、旧实例 `instanceId` 与候选端口；请求重启的进程为子进程生成交接标识并随 `restart --handoff` 传入。
- 前端按配置端口与宿主顺延端口探测服务状态：探测地址只使用协议与主机（不携带页面路径与 hash 路由），只接受携带本次交接标识且实例标识不同于旧实例的应答；目标地址与当前页面一致时执行刷新，端口变化时跳转到新实例上报的实际监听端口并保留路径与 hash。无关 HTTP 服务、仍在应答的旧实例与超时都不会触发跳转。
- 只读的 `GET /api/status` 放行同主机的其他端口并返回可读 CORS 应答，其余接口保持同源要求。
- `host.appearance.setBackground` 在替换背景时回收上一个 Blob Object URL，`clearBackground` 回收当前地址；插件多次轮换壁纸不再累积失效的 Object URL。
- 环境粒子增强为 36/56/80 三档密度、点透明度 0.2、连线透明度 0.08、连接阈值 104px，连线改为双层索引循环；`prefers-reduced-motion`、页面隐藏暂停、resize、DPR 上限与外观变更重绘保持不变。

### 插件桥接组件化

- 声明式插件表单控件改为直接实例化宿主公开的 `nxp-*` 元素：`text`/`url`/`secret`/`status` → `nxp-text-input`，`textarea` → `nxp-text-area`，`number` → `nxp-number-input`，`range` → `nxp-range`，`color` → `nxp-color-picker`，`switch` → `nxp-switch`，`select`/`multi-select` → `nxp-select`。
- 桥接层只保留 schema → 属性、值收集、改动同步与校验错误投影；删除自行拼装的 Select/Number/Color 控件 DOM、宿主级控件事件委托与 `platform/icons`、`platform/format` 直接依赖，公开组件修复会自动作用于声明式插件表单。
- 新公开 `nxp-switch-list` 开关分组元素，插件用它把多个 `nxp-switch-setting` 组成与设置页一致的开关列表；结构卡片在 light DOM 下不产生额外布局盒，插件卡片的展开置顶与宿主卡片使用同一规则。
- `nxp-button` 与 `nxp-badge` 新增 `label` 属性：自定义元素消费方用属性传文案，元素不依赖插槽子节点即可渲染，父级重渲染不会影响元素自身的 DOM 与交互；插槽内容继续作为替代写法。
- 公开元素的插槽文本在父级应用重渲染后保持有效：父级把自定义元素当普通元素写入 `textContent` 时，节点重新作为插槽内容挂载，元素自身的 DOM、样式与交互不再被抹掉。
- 新增表驱动的控件验收用例（元素映射、初始值、约束、单选与多选、开关、错误投影与清理）、公开元素插槽契约用例与静态实现边界用例（禁止拼装控件 DOM、平台依赖必须经 host adapter、字段类型必须映射到公开元素）。

### CI 影响域治理

- 宿主 CI 拆分为影响域 Gate（前端 Unit、宿主 Core、文档/i18n、插件契约、管理员 UI Smoke、System 四域），普通前端或文档改动不再触发 Windows System Smoke；每周定时与手动 `workflow_dispatch` 保留 `admin all` 全量回归，判定失败时按全量门禁执行。
- System 影响域按 suite 细分为 `system_runtime`、`system_execution`、`system_emulator`、`system_update`：MCP 与运行时生命周期、执行与判定、模拟器驱动、更新事务各自只触发对应作业，横切文件显式列入多个域，不再使用 `src/**` 作为共同触发源。
- 新增 `tests/tools/ci-domains.test.mjs`（16 个用例，含「宿主源码逐文件命中至少一个 System 域」）与 `tests/run.mjs` 的 `tooling` 入口；CI 的 `changes` 作业先校验映射再做判定。
- `tests/run.mjs` 增加 `system` 分组入口（`runtime`、`execution`、`emulator`、`update`）与 `--dry` 列表模式，CI 不再复制 System Smoke 运行逻辑。
- 插件仓库 CI 拆分为 plugin-source、plugin-frontend、plugin-managed、plugin-package 四个 Gate，发布与每周审计工作流保持完整校验。

### 双仓插件门禁

- `Test-FrontendPlugins.mjs` 强化为 Frontend API 1.5 conformance：精确版本校验、宿主私有 class 拒绝、公开 `nxp-*` 元素白名单（宿主检出可用时直接读取 `NEXUS_PUBLIC_ELEMENTS`）、禁止复制 Nexus UI 组件、折叠卡片挂载断言，以及卸载后定时器与 window 监听不残留。
- 插件前端 conformance 增加 CustomWallpaper 生命周期回归：激活即应用背景与配色（不依赖设置页面）、离开设置页面保留背景与配色、页面访问不触发随机轮换、按时间轮换在无设置页面时继续生效、只有插件停用才清理全局外观与计时器。
- 宿主 `NxpModal` 锁定语义、公开元素注册与新公共组件契约均有对应组件测试；新增插件资产存储、二进制传输、旧数据搬迁与插件业务用例。

## v0.15.8（Pre-release）

### 插件桥接与外部契约

- 插件桥接实现迁入 `frontend/src/plugin-bridge/`，宿主平台服务经单一 host adapter 注入，桥接层不再直接引用宿主内部模块。
- 宿主侧补齐 Frontend API 1.4 contract tests：精确版本匹配、`host.*` 能力面、18 个公开 slot 白名单、renderer surface context 与清理、生命周期订阅与释放。

### 宿主平台迁移

- 宿主 i18n、API client、page state、shell 与 toast、认证、外观、tooltip、限制、markdown、particles 与启动编排迁入 `frontend/src/platform/` 与 `frontend/src/app/`，`frontend/src` 对旧 `wwwroot/` 源码零依赖。
- 认证提示与远程重新认证改用 Nexus UI 弹窗；插件 route、宿主弹窗与页面字段错误行为保持不变。
- 宿主页面切换到真实 Vue Router 路由表与懒加载，保留 route token、离页轮询清理、弹窗与监听清理、插件 route 生命周期与导航态同步。

### 样式与静态资源

- 样式收敛为 token、基础元素、布局/shell 与组件四层：`styles/app.css` 承载基础元素样式，`styles/shell.css` 承载布局与插件 surface 样式，Nexus UI 元件样式保留在组件 SFC 中。
- 删除 `@legacy` alias、`wwwroot/package.json`、`wwwroot/style.css` 与全部遗留平台模块；发布包 Web 静态资源只来自 `frontend/dist`。
- 清理失去消费者的宿主语言资源键，并把 Web Logic 用例统一迁入 frontend Vitest。

### 路由集成与双仓门禁

- `PluginRouteHost` 直接读取当前路由取得 route segment，不再依赖上游注入的 `ready`/`segments`；插件 route 首次进入、直接访问、插件间切换与回退 Dashboard 的行为恢复。
- 新增真实 Router 装配集成测试，覆盖 route handler 的 token 与 segments、`onPageEnter`/`onPageUpdated`、插件 route 与宿主 route 之间的 leave/dispose 次数、无效 route 回退，以及 query 变化时的页面代际语义。
- 宿主 CI 增加官方插件 Frontend API 契约 step；`docs/TESTING.md` 与迁移对照表改为真实测试分工。
- 修复设置页通知 section 未导入 `NxpBadge` 导致 Webhook/SMTP 通道状态徽标不渲染的问题。

## v0.15.7（Pre-release）

### 迁移完整性

- 恢复配置编辑事务会话：Users 页面加载后按用户绑定匹配进行中的编辑会话，只还原锁定编辑 UI 并从原会话继续 done/cancel，不重发 `action:start`，恢复失败静默。
- 专项脚本根目录的手工输入与原生目录选择都会触发 `/api/scripts/probe`，按插件与根路径签名去重，过期响应与关闭后的在途失败不提示。
- 插件仓库刷新触发宿主 catalog cache 失效后强制重取 store 列表，刷新失败如实提示。
- 更新状态为 `ready` 时常驻显示备份提醒，离开该状态自动消失。
- Users 倒计时到期后延迟单次重新拉取后端状态；同一批到期用户只刷新一次，相同过期时间戳不形成请求循环，卸载时清理定时器。

### 前端组件化

- Users、Settings、Scripts、History 的复杂事务从页面 SFC 下沉到 feature 组件、composable、service 与 utils，页面保留请求调度、生命周期与高层编排职责。
- 补齐脚本/队列卡片徽章、调度目标选择器和历史用户筛选返回的稳定定位符。

### 迁移对照

- 新增 docs/frontend-migration-coverage.md 作为全面迁移的发布硬门禁。
- 删除已经确认不可达的旧 Web 入口、`wwwroot/views/**`、`wwwroot/i18n/**` 与无消费者 `wwwroot/core/*` 模块；`wwwroot/package.json` 与 `wwwroot/style.css` 按计划保留。
- 宿主语言资源以 `frontend/public/i18n/` 为唯一源并移除失去消费者的资源键；文档一致性检查改为按当前 Vue 组件调用点与插件桥接源码校验。

### 稳定性修复

- 设置页更新状态恢复「取消下载」、通道启用状态与飞书 App Secret 字段的可见文案（此前引用缺失资源键）。

## v0.15.6（Pre-release）

### 前端行为与组件

- Scripts、Users、Queues 统一使用 Pointer Events 排序能力，恢复整卡拖动、键盘上下移动和队列编辑器内嵌列表排序。
- Queues 恢复公开插件 slot、20 条固定分页、队列数量与编辑子项限制、专项插件可用性提示和下一次调度倒计时。
- Queues、Scripts、Users 的列表卡片与队列编辑器拆分为 feature 组件，页面 SFC 回收到编排、请求和生命周期职责；Dispatch、History 的运行计划与详情弹窗统一接入 `NxpModal`。
- 正式页面开始复用 `NxpModal`、`NxpPager` 与 `NxpLoadingState`；统一弹窗支持焦点进入、Tab 循环、Escape、遮罩关闭和 locked 语义。

### 动效与测试

- 增加统一 motion tokens、页面/卡片/弹窗过渡和 `prefers-reduced-motion` 支持。
- Visual Contract 收敛到页面行为与稳定组件状态，业务页面改用语义断言；新增排序、队列倒计时和弹窗行为测试。
- Frontend plugin conformance 实际执行 renderer 的挂载与清理生命周期，并修复 CustomWallpaper、LiveScreenshot 浏览器发行产物的 Vue 挂载问题。
- UI Smoke 加入宿主内置的真实 Frontend API fixture，覆盖插件清单、模块激活、settings slot 渲染、页面离开清理与重新进入。

## v0.15.5（Pre-release）

### 前端组件化与插件契约

- 引入 Vue 3、TypeScript、Vite 和 Nexus UI 公共组件层；开发期构建，运行期继续托管静态资源。
- Frontend API 升级到 1.4 并采用精确版本匹配，移除旧的 `host.controls`、`host.actions` 和 Frontend API 1.3 兼容路径。
- 宿主外壳接入 Vue 路由/状态与公共 `nxp-*` Native Custom Elements，官方 CustomWallpaper、LiveScreenshot 插件迁移到 Vue 组件。

## v0.15.4（Pre-release）

### 国际化语义规范化

- 修正全局运行天数输入框的提示键、占位符和帮助文案，并与脚本绑定运行天数保持同一语义。
- 修正调度启动失败的英文错误文案，区分运行启动失败与调度状态加载失败。
- 将仪表盘计数、调度摘要、失败原因、队列加载、脚本加载、诊断概览和系统操作倒计时收敛为完整模板。
- 清理已迁移的片段键与旧用户全局管理键，新增前端语言资源语义、调用位置和语言注册表门禁。

### 前端交互与信息层级

- 调度中心运行计划检查将目标摘要、任务列表和用户准入拆分为同级卡片，并优化任务用户数徽章对齐及窄屏布局。
- 设置页语言选择器在保持当前选项时不触发刷新；实际切换界面语言后才重新加载页面。
- Users 页面英文操作按钮使用完整的 `User Management` 与 `Global Management` 文案，桌面端按内容自适应，移动端保持固定操作网格。

## v0.15.3（Pre-release）

### 国际化与插件契约

- 宿主 locale 资源按功能域整理，并加入 key、placeholder、资源内容与源码引用契约检查。
- 语言资源键改为语义层级命名，清理句子型与哈希型 key，并按字典序统一排序。
- Web `zh-CN` / `en-US` 资源键进一步收敛为最长 40 字符，并同步诊断与闲时更新状态代码。
- 修复语言切换后的翻译缓存、列表分隔符和界面可访问性文本问题。
- CLI 菜单、帮助、参数校验、服务诊断和更新入口改用 `cli.*` 稳定资源键；旧日志与旧调用保留兼容回退边界。
- 数据化专项插件输入支持 `labelKey` 与 `descriptionKey`，官方专项插件同步中英文输入文案与商店元数据。
- 统一插件本地化资源的 BCP47 locale、key 集合、placeholder 集合和安全命名校验。
- 二次复核并重命名残留句子型 locale key，补充用途型命名门禁，清理英文复数歧义写法。
- 规范化宿主与 Web API 错误英文文案，并修正四个专项插件历史商店记录的英文语义；对应插件分别采用新的 patch 版本承载修复。

### 插件更新

- 更新全部插件时按已安装、兼容、存在新版本且无待处理事务筛选候选项，覆盖手动安装后与目录身份匹配的插件。
- 更新候选为空时使用统一的无可更新提示，并保持逐项结果与重启状态反馈。

## v0.15.2（Pre-release）

### 国际化与控制面契约
- 宿主语言设置持久化为 `HostLocale`，CLI、托盘、通知和后台宿主上下文可使用全局语言；Web 页面继续按浏览器语言选择资源。
- Web API 错误响应统一为机器可读的 `code` 与 `args`，前端按当前语言资源生成用户可见文字；诊断和运行计划解释结果改为稳定代码与参数。
- 前端资源注册表和插件本地化契约采用规范化 BCP 47 locale、键集合与占位符校验，清理 `legacy.*` 资源键。

### 运行计划、诊断与插件仓库
- 优化运行计划检查和系统诊断卡片的信息层级、表格列布局及窄屏响应式展示。
- 插件仓库新增“更新所有插件”，按官方仓库托管、已安装且存在可用新版本的范围顺序执行并汇总结果，单个失败不影响其他插件。
- 官方插件仓库补充本地化资源一致性验证和贡献约束文档。

## v0.15.1（Pre-release）

### 国际化与插件契约
- 宿主 Web UI、CLI、托盘/系统提示、通知和用户可见服务端错误接入 `zh-CN` / `en-US` 资源；浏览器按 `navigator.languages` 初始选择语言，并以 `localStorage` 保存偏好。
- Plugin API 扩展至 1.5，Frontend API 扩展至 1.3；官方插件支持 manifest 本地化资源、声明式 UI/历史展示本地化引用和前端 `host.i18n`。
- `CustomWallpaper`、`LiveScreenshot`、`GameCheckIn` 同步更新资源、manifest、商店元数据、发布校验和打包流程。
- 运行记录保存稳定结果码与参数，历史及插件展示按请求语言投影，保留结果判定和持久化安全边界。

### 核心结构治理
- `SystemActions` 按进程启动、进程树清理、窗口操作和系统电源拆分职责，原有进程所有权、Job Object 和清理确认语义保持有效。
- `ExecutionCoordinator` 抽离用户脚本、游戏启动、脚本进程会话、截图采集和尝试监控循环，保留取消、预算、判定、截图和收尾时序。
- `CliCommandRouter` 改为命令注册表与处理器分发；`UserCommands` 按策略、校验和结果适配拆分，保持 CLI/API 行为契约。

## v0.15.0（Pre-release）

### 可靠性、诊断与可验证性
- 新增故障注入与更新恢复验证链路，覆盖更新交换阶段的中断恢复、备份完整性和恢复工作进程；保留 KN-90 最近有效截图缓存运行语义。
- 新增 `doctor`、Web 系统诊断、MCP `get_diagnostics` 与脱敏 Support Bundle 导出，诊断结果和导出内容具备稳定结构、大小边界和敏感信息扫描。
- 新增脚本/队列 dry-run 与 Execution Explain，通过共享准入评估路径展示运行计划、用户资格、资源冲突和阻断原因，并保持只读。
- NexusPipeline-Plugins 新增 Plugin TestKit、官方插件生命周期测试与前端插件 conformance 校验。
- 扩展路径、压缩包、远程访问、MCP、插件 Web API 和密钥脱敏边界测试，强化现有安全契约验证。


# Changelog

本仓库所有重要变更均按版本记录于此。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/)，版本遵循 [SemVer](https://semver.org/lang/zh-CN/)（v1.0.0 之前为 Pre-release）。

## v0.9.3（Pre-release）

### 调度、持久化与生命周期

- 为 Scheduled occurrence 增加持久化状态、重启恢复、触发去重、跨 minute 补偿和冻结执行计划；配置或队列变化后重新校验 pending plan。
- 将队列变更、脚本/用户修改、配置编辑与执行准入纳入统一资源协调，并补充 graceful shutdown 门禁。
- History 记录采用尝试日志与 JSON 提交标记的原子流程；持久化 warning 会进入运行状态；通知通道增加独立超时。
- ResultCollector 的日志限制统一按 UTF-8 字节数计算。

### 插件、模拟器与日志监控

- 分离插件配置态与运行态，补充 InitFailed 能力门禁、未知插件失败结果和用户偏好保留。
- 模拟器清理按目标 package 执行，区分 ADB 命令失败与离线状态，并增加独立 cleanup deadline。
- 修正 LogMonitor transient reopen offset 和 FileId fallback 语义。

### 测试与验证

- 基线回归测试统一命名为 `BaselineReproductionTests.cs`，覆盖 v0.9.3 开工前稳定复现的问题。
- 管理员构建通过，仅保留基线已有的 3 条 nullable 警告。
- 单元测试通过：184/184。
- 加速档全量 Playwright：87/87；`judge-scenarios` 150/150；`chaos-queue` 166/166。
- 发布前真实计时档：Playwright 87/87；`judge-scenarios` 150/150；`chaos-queue` 166/166。
- `git diff --check` 通过。

## v0.9.2（Pre-release）

### 正确性与安全性加固

- 统一 PreRun、Main、Judge、PostRun 的 Attempt 与最终收尾语义，确保前置失败、提前成功、致命结束和 PostRun 失败都能保留正确的最终结果与原因。
- 收敛配置准备、交换、替换和还原的事务边界；补齐异常路径 History、synthetic record 的 `FinalStatus`、脚本级完成通知和 `FinalizeRun()` 重复调用幂等性。
- 加固 SystemAction 取消状态机、重启门禁、Settings 克隆写入、脚本与队列提交/删除隔离，以及 Scheduler 瞬时资源冲突的重试语义。
- 加固进程树清理与收尾屏障，区分远程 ADB 与本机模拟器控制，并在 ADB 取消时回收子进程。
- 修正通配符/目录日志轮换的候选文件快照，避免旧日志内容进入当前 Attempt。

### 文档与测试

- 完成 v0.9.2 回归保护，回归测试文件统一命名为 `RegressionTests.cs`。
- 将已知问题台账统一为 `KNOWN_ISSUES.md`，移除过时的 v0.6/v0.7 历史评估归档，并将 ROADMAP 收敛为后续版本计划。

### 验证

- 管理员构建通过：`release/nexus-pipeline.exe`（版本 0.9.2），仅保留基线已有的 3 条 nullable 警告。
- 单元测试通过：178/178。
- 加速档全量 Playwright：87/87；`judge-scenarios` 150/150；`chaos-queue` 166/166。
- 发布前真实计时档：Playwright 87/87；`judge-scenarios` 150/150；`chaos-queue` 166/166。
- `node --check` 与 `git diff --check` 通过。

## v0.9.1（Pre-release）

### 并行调度安全性加固

- 扩展资源租约模型，覆盖用户数据、日志精确路径与通配模式、前/后置脚本可执行文件及进程，并对现存路径执行物理规范化；统一 ADB loopback 端点别名。
- 将 EmulatorOnly 分类收紧为 fail-closed：必须具备有效 ADB 端点，专项插件还必须声明模拟器能力。
- 在同一数据锁内构造队列、脚本和用户快照，补齐缺失脚本与空用户任务的进度计数语义。
- 将脚本、用户配置 CRUD、配置编辑门禁与执行租约纳入统一协调；Web 冲突返回 HTTP 409 和稳定错误码。
- 明确完成组收尾边界：首个非 `none` 完成意图后进入 `Closing`，完成操作执行或取消后重新开放。
- Scheduler 区分瞬时准入冲突与永久校验失败；瞬时冲突保留 pending trigger 并在后续 tick 重试。
- 新增并行资源、fail-closed 分类、完成组关闭和 Scheduler 重试测试，并保持现有队列内串行语义。

### 验证

- 管理员构建通过：`release/nexus-pipeline.exe`（版本 0.9.1）。
- 单元测试通过：163/163。
- 加速档全量 Playwright：86 通过、1 个 CI 预期跳过；`judge-scenarios` 150/150；`chaos-queue` 166/166。
- 发布前真实计时档：Playwright 87/87（约 9.0 分钟）；`judge-scenarios` 150/150；`chaos-queue` 166/166。
- `node --check` 与 `git diff --check` 通过；发布使用本版本管理员构建产物。

## v0.9.0（Pre-release）

### 并行调度

- 固化队列、任务、脚本和启用用户快照，形成不可变执行计划，运行期间不受仓储后续编辑影响。
- 建立 `EmulatorOnly` / `Standard` 资格矩阵：纯安卓模拟器队列可互相并行，最多一个普通队列同时运行；无法证明为纯模拟器的队列按普通队列处理。
- 建立原子资源租约，统一检查脚本 ID、解析后的启动目标、进程基名、配置路径父子关系和模拟器 ADB 端点冲突。
- 将脚本、队列、Web、CLI 与 Scheduler 的启动准入收敛到同一策略；队列内任务与用户顺序保持串行。
- 将并行运行状态、进度、历史与日志查询改为快照读取，并协调配置门禁、完成通知和取消语义。
- 将完成操作改为完成意图：活动运行全部释放后统一 arm，同一操作合并，不同操作在准入时拒绝；取消队列跳过完成意图。

### 验证

- 管理员构建通过：`release/nexus-pipeline.exe`（版本 0.9.0）。
- 单元测试通过：158/158。
- 加速档全量 Playwright：86 通过、1 个 CI 预期跳过；`judge-scenarios` 150/150；`chaos-queue` 166/166。
- 发布前真实计时档：Playwright 86 通过、1 个 CI 预期跳过（约 9.0 分钟）；`judge-scenarios` 150/150；`chaos-queue` 166/166。
- 新增并行准入 E2E 用例在加速档与真实计时档均通过；`node --check` 与 `git diff --check` 通过。

## v0.8.7（Pre-release）

### 前端布局验收修正

- 仪表盘：移除状态卡右侧冗余的运行数；移除底部统计卡行（脚本实例、调度队列、正在运行、下一调度倒计时），运行信息收敛到状态优先卡与「正在运行」面板。
- 脚本实例弹窗：自定义完成标志区按钮组与游戏联动组同宽（各占行宽 1/3）并整组靠右对齐；开启「使用判断脚本」后按钮按「上传脚本文件、使用判断脚本、自动更新配置」顺序排列，关闭时保持「使用判断脚本、自动更新配置」。
- 调度队列卡片：移除「运行模式」徽章（按计划/启动时/手动运行），手动队列新增「不自动运行」徽章，触发信息统一归入该徽章组。
- 更多菜单弹出卡（脚本/队列/用户卡片「···」）：由绝对定位改为 fixed + 触发器视口坐标定位，脱离列表容器 `overflow:hidden` 裁剪，靠底部卡片处不再被卡片边截断；视口底部空间不足时自动翻转到触发器上方，滚动/改变视口时跟随触发器重新定位。
- 手机端（≤820px）脚本/调度队列卡片：「用户管理」「编辑队列」按钮延伸至「···」更多按钮；操作区改为从拖拽把手之后起排（不再受图标列右移），三类卡片操作按钮宽度完全一致（与用户卡片「编辑配置」同宽），消除操作区左侧空隙。
- 历史记录页改版：卡片按 1/3 竖线分隔——左侧「运行日期」仅显示有记录的日期（倒序，含当日条数），右侧「运行情况」显示选中日期当日运行记录（状态色条 + 时间/脚本名 + 状态徽章含失败原因 + 记录文件路径），点击记录弹出运行详情；页头右上角天数范围下拉扩展为 7/15/30/60/90/120/180 天（默认 30）；新增 `GET /api/history/dates`（日期索引）与 `GET /api/history?date=YYYY-MM-DD`（按日取记录，含 historyDir），原分页与详情 API 不变。
- 运行文件内置品牌图标：`src/NexusPipeline.ico`（侧边栏「N」徽章：圆角渐变 + 白色粗体 N，16-256 多尺寸）设为 exe 应用图标（资源管理器/任务栏显示），托盘图标同步使用该图标（提取失败回退系统默认）。
- 同步更新 E2E 布局断言：仪表盘统计区移除、下一调度卡移除、判断脚本按钮组顺序与靠右对齐。

### 验证

- 管理员构建通过：`release/nexus-pipeline.exe`（版本 0.8.7，内置品牌图标）。
- 单元测试通过：148/148。
- 加速档全量 Playwright 回归通过：86/86；加速档专项：`judge-scenarios` 150/150、`chaos-queue` 166/166。
- 发布前真实计时档全量回归：Playwright 86/86（约 8.9 分钟）、`judge-scenarios` 150/150、`chaos-queue` 166/166。
- 变更涉及的前端模块与 E2E 用例通过 `node --check`。

## v0.8.6（Pre-release）

### 前端信息架构与交互深度优化

- 以状态优先和渐进披露重排仪表盘、脚本实例、用户、调度队列、调度中心、历史记录与插件页面，系统导航与工作台职责分组更清晰。
- 将高频操作保留在列表行，将编辑/删除等低频操作收进可访问的“更多”菜单；历史记录支持整行点击与键盘进入详情，名称与长文本保留滚动可读性。
- 统一切换控件为轨道式视觉开关，状态由 `aria-pressed` 和辅助状态文本表达，移除对“开/关”可见文案的依赖；表单错误位保留稳定布局空间。
- 调度中心收敛为统一执行条；脚本超时策略、队列定时项和高级配置采用渐进披露；队列弹窗重建时保留定时项展开状态、DnD 顺序和滚动位置。
- 设置页仅在发生需要重启的变更后展示重启提示；通知插件配置、插件分组和 Dashboard 异常摘要沿用既有 API、密钥与远程访问语义。

### 验证

- 管理员构建通过：`release/nexus-pipeline.exe`。
- 单元测试通过：148/148。
- 加速档全量 Playwright 回归通过：83/83；真实计时档全量 Playwright 回归通过：83/83。
- 真实计时档 `judge-scenarios` 通过：150/150；`chaos-queue` 通过：166/166。
- 变更涉及的前端模块与 E2E 用例通过 `node --check`，`git diff --check` 通过。

## v0.8.5（Pre-release）

### 前端响应式协调性改造

- 收敛 CSS 设计令牌与组件级联，统一页面标题、列表行、表格、设置行和响应式规则。
- 优化手机端导航外壳、脚本/队列操作区、Dashboard 统计区和 History 移动记录布局。
- 保持原生 ES modules、现有 API、路由、`data-action`/`data-testid`、DnD、Modal、主题和无障碍交互契约。
- 根据验收截图修正定时列表外框、手机用户卡片、调度中心同行操作、插件名称滚动、插件说明间距和用户编辑开关布局。
- 修正插件名称单元格直接使用弹性布局造成的表格行边界与其他列上下错位，保留名称悬停/键盘聚焦滚动交互。

### 验证

- 管理员构建通过：`release/nexus-pipeline.exe`。
- 单元测试通过：148/148。
- 加速档全量 Playwright 回归通过：83/83；验收修正定向回归通过：6/6。
- 发布前真实计时档通过：Playwright 83/83（约 9.0 分钟）、judge 150/150、chaos 166/166。
- 全部 `wwwroot/**/*.js` 通过 `node --check`，差异检查通过。

## v0.8.4（Pre-release）

### 前端视觉重构

- 收敛为低阴影、细边框、统一圆角和数字间距 token 的工作台视觉，保留原生 ES modules、现有 API、路由、主题、响应式和无障碍交互契约。
- 将脚本实例、调度队列、用户、定时任务和队列任务统一为列表行；历史与插件管理统一为表格 surface；设置和通知插件配置减少嵌套卡片层级。
- 普通按钮改为 neutral/ghost，新增 `primary` 主操作语义；次要操作与删除操作恢复可见描边，删除确认继续使用实心危险按钮。
- Dashboard 插件摘要改为紧凑能力行；移除通知插件行中的重复状态与实例统计文案；普通内容块取消阴影，Modal、Toast、拖拽项等浮层保留独立 elevation。
- 队列通知切换控件与远程访问设置采用同一设置行结构；移除队列任务列表外层卡片，保留任务行与拖拽排序。
- 保留 `effects/particles.js`、`#ambient-particles` 及其低透明度、减弱动效、页面隐藏暂停和窗口尺寸响应行为。

### 验证

- 管理员 `build.cmd`：通过（保留既有 3 项 nullable 警告）。
- 前端 JavaScript 模块语法检查：通过；`git diff --check`：通过。
- 管理员加速档 Playwright e2e：81/81 通过，`failedTests` 为空。
- 发布前真实计时档：单元测试 148/148、e2e 81/81、judge 150/150、chaos 166/166，全部通过。

## v0.8.3（Pre-release）

### 项目与文档治理

- 统一框架依赖单文件发布、队列全局串行和文档单一事实源表述。
- 完成历史评估内容收尾，收敛 README、设计、架构、开发、贡献、发布和路线文档职责。
- 将测试目录整理为 `tests/NexusPipeline.Tests/` 与 `tests/e2e/`，同步构建、CI、测试隔离和文档路径。
- 增加 `SECURITY.md`、Issue/PR 模板、Dependabot、`.editorconfig`、`.gitattributes` 和 CI 最小权限声明。
- 测试数量与断言数量集中记录在本版本验证结果、后续 Release Notes 和 CI 产物中。

### 验证

- 管理员 `build.cmd`：通过（保留既有 3 项 nullable 警告）。
- 单元测试：148/148 通过（304 项断言）。
- 管理员加速档 Playwright e2e：81/81 通过（4.1 分钟）；`judge-scenarios`：150/150 通过；`chaos-queue`：167/167 通过。
- 管理员真实计时档 Playwright e2e：81/81 通过（8.9 分钟）。
- 管理员真实计时档 `judge-scenarios`：150/150 通过；`chaos-queue`：166/166 通过。

## v0.8.2（Pre-release）

### 后端架构第三次优化

- 将 `DispatchCenter` 收敛为执行门面，拆分 `ExecutionValidator`、`ExecutionRunner` 与 `SystemActionExecutor`，保留现有执行入口、取消和系统完成操作语义。
- 建立脚本、队列、用户、设置、历史、执行、通知和插件能力的显式 Application 端口与运行时仓储，减少业务服务直接依赖 `RuntimeContext`。
- 保持配置交换、重试、通知、队列串行、历史记录、Web/CLI 入口和插件 capability 行为兼容。
- 增加执行边界与组合根解析治理测试，防止新的服务定位器和具体插件实现耦合回流。

### 验证

- 管理员 `build.cmd`：通过（仅保留既有 3 项 nullable 警告）。
- 管理员真实计时档 Playwright e2e：81/81 通过。
- 管理员真实计时档 `judge-scenarios`：150/150 通过；`chaos-queue`：166/166 通过。
- 单元测试：148/148 通过（304 项断言）。

## v0.8.1（Pre-release）

### 后端领域边界收敛

- 将 `RunSession` 收敛为运行状态对象，由 `ExecutionCoordinator` 负责一次运行级编排；新增 `AttemptRunner` 宿主端口、`RetryPolicy`、`ResultCollector` 和 `CleanupManager`，保留原有判定、重试、日志和清理时序。
- 按领域整理 Services 目录：Execution、Configuration、Judgement、Scheduling、History、Notification；`ConfigurationTransaction` 封装配置交换 prepare/retry/sync/replace/rollback 原语，`ConfigRunSession` 继续作为唯一收尾顺序入口。
- 新增 `INotificationChannelProvider`、`IEmulatorCapabilityProvider` 与 `NotificationDispatcher`，执行域/DispatchCenter 不再直接依赖具体插件管理器；新增 `ExecutionCommands` 作为 Web、Scheduler 和常驻服务 CLI 通道共享的启动/取消命令入口。
- 保持现有 Web/API、配置交换磁盘协议、历史格式、队列串行语义和插件 capability 行为不变，并增加运行生命周期、重试策略、配置事务、通知端口和应用命令治理测试。

### 验证

- 管理员 `build.cmd`：通过（仅保留既有 3 项 nullable 警告）。
- 单元测试：145/145 通过（281 项断言）。
- 管理员加速档 Playwright e2e：81/81 通过。
- 管理员加速档 `judge-scenarios`：150/150 通过；`chaos-queue`：166/166 通过（加速档采样兜底可能为 167）。
- 发布前真实计时档全量回归：管理员 E2E 81/81（8.8 分钟）、judge 150/150、chaos 166/166，全部通过。

## v0.8.0（Pre-release）

### 后端架构强化

- 将进程入口、运行时初始化和服务启动生命周期拆分到 `src/Application/`：`Program` 仅负责入口转交，`ApplicationHost` 负责命令分发，`StartupPipeline` 负责服务生命周期，`RuntimeInitializer` 负责运行时数据初始化。
- 将 `DispatchCenter` 的运行中/已结束任务和系统操作倒计时状态隔离到 `ExecutionStateStore`，保留原子防重入、队列全局串行和 100 条结束记录上限。
- 将配置会话标记、编辑会话模型及配置恢复扫描/延迟重试从 `ConfigSwapSession` 拆分到 `Services/ConfigSwap/`；原 `ConfigSwapSession` 保留兼容 façade。
- 保持现有 Web/API、配置交换目录、`.session`/`swap-backup`/`retry-store` 磁盘协议、插件能力和运行时语义不变，并补充运行状态生命周期架构测试。

### 验证

- 管理员 `build.cmd`：通过（仅保留既有 3 项 nullable 警告）。
- 单元测试：141/141 通过（262 项断言）。
- 管理员加速档 Playwright e2e：81/81 通过。
- 管理员加速档 `judge-scenarios`：150/150 通过；`chaos-queue`：166/166 通过。
- 管理员真实计时档 Playwright e2e：81/81 通过（8.9 分钟）。
- 管理员真实计时档 `judge-scenarios`：150/150 通过；`chaos-queue`：166/166 通过。

## v0.7.12（Pre-release）

### 前端 UI 第二次全面优化

- 按新版 UI Review 收敛页面标题、间距、按钮、卡片、表单栅格、危险操作和移动端重排，保留原生 ES Module 与现有 API/交互契约。
- 仪表盘调整为三张运行统计卡 + 宽版下一调度卡；脚本实例、调度队列、插件、设置、调度中心和历史页面统一操作区与响应式布局。
- 修复长表单弹窗的固定标题/操作区与独立滚动正文；拖拽排序、添加定时和异步刷新保留页面、弹窗及滚动容器位置。
- 修复移动端脚本游戏联动字段宽度、插件操作按钮间距、设置类切换卡片底色与排列、窄屏日志级别选择器换行，以及会造成布局跳动的字段错误文字。
- 删除脚本、用户、调度队列按钮统一使用与二次确认弹窗“确认删除”一致的高对比危险样式；历史记录筛选控件收敛为与其他页面 page-head 操作区一致的高度和间距。

### E2E 稳定性

- 合并用户提交的“新增用户后立即编辑配置”门禁释放竞态修复（5712258），并移除测试中的临时诊断输出。

### 验证

- 管理员 `build.cmd`：通过（仅保留既有 3 项 nullable 警告）。
- 管理员单元测试：140/140 通过。
- 管理员 Playwright e2e：81/81 通过（真实计时档，8.8 分钟）。
- 设置页远程访问布局定向回归：1/1 通过。
- 删除与历史详情相关 E2E 定向回归：5/5 通过。

## v0.7.11（Pre-release）

### 前端 UI 全面精修

- 统一 SVG 图标、按钮组间距、表单栅格和服务行为类切换卡片的排列与底色，修正移动端脚本表单「启动后等待秒数」宽度不一致、插件操作按钮过近等问题。
- 重构长表单弹窗为固定标题/操作区 + 独立滚动正文；拖拽排序、新增定时和异步刷新后保留窗口及滚动容器位置，避免页面或弹窗跳回顶部。
- 收敛字段错误反馈为输入框红色状态与无障碍属性，移除会造成布局跳动的内联错误文字；窄屏设置选择器可独占整行。
- 仪表盘、脚本实例、调度队列、插件、设置等页面完成 UI Review 项的 spacing、响应式布局和控件风格收口。

### 验证

- `build.cmd`：通过（仅保留既有 3 项 nullable 警告）。
- 单元测试：140/140 通过（管理员权限环境）。
- Playwright e2e：81/81 通过（管理员权限、真实计时档）。
- `judge-scenarios`：150/150 通过（管理员权限、真实计时档）；`chaos-queue`：166/166 通过（管理员权限、真实计时档）。

## v0.7.10（Pre-release）

### 前端交互修复

- 修复脚本实例分页后拖拽排序会把当前页整体挪到全局首部的问题；排序回写限定在当前分页区间，并补充第二页真实拖拽回归。
- 修复远程访问场景下重启服务探测与跳转固定使用 `127.0.0.1` 的问题；重启按当前访问主机名/IP/IPv6 主机，仅替换端口，CSP 同步允许当前访问主机的跨端口探测。
- 删除用户成功后关闭确认弹窗；调度中心取消运行增加确认卡片和提交忙碌态。
- 修复脚本实例与调度队列卡片图标因 CSP 拦截 `blob:` 图片 URL 而始终显示占位图的问题，并补充实际图标加载回归。
- 仪表盘下一调度卡片恢复为两行：上方显示倒计时，下方显示「下一调度队列：队列名称」。
- 历史详情默认保留尾部 200 行，并提供按次查看完整日志入口；访问令牌生成改为保存完成后反馈，默认保持隐藏，增加显示/隐藏与复制操作。
- 修正游戏联动设置文案，明确游戏路径/ADB 用于失败清理与重试恢复；保留项目要求的必填校验与既有业务语义。

### 已知问题收尾

- KN-74：损坏或缺少 `configPath` 的 `replaceConfigs` 备份清单改为隔离到 `.corrupt-*`，不再被后台恢复循环永久重试。
- KN-75：旧布局迁移识别疑似用户数据目录，避免保留名用户目录与旧版无用户目录发生误迁移。
- KN-76：复核确认当前实现已按 `RestoreKind` 保持文件/目录形态还原，从未修复台账中移除。
- KN-73、KN-82：本版复核未能稳定复现，保留为低概率并发边界，不作未经验证的并发语义改动。

### 验证

- `build.cmd`：通过（仅保留既有 3 项 nullable 警告）。
- 单元测试：140/140 通过。
- Playwright e2e：81/81 通过（真实计时档）。
- `judge-scenarios`：150/150 通过（真实计时档）；`chaos-queue`：166/166 通过（真实计时档）。

## v0.7.9（Pre-release）

### 核心模块扩展性治理

- **Run 生命周期收敛**：新增 `RunBudget` 统一整个运行（含重试、前置/后置脚本）的总超时预算；新增 `RunAttemptFinalizer` 收敛脚本进程树、游戏/模拟器清理策略，保持原有失败/取消/强制关闭语义。
- **Config 生命周期收敛**：新增 `ConfigRunSession` 统一 prepare、retry、自动更新同步、`replaceConfigs` 还原、判断脚本目录清理和最终配置现场恢复；固定顺序为「同步 → 替换还原 → script 清理 → 配置交换还原」。
- **Plugin capability 治理**：新增内部 `PluginCapabilityRegistry` 与中立 capability/profile 契约；`PluginSummary` 保持元数据职责，Web 继续输出兼容的 `supportsEmulator` 字段；数据化插件支持 `capabilities` 数组，旧 `supportsEmulator` 自动映射为 `emulator`。
- **Plugin host 解耦**：`PluginContext` 改用显式 `PluginHostServices` 访问设置与服务，插件配置/密钥文件格式保持不变；未实现未来的 `ISignInProvider`、`IRunHook`、`IProbeProvider` 等业务扩展。

### 兼容与文档

- 保持现有 Web/API、磁盘 JSON、配置交换目录、`.session`/`swap-backup`/`retry-store`、数据化插件目录和通知行为兼容。
- 架构、设计、开发门禁、发布流程与项目协作约束同步至 v0.7.9；当前单元测试为 138 个测试、228 个断言。

### 测试

- `build.cmd`：通过（仅保留既有 3 项 nullable 警告）。
- 单元测试：138/138 通过。
- Playwright e2e：77/77 通过（加速档约 4.2 分钟）。
- `judge-scenarios`：150/150 通过；`chaos-queue`：167/167 通过。

## v0.7.8（Pre-release）

### 核心运行与配置安全

- 复用已运行服务的实际 Web 端口，避免端口漂移后重复拉起服务。
- 不同调度队列全局串行；运行期间拒绝修改队列、脚本和用户配置。
- 已运行脚本先强制结束并确认退出，再重新启动监管。
- 总超时覆盖游戏/模拟器启动、主脚本、重试及前后置用户脚本。
- 失败重试使用 `retry-store` 重新执行完整配置交换，保留用户永久快照边界。
- 自动更新配置采用 `store.tmp`、`store.previous` 目录事务，源配置变化或写入失败时保留旧快照。
- 专项自动更新配置由后端强制开启；MaaEnd 还原描述按实例 ID 定位。

### 测试

- 加速档：e2e 77/77、judge 150/150、chaos 166/166、单元测试 127 个测试通过。
- 真实计时档发布门禁：e2e 77/77（8.9 分钟）、judge 150/150、chaos 166/166、单元测试 127 个测试通过。

## v0.7.7（Pre-release）

### 修复（v0.7.6 全面评估产出）

- **KN-77 自动更新配置收尾同步内容有效性守护（数据风险）**：此前收尾同步对单文件 config 只查「非空」——脚本被取消/超时强杀瞬间正在写配置（半写/损坏 JSON）时，坏内容直接被镜像进用户快照 store 永久污染，下次运行脚本解析失败。现新增：
  - **JSON 型内容有效性探测**（`ConfigSwapSession.ValidForSync` → `ContentValidForSync`/`JsonContentValid`）：`.json` 扩展名或内容以 `{`/`[` 开头的文件必须可解析，0 字节 `.json` = 半写坏态，非 JSON 文本不校验，单文件 32MB 上限跳过探测；探测失败 → 跳过整个同步（宁可保留旧快照也不入库坏态）；
  - **收尾同步同样执行稳定性双采样**（此前仅首次检测）：外部守护进程仍在写配置时跳过本次回写，保留旧快照。
- **文档全面对齐 v0.7.6**：KNOWN_ISSUES 台账移除全部已修复项（仅留未修复 KN-09/73-76/78/79/82/83 与语义保留 KN-80/81）；ROADMAP 基线更新 v0.7.6 并删除已发布详章；DESIGN 新增「自动更新配置」说明（4.5 节）；README/ARCHITECTURE/DEVELOPMENT/ASSESSMENT 同步 v0.7.6 语义；FLAKE-LEDGER 补本次回归记录。

### 测试

- 单测 **174 → 183 断言**（+9：坏 JSON 文件/目录跳过、混合合法通过、空 .json 跳过但空 txt 通过、无扩展名 JSON 内容校验）；judge-scenarios **140 → 150**（+10：半写 JSON 不入库 + config 还原、合法 JSON 照常入库 + config 还原）。
- 加速档全量回归全绿：e2e 77/77 + judge 150/0 + chaos 167/0 + 单测 183（2026-08-16）。

### 变更

- 版本号 0.7.7。

## v0.7.6（Pre-release）

### 功能：自动更新配置（AutoUpdateConfig）

- **新增开关「自动更新配置」（默认开）**：允许运行产生的配置更改**反向同步回用户快照 store**（config → store 全量镜像），保留游戏脚本自身写入的任务完成记录/运行计数/脚本更新新增的任务，供下次运行延续——此前运行结束配置交换还原会把脚本写入的进度全部丢弃，重试/下次运行从旧快照从头开始，违背脚本自身设计。
- **触发时机**：① 首次检测——运行开始 15 秒后（随测试加速缩放）在监控主循环内一次性同步（关/开模式共有，仅第 1 次尝试，捕获脚本启动后自行更新的任务配置）；② 收尾同步——每次运行收尾（成功/失败/达最大次数/**cancelled**/总超时）在 finally 中、插队还原与配置交换还原**之前**执行（config 此刻为脚本最终态），仅开关开启时。
- **同步语义**：全量镜像（copy-then-prune，先复制后删除，防中途失败留下空 store）；判断脚本插队文件（`replaceConfigs` 目标）有还原描述（`config-restore.json`）时先还原任务启停为初始值再写入快照（**初始启停 + 运行后计数/其他字段**），无还原描述时跳过（store 保持原样）；非插队文件照常镜像。
- **守护机制**：会话有效性校验（`.session` Phase=run，防 15s 检测与收尾还原时序异常）；基础有效性校验（config 缺失/为空/文件数骤降一半以上 → 告警跳过，防坏态入库永久污染快照）；首次检测前置稳定性检查（短间隔两次采样不一致 = 脚本仍在写配置 → 跳过本次）；同步失败仅告警、不阻断运行收尾还原。
- **专项脚本恒开**：仅前端不渲染开关（自定义完成标志整块本就不渲染），后端不强设字段，保存 payload 恒 true。
- **前端**：通用脚本弹窗「自定义完成标志」区新增「自动更新配置」切换按钮（关键字/判断脚本模式皆可，默认开，`data-testid="sm-autoupdate"`）。

### 专项判断脚本重写（plugins/）

- `maaend`/`bettergi` 判断脚本保留「失败选择性重试」编排，新增：首次触发时读取 config 提取初始任务启停映射写 `config-restore.json`（array 型 `instances[{index}].tasks` / map 型 `TaskEnabledList`，跨尝试只写一次）；宿主收尾按描述还原启停后同步快照。`march7th`/`zzzonedragon` 无启停编排不重写。契约文档见 `plugins/README.md`「配置还原描述」。

### 测试

- 单测 **174 断言**（97 → 174，+77：还原描述执行器 array/map/路径无效/未覆盖键保持、全量镜像新增/删除/插队跳过/启停还原/空 config 跳过/骤降跳过、首次检测时机判定）；e2e **76 → 77**（+1：自动更新配置开关渲染/切换/保存/回显/专项不渲染）；judge-scenarios **115 → 140**（+25：开=收尾同步成功/cancelled、关=仅首次同步、通用插队文件不写快照、MaaEnd 专项启停还原 + 计数保留）。
- 加速档 + **真实计时档**全量回归全绿（e2e 77/77、judge 140/0、chaos 166/0、单测 174，2026-08-16）。

### 变更

- 版本号 0.7.6。

## v0.7.5（Pre-release）

### 修复（已知问题台账收尾 + 台账外 5 项）

- **KN-06 远程访问下脚本图标 401**：新增 `hydrateIcons`（core/api.js）——图标渲染后经 fetch（自动携带 Bearer）取 blob 转 ObjectURL 替换 `[data-icon-id]` 元素，按脚本 Id 缓存；本地模式行为不变，远程模式图标恢复可用。
- **KN-35 历史详情 31 天窗口**：`HistoryService.FindById` 默认窗口改取保留天数上限（默认 180/可配 365），超出 31 天的记录点详情不再 404；显式传参语义保留。
- **KN-31 AuthFails 字典累积**：远端认证失败条目加 LastActive 时间戳，每 60 秒清理超 10 分钟无活动条目（锁定中保留至锁定过期）。
- **KN-39 Python 多安装候选不确定**：安装目录候选按解析的主次版本号数值降序取最新（`Python310` > `Python39`，避免字符串序误判），无法解析排最后。
- **KN-33 Webhook 成功判定**：补 HTTP 状态码——HTTP 非 2xx 直接失败，飞书/钉钉再校验 body `code==0`（此前 HTTP 500 + code==0 误判成功）。
- **KN-08 bat 游戏启动器等待**：随测试加速缩放（`TestHooks.ScaledMs`），加速档不再白等真实秒数。
- **KN-55 .session/.meta 序列化**：写盘改 PascalCase（与「磁盘 JSON = PascalCase」约定一致）；读取 `PropertyNameCaseInsensitive`/双键兼容旧版 camelCase，旧崩溃现场无需迁移。
- **KN-27 icon 响应安全头**：补 `X-Content-Type-Options: nosniff` / `Referrer-Policy`，与静态文件响应一致。
- **台账外 KN-68 CSP 跨端口轮询冲突**：`connect-src` 放行 `http://127.0.0.1:*`——设置页改端口重启后跨端口探测不再被 CSP 拦截，自动跳转新端口恢复可用（仅回环，不扩大攻击面）。
- **台账外 KN-69 maaend 模板直出判定失灵**：judge.js 实例定位回退链 `autoStartInstanceId` → `lastActiveInstanceId` → 唯一实例（模板直出配置单实例场景可正常判定；多实例无标记仍保守无输出）。
- **台账外 KN-70 编辑 start 模板标记孤儿窗口**：`Mark.Write()`（含 GeneratedTemplate/TemplateFiles）提前到模板生成后立即持久化——StartVisible 失败/崩溃窗口不再产生无清单的模板孤儿；`DoRestore` 两路径统一先按清单删除模板兄弟文件（cache 非空路径此前不消费清单）。
- **台账外 KN-71 进程退出轮判断脚本双触发**：周期触发后同轮的退出/stall 最终触发跳过（同轮日志段不变、周期触发输入即最终状态，属完全重复执行）；**批次触发后的同轮最终触发保留**——进程退出是新事实，判断脚本可基于自身状态文件二次执行给出最终判定（真实档 e2e 06「进程退出时最终触发」用例回归修正，首版「距上次触发 1 秒」一刀切守卫误拦该场景）。
- **台账外 KN-72 KillByName 连带杀游戏**：自重启轮按名清理携带游戏排除名单走 Toolhelp 树清理——不再 `Process.Kill(entireProcessTree: true)` 连带杀死脚本自启动的游戏子孙进程（游戏关闭/失败路径语义不变）。

### 测试

- 断言数字不变（单测 97、e2e 76、judge 115、chaos 166）。
- 加速档全量回归：e2e 76/76（4.0m）+ judge 115/0 + chaos 166/0 + 单测 97/97 全绿（2026-08-16）。

### 变更

- 版本号 0.7.5。

## v0.7.4（Pre-release）

### 修复（已知问题批量：稳定复现 + 无严重副作用项 + 台账外新发现）

- **目录型专项二次编辑误删用户配置（台账外 KN-65，数据风险）**：`EnsureConfigForEdit` 对目录型 ConfigPath（如 MaaEnd `config\`）的目录不再当「误建残留」递归删除——第二次编辑会话时目录是刚从 store 还原的用户配置快照，此前被删并改用默认模板覆盖（提交则 store 被模板污染）；现目录非空即视为已有配置跳过模板生成、空目录仍复制模板兜底，文件型误建目录保留原自愈清理。
- **pending 系统操作叠加双执行（台账外 KN-66）**：60 秒窗口内多个队列先后完成时，新 pending 覆盖旧 pending 前先取消旧任务的 Cts——此前旧 sleep 的 `Task.Delay` 未取消，到期仍执行休眠（双系统操作真实触发）。
- **KN-05 CLI 删除脚本/队列不清理**：脚本删除对齐 Web 端——运行中拒绝、清理 `data/{脚本Id}` 目录、释放 ScriptConfigGate 与跨进程互斥体（此前仅移除列表，磁盘残留 + 静态字典累积）；队列删除增加运行中拒绝。
- **KN-11 Python 判断脚本尾行 JSON 丢失竞态**：`WaitForExitAsync` 后补同步 `WaitForExit()` 排空输出缓冲——进程退出瞬间异步输出事件未投递完时契约规定的 stdout 尾行 JSON 不再丢失。
- **KN-37 用户改名/删除大小写敏感**：查询/存在性校验/删除统一 `OrdinalIgnoreCase`，与重名查重和 users/order 口径一致（此前 `ABC` 用户用 `abc` 删除 404，语义矛盾）。
- **KN-34 Webhook 文本转义不完整**：`JsonLiteral` 改 System.Text.Json 序列化（`UnsafeRelaxedJsonEscaping` 保留中文原样、`\b`/`\f` 等控制字符正确转义）——含控制字符的通知文本不再致 Webhook 端 JSON 解析失败。
- **KN-30 settings PUT 双次 Save**：`allowRemoteAccess` 已在通用反射路径绑定，删除冗余二次绑定与二次保存（双次 Normalize/写盘副作用消除）。
- **KN-40 插件开关任意串按 disable 处理**：`/api/plugins/{name}` 显式校验 enable/disable，其余 400。
- **KN-07 resolve.json 多占位符静默丢弃**：路径/参数模板含多个占位符时显式校验并整体推导失败（Warn 可观测），不再静默截断。
- **KN-26 Webhook 类型白名单双份维护**：单源化至 `AppSettings.WebhookTypes`（`ConfigStore.Normalize` 与 `WebhookSender` 共用，消除漂移）。
- **KN-24 插件开关静默写入不存在插件名**：`SetEnabled` 显式拒绝不存在的插件（此前静默写配置待下次加载清理）；DisabledPlugins 写入保留（实为 `ConfigStore.Normalize`「旧配置补默认内置插件」判据，非纯冗余）。
- **KN-25 尝试日志段首尾不对称**：段起点移至「开始」头之前，与「结束」头对称（判断脚本输入与按尝试分批归档日志现在包含完整尝试边界）。
- **KN-14 基础按钮触控目标补修**：基础 `button` min-height 38px → 40px（v0.7.3 台账漏报缺口，`.sm`/drag-handle/侧栏按钮已达标）。
- **KN-47 侧栏地址硬编码**：`/api/status` 补 `actualPort`（实际监听端口，端口漂移/未重启时与配置不同），侧栏「服务 · host:port」动态化。

### 清理（低优先级死代码全量）

- 后端：`SendStrategy` 死配置（KN-17，字段/Normalize/回显/CLI 菜单全链路移除）、`Audit.Cli`（KN-18）、`IsSupportedLanguage`（KN-19）、`LogLevel.ToSetting`（KN-20）、`SpecializedPlugins`（KN-21）、`runUsers.Add(null)` 不可达兜底（KN-22）、`OfType<IPlugin>()` 恒真过滤（KN-23）、`KillTree` 文案误导（KN-28）、`SessionJudge` 嵌套 enum 改 internal（KN-29）、`else if (!queue.NotifyEnabled)` 冗余（KN-38）、`PromptEdit`/`PromptEditMasked` 合并（KN-41）。
- 前端：`dayDesc`/`actionLabel`/`unregisterPager`/`FALLBACK_ICON` 死代码（KN-43）、select click+change 双触发（click 委托跳过 select/option，change 唯一分发）与 `selectField` option 转义（KN-44）、localStorage 读写异常保护（KN-45，主题/token/重启轮询全量 try/catch——隐私模式/禁用存储下不再白屏）。
- **KN-46 台账核验**：前端 `queueTotalUsers` 与后端 `Limits.QueueTotalUsers` 逐行对应（未选脚本计 1、启用用户 `Math.max(1,…)`、保存前空任务已过滤）——两端口径实际一致，台账表述过时。
- **KN-14/KN-47 台账表述修正**（KN-14 v0.7.3 时未含基础按钮缺口、KN-47 双按钮已随 v0.7.3 自动保存消失）。

### 测试

- 断言数字：单测 97、e2e 76、chaos 166 不变（chaos 加速档实测 167 / 真实档 166——固定轮 config 采样对丙的 `fastOk` 兜底断言随「采样直接命中与否」动态触发，口径取真实档发布门禁值 166）；F5 归档兜底 `archivedLogWritten` 兼容 KN-25 后归档日志「开始」头首行——此前兜底失效属测试侧未随语义同步。
- 加速档全量回归：e2e 76/76（4.1m）+ judge 115/0 + chaos 167/0 + 单测 97/97 全绿（2026-08-16）。
- 真实计时档全量回归（发布门禁）：e2e 76/76（8.4m）+ judge 115/0 + chaos 166/0 + 单测 97/97 全绿（2026-08-16）。
- 首轮发现与修复：judge 5 项通知断言失败——KN-34 初版 `JsonSerializer.Serialize` 默认编码器将中文转义为 `\uXXXX`（合法 JSON 但破坏既有通知契约与原始字符串断言），改 `UnsafeRelaxedJsonEscaping` 后恢复全绿；chaos 2 项失败——KN-25 后归档日志首行为「开始」头致 F5 兜底失效，断言同步后全绿。

### 变更

- 版本号 0.7.4。

## v0.7.3（Pre-release）

### 新增（前端设计全面重构，对照 Web Interface Guidelines）

- **内联表单错误**（KN-58）：`setFieldError`/`clearFieldError` 通用组件——校验失败字段内联错误文字（`role=alert`）+ `aria-invalid` + 聚焦，替代仅 toast；覆盖脚本/用户/队列保存全部校验路径。
- **提交按钮忙碌态**（KN-59）：`withBusy` 通用组件——保存/执行/删除/重启/完成编辑等请求期间禁用按钮 + spinner（防重复提交），7 个视图提交类 actions 全覆盖。
- **拖拽键盘替代**（KN-60）：drag-handle 改为可聚焦（`role=button` + `tabindex=0` + aria-label），聚焦后 ↑/↓ 键控重排并提交（脚本/队列/用户/弹窗定时/弹窗任务 5 处自动生效）+ focus-visible 焦点环。
- **dispatch 运行面板局部更新**（KN-16）：2 秒轮询改为按 runId 增删改任务卡片（不重建 DOM，保留取消按钮焦点与日志选区）；日志仅「贴底时」自动滚动（用户上翻阅读不再被打断）；标题计数 aria-live 播报。
- **limits 警告层无障碍四件套**（KN-13）：`aria-labelledby` + 初始焦点 + 焦点陷阱 + 关闭后焦点恢复 + Esc 关闭。
- **ARIA 全量补全**（KN-62）：pager 当前页 `aria-current="page"`；移动端菜单按钮 `aria-expanded/aria-controls`；三处表格 `th scope="col"`；`nav-backdrop` 由可点击 div 改 `<button>`（点击遮罩关闭保留）；panel-toggle 补 `aria-controls`；队列任务下拉补 `aria-label`（KN-64）。
- **路由焦点重置**（KN-61）：视图渲染后统一 `#view.focus({ preventScroll: true })`，键盘用户切页从内容区继续导航。
- **交互态与文案**：`.list-item`/`.plugin-card`/`.timeset-card` hover 背景补齐（KN-63）、plugin-card 描述 line-clamp 2 防溢出；8 处英文大写 eyebrow 中文化 + `text-transform: uppercase` 移除（KN-15）；触控目标统一 ≥40px（`.sm`/drag-handle/侧栏主题按钮，KN-14）；modal 焦点陷阱 focusout 兜底（KN-12）；`img` 显式尺寸、统计数字 `tabular-nums`、placeholder `…` 规范化、`.fs-item` 死代码清理。
- **设置页自动保存（用户需求）**：取消「保存设置」按钮（含卡片内分隔横线）——四个切换开关点击即存、输入/下拉失焦即存、生成令牌即存；保存串行化防并发乱序、成功静默更新内存状态（不整页重渲染）、失败 toast；远程访问开关切换后局域网地址列表局部刷新。
- **重启服务按钮移位（用户需求）**：移至页面右上角（同调度队列页新建按钮位置，主色样式），`data-testid="restart-service"` 保留；点击前等待挂起的自动保存完成，移除「端口未保存拒绝重启」校验（自动保存已即时落库）。
- **分页按需显示（用户需求）**：脚本/队列/历史/用户分页条仅在超过一页时渲染（`pagerMarkup` 单页返回空）。

### 测试

- e2e 全量 **76**、单测 **97** 不变（纯前端改动，用例数与断言数无增减）。
- 加速档全量回归：e2e 76/76（4.0m）+ 单测 97/97 全绿（2026-08-16）。

### 变更

- 版本号 0.7.3。

## v0.7.2（Pre-release）

### 修复

- **KN-01 损坏配置被静默覆盖（数据丢失）**：scripts/queues/settings.json 解析失败时原文件自动改名保留（`*.corrupt-时间戳`），后续任意一次保存不再覆盖损坏数据；日志与审计明确提示保留位置，可手动恢复。
- **KN-03 队列重复触发（双跑）**：DispatchCenter 注册锁内对调度队列对称查重——手动（Web/CLI/manage）与定时并发触发同一队列时后者 400 拒绝；此前仅定时入口有 `_runningQueueIds` 防重，手动入口可双跑致双历史/双通知/双完成操作（如双关机命令）。
- **KN-04 共享集合无锁并发**：`RuntimeContext.Scripts/Queues` 引入 `DataLock`——修改侧锁内完成「读-改-写」整段、读取侧锁内枚举或深拷贝快照（`SnapshotScripts/SnapshotQueues`）；`RunningExecution.Records` 锁内追加/快照；调度器每秒枚举与 Web 请求并发修改不再抛「集合已修改」/越界异常（本地/远程均生效）。
- **KN-10 明文旧密钥回显**：GET /api/settings 对未加密（旧版明文或手工编辑）的 Webhook/SMTP 授权码/访问令牌一律返回占位符，不再回显明文；accessToken 判定与其余密钥统一。
- **KN-32 PUT /api/settings 空字段名 500**：请求体含空键时显式 400「请求体包含空字段名」（此前 `field[0]` 抛 IndexOutOfRange → 500）。
- **KN-36 删除脚本与配置交换并发崩溃**：`WithSwapLock` 的 `WaitOne` 补捕获 `ObjectDisposedException`（`RemoveMutex` 并发 Dispose 触发），移除条目重建互斥体重试一次。
- **KN-42 编辑配置会话门禁泄漏**：会话已注册（keepGate）后写响应异常/客户端断开时主动清理现场——结束已启动的编辑进程、还原配置交换与隐藏配置、移除会话并释放门禁；清理失败保留标记交由自愈/后台重试兜底，不再占住脚本直到重启。
- 顺手：`ScriptInstance` 成功关键字注释过时修正（v0.7.1 跨行 AND 语义）；FLAKE-LEDGER F1/F2/F3 状态列同步为已关闭（多轮全量含发布门禁未复现）并修正回归记录行序；AGENTS/DEVELOPMENT/RELEASING 断言数字同步。

### 测试

- 单测 **95 → 97**（+2：KN-01 损坏配置改名保留 / 合法文件正常加载且不产生保留文件）；e2e 全量 **75 → 76**（+1：KN-03 队列运行中重复触发被拒——首次触发进入运行中后再次触发断言 400 拒绝）；judge 115、chaos 166 不变。
- 加速档全量回归：e2e 76/76（4.1m）+ judge 115/0 + chaos 166/0 + 单测 97/97 全绿（2026-08-16）。

### 变更

- 版本号 0.7.2。

## v0.7.1（Pre-release）

### 修复

- **成功/失败关键字 AND 语义改为跨日志匹配（用户需求确认）**：组内逗号分隔的关键字在整个**尝试日志**中分别出现即命中（跨行累积、与出现顺序/间隔无关）——此前要求同一行内全部出现，跨行分散的关键字永不判定成功；失败关键字同语义。判定状态严格限定当前尝试：失败重试后新尝试从「尝试开始时的文件长度」续读日志，重试前的日志不会进入判定输入（`SessionJudge` 每次尝试新建 + `LogMonitor` 尝试切片双保险）。
- **KN-02 POST 注入已存在 Id 造成重复记录**：新建脚本/队列一律重新生成 Id，客户端提交的已存在 Id 不再保留。
- **KN-51 托盘「打开管理页面」端口漂移**：改用实际监听端口（`WebServer.Current.Port` 优先，回退 `Settings.WebPort`）——设置页改端口未重启 / 启动时端口冲突自动 +1 时不再打开 404。
- **KN-52 CLI 历史保留天数校验不一致**：与 Web 端 `Limits.CheckRetentionDays` 口径统一（1-上限），越界输入显式报错，不再被 `ConfigStore.Normalize` 静默重置为 7。
- **KN-56 CLI `PromptEdit` 输入重定向崩溃**：`Console.ReadKey` 在 stdin 重定向（管道/自动化）下抛未处理异常直接崩溃，降级 `ReadLine`（空行=不变），`PromptEditMasked` 同处理。
- **KN-53（台账核验）**：CLI 脚本菜单保存前已调用 `Limits.CheckScriptTimeouts`（含 -1 成对校验），台账标记已修复。
- **KN-54 完成操作语义文档化**：队列完成操作在任务失败（非取消）时仍执行——语义经用户确认保留，README/DESIGN 补说明。

### 变更

- **队列弹窗空任务列表不再显示空卡片**：无任务时隐藏 `tasks-card` 外壳（「+ 添加任务」按钮保留为首添入口）；空列表时不再对不存在的 `qm-tasks` 节点注册拖拽（此前会抛 TypeError）。
- **超长 placeholder 缩短 + 常驻说明**：脚本弹窗「日志无更新超时 / 运行总时间超时」placeholder 缩短为「-1 = 不超时（长时脚本）」，完整语义（同为 -1 即长时、不能只填其一、不能与普通脚本混排队列）移入运行设置区常驻 muted 说明。

### 测试

- 单测 **96 → 95**（删除行内 `LineHits` 5 项，新增跨行 AND 4 项：跨行累积 / 顺序无关 / 单词不命中 / 大小写不敏感）；e2e 全量 **75**（新增 KN-02 Id 注入用例；06 关键字用例「AND 跨行不命中」反转为「跨行分别出现 → success」+ 新增「只出现一个词 → failed」防回归）；judge 115、chaos 166 不变。
- 真实计时档发布门禁：e2e 75/75 + judge 115/0 + chaos 166/0 全绿（2026-08-15）。

### 变更

- 版本号 0.7.1。

## v0.7.0（Pre-release）

### 新增

- **安卓模拟器启动方式（GameMode）**：脚本实例新增「启动方式」——PC 客户端 / 安卓模拟器（旧配置兼容默认 pc）；模拟器模式复用 `GameExe`=ADB 地址（`host:port`）、`GameArgs`=am start 参数（`-n 包名/.Activity`）：
  - **运行链路**（`RunSession` 分叉）：插件启用检查 → adb 解析（测试钩子 `NEXUS_ADB_EXE` 优先 / PATH / MuMu 安装目录兜底）→ `adb connect` → `am start` → 前台确认（`dumpsys window` 解析 mCurrentFocus，目标包名从 `-n` 解析，解析不到宽松通过）→ 脚本运行；尝试失败收尾 `am force-stop` 当前前台应用（桌面/系统界面跳过）；运行结束（成功/最终失败）且「强制关闭」开启 → 关闭整个模拟器；取消不处理；窗口前置/进程树排除对模拟器模式跳过；
  - **模拟器关闭（MuMu 专项）**：`adb emu kill` 对 MuMu 12 无效（实测）——`MuMuManager info -v all` 按 adb 端口反查 vmindex → `control -v <idx> shutdown`（官方优雅关闭）→ 回退 `adb shell reboot -p`（实测有效）→ 轮询确认离线，失败降级明确告警；
  - **内置能力插件「模拟器适配」**（`emulator-adapter`，默认启用，可禁用不可删）：禁用后前端不渲染「启动方式」选择器 + 模拟器运行被拒；专用插件按 `plugin.json` 的 `supportsEmulator` 声明（缺省 false，仅 maaend=true），声明缺失的专项用模拟器启动 → 400；
  - **前端**：游戏卡「启动方式」选择器（插在「启动后等待秒数」左侧，两格等宽同行）；选模拟器后游戏路径变「模拟器ADB地址」、启动参数 placeholder 提示 `-n 包名/.MainActivity`；保存时 ADB 地址格式前端校验；
  - **判断脚本输入**：`JudgeScriptRunner.BuildInput` 补 `gameMode` 字段；`Limits.CheckScriptPaths` 分叉（模拟器=ADB 地址格式校验，PC=可执行文件）。

### 修复

- **adb connect 假成功**：`adb connect` 对拒绝连接目标退出码仍为 0（实测 10061），按输出失败标记（cannot/failed/unable to connect）识别——连接失败信息从误导的「模拟器应用启动失败」纠正为「模拟器连接失败」，不再多绕一次 am start。
- **am start 错误码不可靠**：无效 Activity 时 am 输出 `Error: ...` 但退出码为 0（实测），按输出 `error` 标记立即失败并携带原始错误，不再白等 `GameWaitSeconds` 前台确认轮询。
- **关闭模拟器「虚假成功」**：`ShutdownEmulatorAsync` 此前丢弃离线轮询结果，MuMuManager 返回 0 但未实际关闭时会误报成功；现每条关闭路径均以确认离线为成功凭据，超时降级失败并告警。
- **API 响应缺 `Cache-Control: no-cache`**：`/api/status` 等 API 响应此前无缓存头，浏览器启发式缓存致插件状态变更后刷新页面仍读到旧值（如禁用「模拟器适配」后选择器残留）；与静态文件（v0.6.9 P13）对齐补齐。

### 变更

- **「启动后等待秒数」宽度统一**：游戏卡「启动方式 + 等待秒数」行统一为双格等宽布局（各约半宽）；模拟器适配不可用时（专项/插件禁用）等待秒数顶替选择框位置、右格空出——三种场景排版一致。
- **插件能力卡片瀑布流**：仪表盘插件卡片从 3 列 grid（行内矮卡片留空隙）改为 CSS columns 瀑布流——卡片保持高度自适应、列内紧密排列无行空隙，手机单列。
- 文档措辞优化（README/DESIGN/ARCHITECTURE/AGENTS/CHANGELOG/RELEASING 等）。

### 测试

- 单测 **62 → 96**（+34：ADB 地址/`-n` 解析/dumpsys 前台/MuMuManager 反查/am start 错误识别）；e2e 全量 **64 → 74**（+10：模拟器前端联动/专项声明/非法地址拦截/后端拒绝/全链路/失败重试 force-stop/不关开关/插件禁用/连接失败与 am start 失败立即判定；08-emulator.spec 10 用例）、CI 核心集 63 → 73；judge 115、chaos 166 不变。
- **真实模拟器实测（MuMu 16384）**：成功链路 / 失败重试 force-stop / 桌面跳过 / `ForceCloseGame=true` 真实关闭（MuMuManager 反查实例 0 → shutdown 1.6s 离线 → launch 15s 重启恢复）/ 不关开关——全部通过；dumpsys 与 MuMuManager 真实输出格式与解析代码完全匹配。
- **测试基建**：`WaitEmulatorOfflineAsync` 60 秒上限补 `NEXUS_TIME_SCALE` 缩放（v0.6.4 加速基建遗漏，加速档每关机场景白等 60s——08 spec 从 2.3m 降到 21s）；chaos F5 采样兜底补归档日志证据（含 UTF-8 BOM 剥离），F5 关闭。
- 真实计时档发布门禁：e2e 74/74 + judge 115/0 + chaos 166/0 全绿（2026-08-15）。

### 变更

- 版本号 0.7.0。

## v0.6.10（Pre-release）

### 新增

- **长时脚本实例**：`日志无更新超时` 与 `运行总时间超时` 均设为 **-1** 即长时脚本（无限超时，挂机场景）：
  - **-1 成对校验**：任一为 -1 而另一为正常值 → 保存拒绝（避免半长时语义歧义），前端/Web API/CLI 三处统一；
  - **队列混排拦截**：长时脚本不能与普通脚本编排进同一队列（链式执行会被无限阻塞）——保存校验（Web/CLI）+ 运行期防御（手动执行拒绝、自动调度跳过并记录失败历史）+ 前端任务下拉「（长时）」标注与保存拦截；
  - **前端**：脚本卡片「长时」徽章；超时输入框 placeholder 提示「填入 -1 表示不超时（长时脚本）」；
  - 运行时语义零改动：`TotalTimeoutMinutes > 0` 判断天然支持 -1（判断脚本周期触发 30 秒与成功关键字等待退出 60 秒在长时下仍生效）。
- **队列编辑弹窗拖拽排序**：定时列表与任务列表支持拖拽排序（复用 `core/dnd.js` 把手组件）——任务列表废除上/下移按钮（拖拽后 `index` 字段重排、顺序落盘）；定时列表按数组顺序执行；`syncQueueDraftFromDom` 改按元素携带的 `data-ts-idx` 写回（DOM 顺序与数组顺序脱钩后仍正确）。
- **任务列表卡片化**：任务列表合并为整体卡片（与定时列表卡片同宽同构），删除行内序号文字，删除按钮宽度与定时列表删除按钮一致（84px）。

### 变更

- 文档体系重组：README 大众化重写（面向普通用户，快速上手以专项插件为例）、CONTRIBUTING 大幅扩充、docs/DEVELOPMENT 重构为开发环境指南、新增 docs/RELEASING（发布流程）与 docs/KNOWN_ISSUES（已知问题台账）、开发清单入库 docs/ROADMAP、ci.yml 混合编码修复为 UTF-8、DESIGN/ARCHITECTURE 过时内容修正。
- 长时脚本卡片取消 accent 底色高亮（仅保留「长时」徽章，用户决策）。

### 测试

- e2e 全量 60 → **64**（新增：长时脚本 -1 成对校验/保存/徽章、长时运行不因日志无更新超时失败、队列混排拒绝与纯长时通过、队列编辑弹窗定时/任务拖拽排序）、CI 核心集 59 → **63**；单元测试 58 → **62**（新增长时语义与成对校验断言）；judge 115、chaos 166 不变；加速档全绿（2026-08-15）。

### 变更

- 版本号 0.6.10。

## v0.6.9（Pre-release）

### 修复

- **测试 flake 治理（本版首要目标）**：
  - **服务「无日志死亡」级联失败根治（F1/F4）**：`killRuntimeServices` 固定 600ms 等待改为**轮询确认 runtime 进程完全消失**再启动新服务——旧进程单实例互斥体未释放时 web 模式静默退出（仅 Info 日志、stdio ignore 丢弃），曾致 02 文件末尾用例后 03-05 全部 ECONNREFUSED；`Program.cs` web 模式互斥失败日志升级 Warn 并附诊断；`waitForService` 失败自动输出进程/端口/服务日志尾部现场；
  - **flake 监控采样器 `uitest/flake-monitor.mjs`**：500ms 采样 nexus-pipeline 进程存在性与 58731 监听状态（进程名+端口判定，测试监控进程无管理员权限也能用），日志与停止信号在 `uitest/flake-monitor-logs/`（独立目录，playwright 每轮清空 test-results 不受影响）；flake 台账 `uitest/FLAKE-LEDGER.md`（现象/复现条件/根因/处置/回归记录，每次全量回归更新直至清零）；
  - **级联隔离兜底（A5）**：7 个 spec 文件模块加载时 `ensureService`——服务不可达自动强杀残留并重拉 web 模式，单文件失败不再带崩后续文件；
  - **重启服务前端恢复加固（F3）**：05 重启用例加 pageerror/console.error 探针，「正在连接本地服务...」loading 滞留时重载页面重试（3 次），失败输出探针现场；
  - **用例删除残留根治（F2）**：02 排序用例 finally 删除改「res.ok + 轮询确认列表消失 + sid2 未赋值跳过」（此前不检查响应且可能删 null，残留导致后续卡片数断言失准），用例开头按名防御清理；
  - **chaos 采样断言治理（F5）**：乙/丙快速成功轮（判定→收尾仅数百毫秒）`seen` 采样缺失时以历史记录 + 日志文件采样佐证（复用 `maxDone` noSkip 先例），其余用户保持严格断言。
- **技术债清理（P1-P16）**：
  - **P1 移除 `QueueTask.UserName` 死字段**：队列任务级指定用户名从未生效（运行时静默跑全部启用用户），前端弹窗/后端模型全链路移除；
  - **P3/P10 Logger 依赖环与跨午夜日志滚动**：`Logger` 不再依赖 `Persistence.AppPaths`（Utilities→Persistence 反向依赖解除），日志文件路径按天实时求值（跨午夜自动滚动）；
  - **P2 配置交换自愈语义对齐**：`RecoverIfNeeded`/`TryRecoverItem` 的 cache 空分支统一——`GeneratedTemplate`（编辑会话模板产物）必须 `DoRestore` 清理（恢复编辑前状态），非模板会话仅清标记（防窄窗口误删用户新写入的 config）；
  - **P5 Python 判断脚本 stderr 可观测**：stderr 独立收集，无合法 JSON 输出时尾部（最多 10 行/800 字符）放入 JudgeError + Logger.Warn（stdout 无结果时保持宽容回退 stderr 解析）；
  - **P6 配置替换延迟到杀进程确认退出后应用**：判断脚本 `failed`+`replaceConfigs` 不再在进程运行时复制覆盖 config（文件占用/半写窗口），改在尝试收尾应用、供重试轮使用（重试轮不重新 PrepareForRun）；judge 单文件 config 用例断言同步（服务日志佐证）；
  - **P7 exit 完成操作收尾竞态**：`Application.Exit()` 延迟到队列 `finally`（FinishedAt/Unregister）之后，CLI 轮询不再查不到结果；
  - **P4 HistoryService 清理加锁**：`Cleanup` 与 `Save` 共享 Sync 锁（删除中的 dayDir 被重建后历史丢失）；
  - **P9 定时时间格式校验**：API 保存时严格 `HH:mm` 校验（"8:00" → 400 报错），Normalize 回退保留给旧数据兼容；
  - **P12 token 输入层无障碍**：token-mask 自绘遮罩（内联 style + 硬编码色值 + 无 dialog/焦点陷阱）改为复用 `showModal(..., locked)` 模态组件（role=dialog/aria-modal/焦点陷阱/焦点恢复）；
  - **P13 安全加固**：令牌比较改 `CryptographicOperations.FixedTimeEquals`（常量时间，防时序侧信道）；`/api/logs` 孤儿 API 移除（无任何前端/测试引用，`AppPaths.LogFile` 随之删除）；静态文件补 `X-Content-Type-Options`/`Referrer-Policy`/CSP 安全头；
  - **P8 日志截断重读重复行**：部分截断（缩短未归零）从新文件尾续读，长度归零仍从头读（契约不变）；
  - **P11 轻量模式托盘**：「打开管理页面」菜单项禁用 + tooltip 说明，`OpenWeb` 防御提示（不再打开 404）；
  - **P14/P15 文档**：`plugins/README.md` 明示 resolve.json 占位符仅整体替换；AGENTS.md 审计豁免措辞修正（审计行 INFO 随阈值过滤、无豁免）。

### 测试

- e2e 全量 60（加速档 3 轮连续回归 + 发布前真实计时档）、judge-scenarios 115、chaos-queue 166（断言数字以发布前真实计时档为准；加速档分支差异 ±1）、单测 58 全绿。

### 变更

- 版本号 0.6.9。

## v0.6.8（Pre-release）

### 新增

- **拖拽排序（脚本实例 / 调度队列 / 用户卡片）**：新增通用拖拽组件 `core/dnd.js`（Pointer Events 统一鼠标/触屏，`[data-dnd-id]` 项 + `.drag-handle` 把手）；脚本实例与调度队列新增 `Index` 字段落盘 + `PUT /api/scripts/order` / `PUT /api/queues/order`（全量 id 名单一致校验，仿用户 order 协议）；CLI 菜单（scripts/queues/dispatch/status）同步按 Index 排序展示与新建追加；用户卡片废除「上移/下移」按钮改拖拽（沿用 `PUT users/order` names 协议）；拖拽结束视图提交全量顺序（可见项重排 + 其余项保持相对顺序），失败回滚重渲染。e2e 新增脚本/队列卡片拖拽用例、用户排序用例改写为拖拽语义（2 新增，全量 58 → **60**、CI 57 → **59**）。

### 变更

- **日志全量更新（操作级审计 + 错误补漏）**：`Logger` 时间戳毫秒化（`[HH:mm:ss]` → `[HH:mm:ss.fff]`）；全库静默 catch 清扫——17 处 A 类补齐 `Logger.Warn/Error`（含 `ResolveLaunchTarget` 路径解析异常回退警告、`IsExeRunning` 进程检测失败按未运行处理、`IsSameProcessName` 失败按非游戏进程处理、插件配置损坏与专项判断脚本读取失败、`.meta` 备份清单损坏**保留备份现场不再删除**、取消信号发送失败、HTTP 监听循环异常退出记录）等；Web 写操作审计覆盖核对（dispatch/cancel/system-action/plugins 均经由服务层记录，全部覆盖）。
- 版本号 0.6.8。

## v0.6.7（Pre-release）

### 修复

- **设置页「重启服务」前端卡死（严重 BUG）**：`views/settings.js` 误用 `state.schedule(...)`（`state` 对象无该方法，`schedule` 为独立导出函数）——首次轮询即抛 TypeError，而「服务重启中」为锁定弹窗（Esc/遮罩/× 不可关闭）导致页面永久卡死，只能手动刷新。改为导入独立 `schedule` 并绑定 settings 页生命周期（page/token 守卫）。e2e 05-settings 重启用例补充前端断言（锁定弹窗出现 + 服务恢复后自动关闭并刷新页面），此前该失败路径无任何测试覆盖。
- **仪表盘轮询与定时器治理**：`startCountdown` 每次调用注册新 1 秒 interval 不清旧定时器（仪表盘 3 秒重渲染反复调用 → 停留期间累积数百个空转定时器）——仿 `startSystemActionCountdown` 增加模块级旧定时器清理，并新增 `stopCountdown` 导出；仪表盘 3 秒全页重渲染改**局部更新**（统计卡/系统操作卡/运行面板/插件面板按区域刷新，不再整页 render，避免重置滚动/焦点与反复重建倒计时）。
- **CLI 体验 3 项**：`PollCliRun` 增加 6 小时总超时上限（此前可无限轮询挂死，超时后提示经 Web/manage 查看状态）；`run-script -user` 缺值时明确报错并提示用法（此前静默忽略）；提交成功但响应解析失败（非 JSON/缺 runId）时提示「任务已提交但无法轮询结果」而非误报「提交任务失败」。
- **钉钉/飞书 Webhook 签名按官方规范修正**：钉钉签名此前为**秒级时间戳 + hex 摘要 + 签名参数放消息体**三重不合规（必失败）——改为毫秒时间戳 + HMAC-SHA256 Base64 签名追加到 Webhook URL 查询参数（`timestamp`/`sign`）；飞书签名由消息体移至请求头 `X-Lark-Request-Timestamp`/`X-Lark-Signature`（秒级时间戳 + Base64，算法不变）。签名算法本身（`timestamp\nsecret` 为 HMAC 密钥、空消息）正确保留。真机推送验证仍需真实机器人环境。
- **前端小修**：脚本卡片/队列任务/调度中心下拉/历史详情 5 处属性插值补 `esc()`（`data-id`、`option value`，防属性注入）；用户删除原生 `confirm()` 改 `confirmModal`（统一 `role="dialog"` 弹窗规范）。
- **测试治理**：e2e 01-core 版本断言改从 `/api/status` 的 version 字段动态读取（此前硬编码 0.6.6，发版漏改即误红）。

### 变更

- 版本号 0.6.7（测试用例数不变：e2e 57 / CI 56、judge 115、chaos 171、单测 58）。

## v0.6.6（Pre-release）

### 变更

- **修复配置交换残留（严重 BUG）**：编辑配置 done/cancel 自动结束脚本进程并确认退出（`KillAndConfirmExited` 改返回 bool，按启动目标名轮询强杀处理防崩溃自重启脚本如 BetterGI）——持续自重启杀不干净时拒绝执行文件交换并返回引导文案「请先在托盘退出脚本后重试」；文件交换成功后才移除编辑会话（失败可原地重试，`.session` 标记由自愈/后台重试兜底）；`TryRecoverItem` 检测脚本进程仍在运行（如「强制关闭服务 + 先启动脚本再启动服务」）时跳过恢复动作、进程退出后由后台重试自动完成（避免误删/误覆盖正在使用的配置）。
- **修复游戏窗口前置未生效**：游戏由启动器延迟拉起时启动瞬间检测不到——`BringGameToFrontIfRunning` 改为监控循环内轮询检测（每轮按名检测，出现即前置，`_gameFronted` 标志只前置一次；复用 `BringToFront` 30 秒窗口覆盖「进程出现但窗口未建」）。
- **修复 build.cmd 增量构建指纹失效**：for /f 对超长哈希（>8191 行缓冲）捕获为空导致指纹写坏（「ECHO 处于关闭状态。」，曾致发布旧 exe）；括号块内 `%VAR%` 解析时展开导致增量判断恒失败——改 PowerShell 内二次哈希（64 字符输出）+ goto 标签结构，内容变化必全量 publish、稳定态增量跳过。
- **配置与日志维护**：`ConfigStore.Normalize` 历史保留天数上限改由 `limits.json` 约束（消除硬编码 180，`Limits.Load` 同步、加载顺序调整）；`ProtectedData` 10.0.10 → 8.0.0（net8 配套）；`Logger` 日志阈值改缓存（消除每次日志调用提前构造 DI 容器，设置加载/保存后刷新仍即时生效）。
- **CLI/菜单一致性**：调度中心（manage 菜单）统一经常驻服务 HTTP 通道（`CliTransport`，与 CLI run-script 同通道——Web 端可见运行任务，消除进程内直调与 HTTP 通道割裂）；manage 启动时探测常驻服务在跑 → 提示菜单修改可能与 Web 端互相覆盖；菜单保存带异常兜底（`Ui.TrySave`，IO 失败不退出菜单）。
- **启动与互斥加固**：崩溃恢复（`RecoverInterrupted`）仅服务类进程（service/web）执行（manage/status/CLI 由运行时自愈兜底，消除多进程并发恢复竞争）；web 模式抢单实例互斥（常驻服务在跑时直接退出防双写）；web 模式退出循环修复 stdin 重定向永久挂起（EOF 自动退出；无效 stdin 持续运行——e2e 服务启动方式相应改为 stdin pipe）。
- **代码清理**：删除死代码 `LogMonitor.ResolveFile`；`ScriptInstance`/`DispatchQueue`/`RunRecord` 手工 `Clone()` 改序列化深拷贝（防新增字段漂移）；`PluginManager.LoadAll` 幂等（重复调用先清空）。
- **测试**：e2e 新增 1 用例（编辑配置文件被占用时提交失败、释放后重试成功且无残留），全量 56 → **57**、CI 核心集 55 → **56**；`07-limits` 端口占用用例适配 web 模式互斥语义（停服务 + node 监听占端口）；数字同步 AGENTS/README/DEVELOPMENT/ARCHITECTURE。
- 版本号 0.6.6。

## v0.6.5（Pre-release）

### 变更

- **设置页「重启服务」**：常驻服务模式可在 Web 界面一键重启（`POST /api/settings/restart`）——先响应 `{ ok, newPort }` 再由后台拉起 `nexus-pipeline.exe restart` 新进程（等待旧进程退出并接管，最多 30 秒）后退出；前端确认卡片 + 「重启中」锁定弹窗 + 轮询 `/api/status` 恢复后自动刷新（端口漂移自动跳转，含 +1 补偿）；校验：轻量模式 400（重启后无 Web）、web 仅网页模式 400、有运行任务 409。保存设置不自动重启；端口改动未保存时前端提示先保存。
- **单实例互斥体加固（服务强杀恢复）**：`Program.AcquireSingleInstanceMutex` 捕获互斥体被遗弃（abandoned）场景——强杀服务后首次重启不再抛 `AbandonedMutexException` 崩溃退出（此前需启动两次）；`restart` 分支轮询等待旧进程释放互斥体时同样处理遗弃状态。
- **日志监控 fresh 判定收紧（判定输入防污染）**：`RunSession` 在尝试开始前记录日志文件快照（存在性 + 长度），启动前不存在的文件才从头读；已存在的残留日志从「尝试开始时长度」续读——残留被启动后追加写刷新 `LastWriteTime` 不再误判从头读，旧内容不再进入判断脚本/关键字判定输入（`LogMonitor` 新增显式初始读取位置；轮换/替换/截断语义不变）。
- **脚本运行防重入（原子化）**：`DispatchCenter.Register` 锁内按脚本实例查重——并发触发（如双击）不再通过进程检测窗口双开会话，后者返回「正在运行」错误。
- **资源管理加固**：① 前置/后置用户脚本与主脚本进程句柄运行结束即 `Dispose`（此前延迟到 GC）；② `ScriptConfigGate`/`ConfigSwapPrimitives` 静态字典随脚本删除清理（`Mutex` 内核句柄不再累积）；③ `Scheduler.Stop` 释放 CTS；④ 窗口前置后台任务捕获异常不再无观察（fire-and-forget 失败仅告警）；⑤ `Bootstrap.Shutdown` 分步保护，单步异常不中断其余清理。
- **窗口处理分场景重构（截图识别防遮挡）**：运行脚本实例/调度队列时脚本主窗口**最小化**让位（命令行/日志已接管输出）、游戏窗口**前置**；编辑用户配置时主程序窗口**前置**（此前该路径无任何前置逻辑）。前置机制强化——`AttachThreadInput` 模拟前台线程输入绕过 Windows 前台锁定（后台常驻服务进程直接 `SetForegroundWindow` 几乎必然失败，v0.6.0 的前置实际未生效）+ `BringWindowToTop` 置顶 + 失败每 1 秒重试至 30 秒超时（超时 Warn 日志可观测）；全部后台 fire-and-forget 且观察异常。**游戏窗口统一前置**：运行路径检测到游戏进程存在即前置（与 `LaunchGame` 配置无关——游戏启动方式复杂（启动器常驻等）由脚本专门适配，宿主不重复启动；`LaunchGame=true` 宿主启动能力保留为用户可选）。
- **进程树清理排除游戏进程**：`KillTree` 自实现（Toolhelp 快照 + BFS 遍历逐进程 `taskkill /F`，替代 `taskkill /T` 全树）——与 `GameExe` 同名的进程（脚本自启动的游戏，即使父进程是脚本）视为「游戏进程」而非脚本树成员，清理时跳过其整棵子树，生杀归游戏管理（`ForceCloseGame`/失败路径按名关闭）；修复「取消后显示『未发现需要关闭的游戏进程』但游戏被连带关闭」的日志矛盾，并消除 `ForceCloseGame=false` 时游戏被连带误杀的隐患；快照失败回退 `/T` 全树。新增单元测试 7 项（ProcessTreeTests，单测 51 → **58**）。
- **测试**：e2e 新增 2 用例（重启服务自动恢复（service 模式，用例内切换并还原测试环境）、运行任务时 409），全量 54 → **56**、CI 核心集 53 → **55**；`helpers.startService` 支持 service 模式启动；数字同步 AGENTS/README/DEVELOPMENT/ARCHITECTURE。
- 版本号 0.6.5。

## v0.6.4（Pre-release）

### 变更

- **判定语义对齐（脚本/关键字互斥）**：`SessionJudge` 在判断脚本模式下不再解析与匹配成功/失败关键字组（此前脚本模式下关键字仍生效、失败关键字可劫持脚本判定，与「判断脚本优先、忽略关键字」的设计声明不符）；通用脚本用户在脚本模式下残留的关键字不再参与判定，专项脚本行为不变。
- **前端修复**：① 调度中心「取消系统操作」对已解析响应再调 `.json()` 的 TypeError（取消后 UI 不刷新并弹错误 toast）；② `"cancel-system-action"` 双视图同名动作静默覆盖（dashboard 版死代码），收敛为 `core/ui.js` 全局 shell 动作（仪表盘/调度中心共用、局部刷新卡片）；③ 清理死监听（`#sm-judge-enabled`）、死条件（`attempt.number === 1 || true`）与过时字段兜底（`attemptCount`）。
- **安全加固（Web 层）**：① 跨站请求防护——带 `Origin` 头的请求必须来自合法源（回环或本机局域网地址且与请求 `Host` 一致），阻止任意网页触发的 CSRF 简单请求与 DNS rebinding（CLI/curl 无 Origin 不受限）；② 远程认证防爆破——连续 5 次令牌失败按远端 IP 锁定 60 秒；③ `/api/fs/browse` 白名单——仅允许已配置脚本的根目录/配置路径/游戏路径及其子路径（403 拒绝任意盘符遍历，e2e 断言同步）；④ 请求体大小上限 10MB（超限 413）。
- **测试提速（唯一加速档收敛）**：
  - 时间加速档统一为 `NEXUS_TIME_SCALE=10`（替代 60）：stall 6 秒/周期触发 3 秒/marker 宽限 6 秒；`run-uitest.cmd` 默认档、CI 与文档同步；真实计时档仅发布前最终回归；
  - `build.cmd` 增量构建：src 无变化时跳过 `dotnet publish`，仅同步 wwwroot/plugins（指纹 `.build-src-hash` 不入库；CI 全新检出仍全量）；
  - e2e 三处既有耗时缺陷修复（加速档全量 11.9 分钟 → **2.8 分钟**）：`下一调度/通知统计` 与 `BetterGI 图标` 用例的 `configPath=runtimeDir` 导致添加用户时配置快照递归复制自身（各 86 秒 + PathTooLong）；`CLI 自动拉起` 用例 spawnSync 等待 CLI 拉起的常驻服务继承的 stdout 管道 EOF 直至 120 秒超时（改异步 spawn + exit 事件）；
  - 加速档余量适配 scale=10：judge `STUCK_PINGS` 3→6（卡住 5s > 周期 3s）、零日志卡住 `ping -n 5`→10（> stall 6s）、chaos `STUCK_PINGS` 3→8（> stall 6s）、06 周期触发 `ping -n 5`→6；
  - 07 FATAL 负向等待 8s→5s；CI 从真实档改 scale=10 核心集（脱离 15 分钟超时风险）。
- **单元测试工程（试点）**：新增 `src/NexusPipeline.Tests/`（xUnit，net8.0-windows，毫秒级、无管理员）：51 断言覆盖 `SessionJudge` 判定状态机（含新模式互斥语义与失败优先/防抖）、`KeywordRule`（行内 AND/行间 OR）、`LogPattern`（精确/目录/日期占位符/通配解析）、`ScriptUserRule`/`QueueRule`/`ScriptInstance.Clone`；CI 新增 `dotnet test` 步骤；主工程 `InternalsVisibleTo` 暴露 internal 契约、`Compile Remove` 排除测试目录。
- **MaaEnd 默认配置模板**：`plugins/maaend/data/config-template/` 新增 `mxu-MaaEnd.json`（完整 MXU 实例/任务核心配置，含默认实例与设置项，任务列表由用户编辑时自行添加）与 `maa_option.json`（绘制质量/日志等选项）；`plugin.json` 声明 `configTemplate`。配套修复 `EnsureConfigForEdit` 对**目录型 ConfigPath** 的支持——模板整体复制到 ConfigPath 本身（此前仅适配 BetterGI 单文件型，复制目标固定为父目录会把目录型模板错放到脚本根目录），复制清单统一相对 ConfigPath 父目录记录，cancel/重启恢复按清单精确清理；MaaEnd 编辑会话在 MXU 尚未运行过（config 目录不存在）时也能直接生成默认配置进行编辑。
- 版本号 0.6.4。

## v0.6.3（Pre-release）

### 变更

- **废弃通用完成标志（SuccessMarkers）**：判定优先级收敛为 判断脚本（脚本优先）→ 成功/失败关键字 → 无任何配置按「进程自行退出」判定成功。`SuccessMarkers` 从模型（`ScriptInstance`/`ScriptProfile`）、判定器（`SessionJudge`）、插件契约、`ApplyProfile` 与扩展适配器注释全链路删除；旧版 `config/scripts.json` 残留字段反序列化自动忽略（JsonOpts 未开启未知属性报错），下次保存自然丢弃，无需迁移。e2e 断言语义等价替换（`successMarkers` → `successKeywords`；字段断言改 `=== undefined`）。
- **CLI 统一调度走常驻服务 HTTP**：`run-script` / `run-queue` / `cancel` 不再本地直调调度中心，改为向常驻服务 HTTP API 提交并轮询结果（`POST /api/dispatch/{script|queue}`、`POST /api/cancel`、轮询 `GET /api/dispatch/{runId}`），CLI 与服务并发操作配置无锁问题随之消除；服务不可达时自动拉起常驻服务进程（轻量运行模式直接报错）；输出风格贴近原等待逻辑（进度行 + 记录明细 + 退出码=全部 success 才 0）。服务端 `DispatchCenter` 新增已结束执行保留列表（`FindAny`，上限 100 条）+ 运行任务查询 API（`records` 完整返回）。`manage` 交互菜单仍本地直调（不在本版范围）。
- **完成操作 Web 倒计时卡片（可取消）**：队列全部完成后执行 休眠/重启/关机 前，Web 界面（仪表盘/调度中心）显示 60 秒倒计时卡片，可点击取消——重启/关机走 Windows 倒计时（`shutdown /t 60`，取消执行 `shutdown /a`），休眠走应用内 60 秒延迟（取消后不执行）；倒计时为真实墙钟，不随测试时间加速缩放；exit（退出软件）保持立即执行。新增 `POST /api/system-action/cancel` 与 `/api/status` 的 `systemAction` 字段。**测试防护**：`NEXUS_SYSTEM_ACTION_DRYRUN=1` 时系统操作仅记录日志不真正执行（e2e global-setup 设置，CI 绝不真关机）。
- **小加固**：① 静态文件服务路径包含校验（`Path.GetFullPath` 前缀比对，纵深防御防逃逸）；② 历史页天数上限放宽至 `MaxHistoryRetentionDays`（默认 180）并新增前端天数选择器（7/30/180，切换重置分页）；③ `StartScript` 指定 `userName` 时 `TotalTasks=1`（进度口径修正）。
- **专项插件全面数据化（v0.6.3）**：专项插件从 C# DLL 工程改为**纯数据目录形态**（`plugins/<插件名>/plugin.json` 根文件 + `data/`）——推导配置写成 `resolve.json`（`require` 必需文件校验含 `searchUpward` 向上搜索规则 + `paths` 模板占位符 `{var}`/`{rel:var}`，完整保留 March7th 管理端/执行端分离推导）、判断脚本写成独立 `judge.{js,py}` 文件（按扩展名定语言，替代内嵌 C# raw string）、默认配置模板改为 `config-template/` **文件夹**（直接放入设置好的默认配置文件；编辑会话 start 时整体复制到配置位置，复制清单随 `.session` 标记持久化，cancel 与重启恢复按清单精确清理）。宿主新增 `DataSpecializedPlugin` 加载器（`PluginManager` 扫描 `plugins/` 注册，替代 DLL 反射加载）；`ISpecializedScriptPlugin` 契约与 `extensions/` 四个 C# 工程移除，插件契约（`IPlugin`/`INotifyChannel`/`PluginContext`/`ScriptProfile`）收敛为宿主内置 internal（public 仅剩入口与领域模型）；`build.cmd` 移除插件编译段（`plugins/` 整体复制到 `release/plugins/`）。judge-scenarios 的 MaaEnd 判断脚本改从 `plugins/maaend/data/judge.js` 读取；e2e 插件断言（插件列表/probe/固化/新建卡片/编辑模板）数据化后字段与内容一致。开发指南见 `plugins/README.md`。
- **测试**：e2e 新增 2 用例（CLI run-script 经 HTTP 提交与自动拉起、完成操作倒计时卡片取消），全量 51 → **53**、CI 核心集 50 → **52**；数字同步 AGENTS/README。
- 版本号 0.6.3。

## [v0.6.2]（2026-08-13）

### 变更

- **完成判定合并实现**：`SessionJudge` 完成标志模式并入关键字模式（完成标志 ≡ 只有成功组的关键字模式——每个标志一个单元素成功组，行内出现即命中，与关键字行匹配同机制；成功命中等待退出 60 秒语义不变）；判定优先级简化为 判断脚本 → 成功/失败关键字与通用完成标志（同一行匹配机制）→ 进程退出。通用脚本 `successMarkers` 与历史实例兼容不变。
- **专用插件移除冗余完成标志**：BetterGI / March7thAssistant / ZenlessZoneZeroOneDragon 的 `ScriptProfile.SuccessMarkers` 不再提供（判定全部由插件固化判断脚本驱动，判断脚本内部已含结束关键字常量；`ApplyProfile` 固化 `successMarkers` 为空）；e2e 断言同步。
- **测试时间加速钩子（v0.6.2，`NEXUS_TIME_SCALE`）**：宿主新增 `TestHooks`——设置 `NEXUS_TIME_SCALE=60` 时按比例缩放监控循环间隔 / 判断脚本周期触发（30 秒 → 1 秒）/ 成功标志等待退出宽限（60 秒 → 1 秒）/ 日志无更新超时（1 分钟 → 1 秒）/ 运行总时长 / 判断脚本执行超时；判断脚本输入 JSON 增加 `timeScale` 字段（测试判断脚本按它缩放内部墙钟常量）；生产不设置该变量 = 行为零变化。`run-uitest.cmd` 默认加速档（`--realtime` 切真实计时档）；三套测试（e2e 51 + judge 115 + chaos 171）合计从 25+ 分钟降到约 11 分钟；**版本发布前仍跑真实计时档全量**。
- **测试基础设施加固**：三个测试套件的 `setupRuntime` 启动前清理 `uitest/runtime` 目录残留服务进程（防先跑 judge/chaos 再跑 e2e 时端口被残留实例占用、请求打向残留服务）；零日志探针改密集采样 + running 双确认（count 文件存活窗口仅 taskkill 耗时，原 100ms 采样会错过）；调度中心用例改固定时长伪脚本（加速档下瞬时退出使「运行中卡片」窗口小于 dispatch 面板 2 秒轮询）；chaos 卡住/崩溃轮日志写入间隔必须小于加速后 stall（1 秒）否则误判 stall、game-crash 等待循环加心跳行；配置交换崩溃恢复用例去除占位判断脚本（加速档下 1 秒宽限提前 kill 脚本导致「正常结束」假通过，两档现都真实走崩溃现场），断言等待「完整还原」（cfg 落位 + `.session` 清除 + original 清空）容忍孤儿进程 cwd 占用 config 目录的部分还原窗口（真实档暴露的既有时序脆弱性）。
- **文档一致性修正与治理**：判定语义表述统一（README / DESIGN / AGENTS / ARCHITECTURE / extensions-README 移除「插件固化标志」旧语义，明确判定由插件固化判断脚本驱动）；extensions-README 契约表补全 `JudgeScript`/`ConfigTemplate`、March7th 表格主程序更正为 `March7th Launcher.exe`；`AppSettings.AllowRemoteAccess` 注释更正（绑定 `http://+:{port}/`，非 0.0.0.0）；README CLI 补 `-user <用户名>` 参数；judge-scenarios 横幅去除过时版本号；`docs/DESIGN.md` §5 为判定语义唯一权威，`docs/DEVELOPMENT.md` 发布流程新增旧语义关键词检索检查点；judge-scenarios 断言数 116 → 115（swap-crash 断言合并）。
- 版本号 0.6.2。

## [v0.6.1]（2026-08-13）

### 新增

- **MaaEnd 专项插件（第四阶段，`extensions/MaaEndAdapter/`）**：MaaEnd（明日方舟：终末地自动化，MXU 客户端 + agent/go-service + MaaFramework）脚本实例配置接管——`Resolve` 推导主程序 `MaaEnd.exe`、启动参数 `--autostart --quit-after-run`（自启动模式触发自动执行，任务运行完成时进程自动退出）、配置目录 `config/`（`mxu-MaaEnd.json`，MXU 首次启动自动生成，目录型不提供配置模板）、日志 `debug/{YYYY-MM-DD}-*.log`（前端写入，文件名带 `-n` 自增序号、启动时自动清理旧文件，通配取最新修改）。判断脚本（JS/Jint，内嵌 43 任务显示名映射表 + 3 旧名别名，zh-CN）：以最后一个启用任务的「任务完成/失败: <显示名>」判定行收尾（MXU 按 tasks[] 顺序串行执行、失败不中断流程）→ 提取全部「任务失败: X」→ 全部可映射时改写配置（该实例全部 `enabled=false`、失败任务 `enabled=true`，保留其余字段）经 `replaceConfigs` 触发**选择性重试**（MXU 无运行记录机制、无天然选择性补做）；无法识别的失败任务保守不改写。启用任务判定只按 `enabled===true`（与 MXU 运行分发一致，`enabledByController` 仅 UI 缓存）。已知边界：RealTimeTask 永不结束（宿主超时兜底）、跳过任务仍记完成、`--quit-after-run` 未触发执行时不退出（stall 超时兜底）、与手动运行并发互相干扰。
- **March7thAssistant 判断脚本（v0.6.1）**：「游戏终止：StarRail」marker 先判成功路径；扫描任务级失败提示行（每日实训未完成/清体力未完成/模拟宇宙未完成/锄大地未完成/遗器背包已满/领取星琼失败）→ failed（无 replaceConfigs，宿主重试天然选择性补做——时间戳仅在达标时保存）；未出现 marker 时匹配 `/ \| ERROR \| 发生错误/`（main.py 顶层 except 的 ErrorOccurred 模板行）快速失败（跳过 30 秒 stall 等待）；良性噪声（尚未刷新/未开启/截图失败/空错误）不误判。不提供 ConfigTemplate（config.yaml 正常安装必然存在且支持默认值合并）。
- **ZenlessZoneZeroOneDragon 判断脚本（v0.6.1）**：「关闭游戏成功」（after_done=关闭游戏）或「暂停运行」（收尾必现兜底）判定运行结束；提取「指令[ X ] 执行失败 返回状态 Y」去重并过滤良性噪声（等待大世界画面=瞬时重试、通知=SMTP 推送成功）；无失败 → success；有失败 → success + notifyText「本次运行有应用执行失败：X、Y」（应用级失败不中断一条龙，宿主 FinalStatus 因日志含「失败」自动落 partial）。无 ConfigTemplate（config 为 500+ 文件目录，单文件模板机制不适用，目录型误删风险）；配置交换重试调研结论为不需要（`ApplicationRunRecord` 状态 0/1/2/3 按日/周重置驱动「应用已完成」跳过，失败应用重跑自动补做）。

### 修复

- **BetterGI 判断脚本 LogPath（重要 bug）**：`log\better-genshin-impact{YYYYMMDD}.log` → `log\better-genshin-impact.log`（BetterGI 用 Serilog 滚动日志，**当前文件恒为无日期名**、带日期的是午夜归档；原模式指向当日归档（当天不存在）→ 真实运行必因「无日志条目」超时失败，v0.6.0 阶段未暴露）。e2e 两处 LogPath 断言同步。

### 变更

- **BetterGI 判断脚本加固**：失败任务改写配置时清空 `NextTaskId`（BetterGI `OnOneKeyExecute` 若 NextTaskId 非空会从中间任务开始执行，可能跳过被重试的失败任务）；`ConfigTemplate` 替换为真实配置内容（6 个标准任务 GUID，TaskEnabledList 全 false，枫丹/挪德卡莱，CompletionAction=关闭游戏和软件）。
- **测试与文案同步**：e2e 新增 MaaEnd 专项适配用例与选择卡片数量/堆叠断言（51 用例 / CI 50）；judge-scenarios 新增 MaaEnd 专项判断脚本端到端场景（从插件源码提取判断脚本，116 断言：失败任务选择性重试与还原 / 全成功单轮 / 未知失败名保守不改写）；插件页与脚本列表文案核对无硬编码（专用插件均动态渲染）；测试数字与插件名单同步至 AGENTS/README/DEVELOPMENT/CONTRIBUTING/ARCHITECTURE。
- 版本号 0.6.1。

## [v0.6.0]（2026-08-12）

### 新增

- **专用插件判断脚本（第一阶段：BetterGI，插件固化）**：专项脚本实例的判断脚本由插件全权提供（`ScriptProfile.JudgeScript`），后端 `ApplyProfile` 保存时固化（`JudgeScriptEnabled=true`、语言固定 JavaScript），**用户不可编辑**（专项弹窗不渲染自定义完成标志区，关键字仍强制清空；判定走脚本模式时替代插件固化标志）。
- **BetterGI 默认判断脚本**（JS/Jint，基于配置与日志调研）：以「一条龙和配置组任务结束」为运行结束关键字；出现后以 `执行失败/执行异常` 模式提取失败任务（含别名映射「前往冒险家协会领取奖励→领取每日奖励」）→ 读取 OneDragon 配置 → `TaskEnabledList` 仅失败任务开启、其余关闭 → `replaceConfigs` 覆盖配置并自动重试（运行结束自动还原）；无失败任务判成功；无法识别的失败任务保守终止（不误改配置）。
- **专项配置模板（NexusPipeline.json）**：BetterGI 专项 `ConfigPath` 指向 `User\OneDragon\NexusPipeline.json`（不直接使用 BetterGI 自带配置）；`ScriptProfile.ConfigTemplate` 提供最小配置模板（结构键完整、值全空，任务列表/定义为空由用户在编辑时自行添加，不读取可能改名的现有配置文件）；首次编辑用户配置会话时生成到配置位置（cancel 清理，崩溃残留由下次编辑覆盖）。
- **配置交换形态修复**：`PrepareForRun` 仅原配置形态为目录时重建目录（缺失形态不再误建同名目录，修复专项首次编辑时 NexusPipeline.json 被建为目录导致模板写入拒绝访问）；`RestoreKind` 对 Missing 按文件还原（修复二次编辑时文件快照以目录形态落位成「目录/同名文件」残留、配置选项丢失）；`EnsureConfigForEdit` 对误建/残留的同名目录递归清理再写模板（自愈）。e2e 新增专项编辑配置模板用例（47 用例 / CI 46）。
- **编辑会话隐藏机制**：专项脚本 + config 为单文件时，编辑会话 start 将 config 同目录下其他 `*.json` 配置（如 BetterGI 自带「默认配置.json」）暂移入 `data/{脚本Id}/{用户}/edit-hidden`，使 BetterGI 配置列表仅剩 NexusPipeline（自动选中，无需手动切换）；done/cancel 恢复，崩溃残留由下次 start 幂等恢复。
- **编辑会话锁定与恢复**：「配置编辑中」卡片改为锁定弹窗（Esc/遮罩/× 不可关闭，只能完成/取消）；新增 `GET /api/scripts/edit-sessions` 查询进行中会话，用户管理页刷新后自动恢复锁定卡片继续编辑；重启后端时 `.session` 标记（`GeneratedTemplate`）驱动恢复——清理编辑会话生成的配置模板并移回隐藏配置，还原编辑前状态。e2e 新增锁定/刷新恢复/重启恢复用例（48 用例 / CI 47）。
- **配置交换数据目录命名统一**：`data/{脚本Id}/{用户名}/` 下子目录改为语义明确的名称——`config/`→`store/`（内部储存配置快照，与 DESIGN 设计名对齐）、`cache/`→`original/`（原配置暂存，崩溃恢复保底）、`replace-backup/`→`swap-backup/`（配置替换备份）、`edit-hide/`→`edit-hidden/`（编辑会话隐藏配置）；`script/`、`.session` 不变。启动恢复扫描前自动把旧名残留目录迁移到新名（幂等，旧版本崩溃现场仍可完整恢复）；判断脚本输入契约（`files[].Root`、`replaceConfigs`）不受影响。e2e 新增迁移与崩溃现场恢复用例（49 用例 / CI 48）。
- **Missing 形态还原修复**：运行/编辑前配置位置不存在（Missing）时，会话结束后原逻辑「original 空仅清标记不动现场」，导致运行生效的 store 快照**残留在配置位置**（专项 NexusPipeline.json 场景：每次运行后残留并污染下次添加用户快照，真机复现确认）。修复：`DoRestore` 对 Missing 形态删除会话期间在配置位置产生的文件/目录，还原为「不存在」（删除失败保留标记交由自愈/后台重试）。e2e 新增自然结束/运行中取消双变体回归用例（50 用例 / CI 49）。
- **运行收尾顺序增强**：杀脚本进程改为「进程树清理 + 轮询按名强杀直至确认退出」（处理 BetterGI 等「被杀后自重启」的脚本——真机日志曾需强杀两轮才干净），确认进程退出后再按设置处理游戏进程、最后进行配置交换还原，消除文件占用导致的还原失败窗口。
- **脚本/游戏窗口启动前置**：宿主启动脚本主程序与游戏进程后，后台将可见主窗口前置一次（SetForegroundWindow；bat/无窗口进程静默跳过），避免其他界面遮挡导致 BetterGI 等截图识别类脚本运行失败。

### 变更

- 插件版本统一 1.0.0 → **0.1.0**（与项目 Pre-release 阶段对齐，3 个扩展适配器 + 通知推送插件）。
- 版本号 0.6.0。

## [v0.5.4]（2026-08-11）

### 变更

- **复选框全面改为切换按钮**（`.mode-toggle` + `aria-pressed` + `data-action` 委托）：脚本弹窗游戏/通知（启动游戏｜强制关闭｜运行通知）、调度队列（队列通知、星期周期 7 按钮、定时「启用」）、用户弹窗（启用用户/仅首次执行/仅最终完成）、设置页（开机自启/轻量模式/打开浏览器/远程访问）、插件配置页（启用 Webhook/SMTP）；长语义说明移入按钮旁 `muted` 小字；废弃原生复选框样式（check-grid/days-frame）。
- **表单控件高度与按钮对齐统一**：定时列表执行时间框不再被 flex 拉伸（固定 40px，与其余输入框一致）；与输入框同行的按钮统一 40px 高度；定时卡片「启用/删除」64px 等宽、任务列表 ↑/↓/删除 52px 等宽、设置页令牌行改为「输入框 + 短按钮」一行布局（移除内联 `style`）。
- 星期周期按钮排版：桌面/平板 7 个等宽一行，手机（≤600px）4+3 两行，触控目标 ≥40px；e2e 断言同步切换按钮语义。
- **间距与对齐打磨**：切换按钮行（`.toggle-row`/`.toggle-grid`/`.field-btn-row`）与上下内容间距统一 12px（修复用户弹窗填写框与按钮 0px、远程访问按钮与令牌行 0px 的过短/不一致）；按钮右侧解释文字改为与按钮底部对齐（`align-items: flex-end`）；modal 内切换按钮与上方填写框统一 20px；手机（≤600px）表单垂直间距全局统一 12px（form-grid 内字段 4px → 12px，消除同 grid 内与 grid 间间距割裂）。
- **select 下拉增强**：保持原生 `<select>`，弹出面板 `option/optgroup` 背景色跟随主题、选中项 accent 高亮、聚焦边框与输入框一致。
- **响应式细节**：手机侧边栏移除「×」关闭按钮（logo 压缩/文字重叠根治，关闭靠遮罩点击与路由切换）；toast 手机端自适应宽度（`max-content` + 上限 50vw）。
- **提示文字修订**：去歧义与隐私隐患——用户弹窗「填写地址则启用」→「填写则启用」；脚本自启动参数示例采用通用参数写法（不出现具体软件名）；专项脚本根目录示例采用通用路径（YourGame）；访问令牌 placeholder 不提示「未设置」状态（统一「留空=不修改」）；判断脚本超长 placeholder 缩短为一行 JSON 契约摘要，API 说明移入代码框下方常驻 muted 说明。
- **测试范围分层**（AGENTS 约束）：仅前端改动跑 `build.cmd` + e2e 全量 46（免专项）；涉及后端跑 e2e 全量 + judge-scenarios + chaos-queue；发布前一律全量。
- 版本号 0.5.4。

## [v0.5.3]（2026-08-11）

### 变更

- **历史与日志落盘精简**：每次运行仅保留 `.json` 纯运行状态（移除尝试详情的日志尾部快照 LogTail/OutputTail/OutputFile）+ **按尝试分批**的 `.log` 脚本日志文件（`HH-mm-ss-{尝试号}.log`，重试失败按尝试标号，排查清晰）；**废弃 `.console.log`**（控制台输出仅保留运行中实时显示，不再落盘）与 **`runs-*.jsonl` 索引**（历史查询直接扫描 `.json` 目录）；运行会话移除控制台全文缓存（内存占用下降）。
- 历史详情（Web/CLI）：按尝试分批展示脚本日志；旧数据兼容（无按尝试文件的旧记录回退读取旧 `.log`）。
- e2e「历史文件集」用例重写为两件套 + 按尝试分批标号断言；版本号 0.5.3。

## [v0.5.2]（2026-08-11）

### 修复

- **BUG #1：多用户运行结束后 config 现场被配置替换备份污染**——`RunSession.RunAsync` 收尾顺序调整：先还原配置替换（replace-backup → config）并清空判断脚本目录，再执行配置交换还原（cache → config 恢复运行前现场），避免替换还原覆盖交换还原的现场。
- **BUG #2：重试轮（attempt≥2）日志监控失效与残留误判**——三层根因根治：
  - **日志文件替换检测改文件身份（FileId）**：`LogMonitor` 新增 `GetFileInformationByHandle` 对比卷序列号+文件索引（`FileReplaced`），脚本 move 归档旧日志后重建新文件时可靠检测并重新从头读取（原创建时间检测在新旧文件 CreationTime 相同时失效，导致监控句柄指向被改名的旧文件、ReadNew 恒空）；
  - **初始监控严格 fresh 判定**：仅当文件在本次尝试开始后写过（`LastWriteTime ≥ attemptStart`，无松弛窗口）才从头读，否则末尾读忽略残留——上一尝试残留不再被误判为本次新文件而重读进判定；
  - **判断脚本输入按尝试切片**：输入日志取本次尝试日志段（上次尝试的失败/成功行不再跨尝试污染判定），同时消除"部分日志批次提前触发"竞态。
- 混沌测试 bat 恢复 move 归档（真实覆盖日志重建场景）；新增混沌调度队列压力测试 `uitest/chaos-queue.mjs`（171 断言，固定/随机种子轮、多用户配置交换、五种干扰判定、崩溃注入、通知双模式、无残留）。

### 文档

- 新增 `docs/DESIGN.md`：核心设计理念（本地优先/配置交换/判定策略/日志监控/自愈）与核心运行流程分步说明（含 mermaid 图）。
- 新增 `CHANGELOG.md`（本文件）。
- 更新 `docs/ARCHITECTURE.md`（判断脚本执行器归位后端表、LogMonitor 职责、v0.5.1/v0.5.2 变更记录）、`README.md`（判断脚本输入口径、游戏语义、测试命令与断言数、文件结构）、`docs/DEVELOPMENT.md` 与 `CONTRIBUTING.md`（测试命令迁移 @playwright/test 与专项测试）。
- 版本号 0.5.2。

## [v0.5.1]（2026-08-11）

### 新增

- 插件级配置：`PluginContext.GetConfig<T>/SetConfig<T>` 落盘 `config/plugins/<插件名>.json`（PascalCase、原子写入）；`GetSecret/SetSecret` 密钥 DPAPI 加密（`enc:` 前缀，与普通配置同文件）；PluginManager 初始化注入插件名；NotifyPlugin 保持 AppSettings 配置不动（行为零变化）。

### 变更

- e2e 迁移 @playwright/test：`tests/` 按域 7 文件 46 用例（CI 核心集 45），移除旧 `test.mjs` 与 EXPECTED 计数机制；`run-uitest`/CI 改 `npx playwright test` + `NEXUS_CI=1`；global-setup/teardown PID 文件跨进程管理服务。
- 前端 `core/limits.js` 归位 `views/limits.js`；`extensions/README.md` 插件开发指南；`.gitignore` 增补 test-results/playwright-report。

## [v0.5.0]（2026-08-11）

### 变更

- 架构重构：核心域按子域重组 `Models/Services/Persistence/Utilities` 命名空间；壳式 DI（`RuntimeContext` 内建 `ServiceProvider` + `Resolve<T>()` 服务出口，插件 `PluginContext` 可解析宿主服务）；Web 特性路由 `[ApiRoute]` 反射扫描注册（新增 API 无需改路由表）；`ApiSettings` 约定自动绑定（camelCase↔PascalCase、密钥白名单与 DPAPI、historyRetentionDays 400 校验保留）。
- `RunSession` 判定策略拆出 `SessionJudge` 状态机；`UserConfigManager` 拆门面 + 原语/会话恢复/数据目录三层（`ConfigSwapPrimitives/Session/Paths`）。
- extensions 插件工程对齐；修复反射路由 cancel 适配与 e2e 历史页时序竞态；e2e 480 断言。

## [v0.4.4]（2026-08-11）

### 新增

- 远程访问（可选）：访问令牌（DPAPI 加密）、http.sys 强通配符绑定、自动防火墙入站规则、局域网地址提示；修复 401 状态码被默认参数覆盖与端口占用复用损坏 HttpListener 闪退。

### 变更

- 多通道通知并存；控制台输出按次完整保存三件套、每日清理；管理器日志命名统一；保留天数默认 7 上限 180；e2e 479 断言。

## [v0.4.3]（2026-08-11）

### 变更

- 运行总时长语义：`TotalTimeoutMinutes` 改为整个运行（含全部重试与前置/后置脚本）计时，前置/后置脚本按剩余时长；`PrepareForRun` 标记先行消除崩溃窗口，恢复失败后台延迟重试自动还原（孤儿进程退出后补还原）。
- 判断脚本输入日志 4MB 有界截断并置 `logTruncated`；专项测试新增配置交换崩溃恢复与零日志探测竞态修复（judge-scenarios 99 断言）。

## [v0.4.2]（2026-08-11）

### 修复

- 单文件配置替换（B-6）：`replaceConfigs` 项等于文件名时替换生效并还原；空 config 目录用户交换后目录消失修复。
- 运行前置：脚本必须至少一个启用用户，手动运行拒绝、队列跳过记录 failed 历史。
- 场景 D 崩溃链路：进程退出未找到日志文件路径补最终触发。

## [v0.4.1]（2026-08-11）

### 修复

- v0.4.0 自定义完成标志链路缺陷：配置替换新文件残留、路径前缀逃逸、成功判定后重复触发、通知文本被用户脚本覆盖、日志超时无最终触发、API 空判断脚本校验。

## [v0.4.0]（2026-08-11）

### 新增

- 自定义完成标志大更新：成功/失败关键字（行内 AND、行间 OR，失败立即终止，成功等待退出 60 秒）；判断脚本（JS 内置 Jint 引擎 / Python 系统解释器，config 只读 + script 目录可读写边界与防逃逸；日志批次/阻塞周期 30 秒/进程退出最终触发）；插队替换配置 `replaceConfigs` 失败后自动重试并在运行结束还原（含崩溃恢复）；`notifyText` 替换通知正文；专用插件脚本强制禁用自定义字段；前端关键字/脚本切换按钮模式与上传脚本；e2e 466/443。

## [v0.3.6]（2026-08-11）

### 变更

- 前端布局 Notion 风格化：设计令牌收敛（小圆角/轻阴影/实体面板、浅色米色系、深色保留品牌蓝）、去渐变去玻璃态、统一紧凑列表行、粒子弱化、移动端触控目标 40px。
- 插件配置页密钥并入保存设置（Webhook 地址/签名密钥同行、地址改 text、移除重复保存按钮）；前端约束文档同步（AGENTS.md Notion 风格基线/展示模式/密钥字段语义）。

## [v0.3.5]（2026-08-11）

### 新增

- 脚本用户上移/下移排序（顺序 API + 执行顺序保证）；定时列表完全一致合并与间隔 <10 分钟确认卡片；删除脚本/队列改用确认卡片；前端路径首尾引号去除。

### 变更

- 游戏路径必填解绑：任务失败无条件强制结束游戏进程，成功按「强制关闭」设置；版本号同步 0.3.5。

## [v0.3.4]（2026-08-11）

### 新增

- 脚本图标自动取主程序 PE 资源最高分辨率（含 256×256，无资源回退关联图标）；脚本路径存在性合规校验（Web/API/CLI 统一）；游戏配置卡弹窗内常驻（勾选启动游戏后路径必填且为可执行文件）。

### 修复

- e2e 占位脚本改用唯一命名（规避 `IsExeRunning` 按进程名误报导致调度中心执行被拒）。

## [v0.3.3]（2026-08-11）

### 新增

- ZenlessZoneZeroOneDragon 专用插件；完成标志重构（内置关键词取消、各插件固化自有关键词、通用脚本按进程退出判定）。

### 变更

- 强制管理员运行（非管理员启动拒绝并退出、移除脚本 740 runas 降级提权）；前端卡片 1/2 布局与长文本滚动、脚本卡片徽章改为插件提供的中文游戏名（`GameName`，主程序不写死）；e2e 新增 `--ci` 核心回归集。

## [v0.3.2]（2026-08-11）

### 新增

- 管理员提权（manifest requireAdministrator + 计划任务自启）；脚本启动参数显式路径启动目标与自动提权（740 runas）；March7thAssistant 专用插件。

## [v0.3.1]（2026-08-11）

### 变更

- 进程检测语义重做（未填游戏路径不检测、填路径确认脚本游戏双启动、自重启进程宽容判定与清理、跑完强制结束）；执行前检查（手动禁止/自动跳过并记录）；强制关闭游戏依赖启动游戏（前端联动+后端归一化）；配置 JSON 中文原字符输出；历史列表移除结果文字。

## [v0.3.0]（2026-08-11）

### 新增

- 插件分离：通用/专用契约（`IPlugin`/`ISpecializedScriptPlugin`）、BetterGI 专用插件、外部插件默认启用；日志路径格式（日期占位符/通配严格匹配、无条目超时、忽略已有日志、轮换跟踪）；脚本实例图标提取与卡片化；队列卡片同构与下次触发倒计时；新建实例选择卡片。

## [v0.2.3]（2026-08-11）

### 新增

- 用户数据保留与迁移；脚本已打开检测；队列多用户依次运行；路径引号清洗；队列不运行模式；协作规范以 v1.0.0 为界。

## [v0.2.2]（2026-08-11）

### 变更

- 通知复选框随插件显隐、generic 模板联动与样式统一；e2e 重构（`--quick` 模式）。

## [v0.2.1]（2026-08-11）

### 新增

- 约束/分页/告警/FATAL e2e 用例集（98 项）；前端分页组件与约束（pager、达上限禁用、警告卡片）；约束体系（`config/limits.json` 三层校验、Web/CLI 统一落点、FATAL 全模式拒启）；存储优化（WriteAtomic 原子替换、历史 jsonl 顺序索引、历史 API 服务端分页）。

## [v0.2.0]（2026-08-11）

### 变更

- 前端模块化（views 按域 7 文件、action 注册表、core 细分 dom/format/forms/modal）；后端分层解耦（Web/Cli/Plugins 命名空间、Handler 拆分、插件契约 `INotifyChannel`、DataStore/Bootstrap）；日志级别体系（DEBUG/INFO/WARN/ERROR/FATAL，阈值过滤与控制台着色）；架构文档与 AGENTS 模块地图更新。

## [v0.1.0]（2026-08-11）

### 新增

- 初始提交（枢链）：脚本实例/用户/队列管理、托盘服务、Web 管理界面、调度与通知的基础能力。

[v0.6.0]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.6.0
[v0.5.4]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.5.4
[v0.5.3]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.5.3
[v0.5.2]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.5.2
[v0.5.1]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.5.1
[v0.5.0]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.5.0
[v0.4.4]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.4.4
[v0.4.3]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.4.3
[v0.4.2]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.4.2
[v0.4.1]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.4.1
[v0.4.0]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.4.0
[v0.3.6]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.3.6
[v0.3.5]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.3.5
[v0.3.4]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.3.4
[v0.3.3]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.3.3
[v0.3.2]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.3.2
[v0.3.1]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.3.1
[v0.3.0]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.3.0
[v0.2.3]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.2.3
[v0.2.2]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.2.2
[v0.2.1]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.2.1
[v0.2.0]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.2.0
[v0.1.1]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.1.1
[v0.1.0]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.1.0

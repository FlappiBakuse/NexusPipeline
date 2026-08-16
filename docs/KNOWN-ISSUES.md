# 已知问题台账（Known Issues）

**建立日期**：2026-08-15（v0.6.9 全面代码评估产出）｜ **状态**：登记中，按版本分步修复（用户决策：全部记录，按版本分开修复）｜ **v0.6.10 交付**：文档不一致项（KN-48/49/50 涉及项）已随文档体系重组修正 ｜ **v0.7.4 交付**：稳定复现无严重副作用项批量修复（CLI 删除清理/判定竞态/大小写一致/死代码全量清理），台账过时表述修正（KN-14/KN-46/KN-47）

> 本台账登记项目已确认的已知问题（潜在 BUG / 死代码 / 文档不一致 / 约束违规），不包含已修复项（见 [CHANGELOG.md](../CHANGELOG.md)）。修复版本排期建议见 [ROADMAP.md](ROADMAP.md)；代码定位指引见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 分级说明

- **高**：数据丢失 / 配置损坏 / 重复执行 / 资源泄漏风险，建议近期版本优先修复；
- **中**：逻辑瑕疵 / 边界缺陷 / 一致性违规，按版本节奏修复；
- **低**：死代码 / 文档过时 / 样式与自约束相悖，随版顺手清理。

## 高优先级

| 编号 | 问题 | 位置 | 建议版本 |
|---|---|---|---|
| KN-01 | **损坏配置被静默覆盖（数据丢失）**：scripts/queues.json 解析失败仅 Warn 并返回空列表，用户任意一次保存即整体覆盖损坏文件，原数据不可恢复 | `src/Persistence/JsonStore.cs:82-85`、`src/Persistence/ConfigStore.cs:30-33` | ✅ v0.7.2 已修复（解析失败原文件改名保留 `*.corrupt-时间戳`，不再被覆盖） |
| KN-02 | **POST 可注入已存在 Id 造成重复记录**：客户端提交已存在 Id 时保留（仅空/不存在才重新生成），集合出现两条同 Id 记录 | `src/Web/ApiScriptsHandler.cs:109-112`、`src/Web/ApiQueuesHandler.cs:55-58` | ✅ v0.7.1 已修复（新建一律重新生成 Id） |
| KN-03 | **队列重复触发**：`Register` 仅对 script 查重，手动 + 定时并发触发同一队列会双跑（重复历史/通知/系统操作，如双关机命令）；Scheduler 的 `_runningQueueIds` 挡不住手动入口 | `src/Services/DispatchCenter.cs:292-305` | ✅ v0.7.2 已修复（Register 对队列对称查重，手动/定时统一拒绝） |
| KN-04 | **共享集合无锁并发**：`RuntimeContext.Scripts/Queues` 与 `RunningExecution.Records` 被 Web 请求线程与后台线程并发读写，远程模式下可抛 `ArgumentOutOfRangeException`/`InvalidOperationException: 集合已修改`（前端轮询 500） | `src/RuntimeContext.cs:28-30`、`src/Services/DispatchCenter.cs:37`、`src/Web/ApiStatusHandler.cs:41-58` | ✅ v0.7.2 已修复（DataLock + Records 锁 + 深拷贝快照） |
| KN-05 | **CLI 删除脚本/队列不清理**：删除仅移除列表并保存，不清理 `data/{脚本Id}` 目录、不释放 ScriptConfigGate/Mutex、不检查运行状态（Web 端有完整清理，行为不一致，静态字典泄漏） | `src/Cli/ScriptsMenu.cs:70-78`、`src/Cli/QueuesMenu.cs:77-85` | ✅ v0.7.4 已修复（脚本删除对齐 Web 端：运行中拒绝 + 清理 data 目录 + 释放门禁/互斥体；队列删除增加运行中拒绝） |
| KN-06 | **远程访问下脚本图标全部 401**：`<img src="/api/scripts/{id}/icon">` 无法携带 `Authorization: Bearer` 头，远程模式图标请求必失败（有占位图兜底不崩溃，功能失效） | `wwwroot/views/scripts.js:57`、`wwwroot/views/queues.js:83`、`src/Web/WebServer.cs:197-203` | v0.7.5（需设计 token 传递方案，涉及 icon 断言同步） |

## 中优先级

| 编号 | 问题 | 位置 | 建议版本 |
|---|---|---|---|
| KN-07 | **resolve.json 多占位符静默丢弃**：`ResolveArgs/ResolvePath` 命中第一个占位符即 return，`{launcher} --config {assistant}` 之类的模板会丢掉后续全部内容（文档已声明「仅整体替换」，但应显式校验或报错而非静默丢弃） | `src/Plugins/DataSpecializedPlugin.cs:219-262` | ✅ v0.7.4 已修复（多占位符模板显式校验并整体推导失败，Warn 可观测，不再静默截断） |
| KN-08 | **bat 游戏启动器等待不随测试加速缩放**：`WaitForGameProcessAsync` 对 bat 用真实 `Task.Delay(timeout)`，加速档（scale=10）下白等 GameWaitSeconds 真实秒数 | `src/Services/RunSession.cs:808-828` | v0.7.5 |
| KN-09 | **日志截断后立即写入的内容漏判窗口**：`ReadNew` 长度检查在 `Length < position` 时把 position 置为新尾——若截断后、下次读取前已写入新内容，截断后新写内容不进入判定输入（失败关键字可能漏判） | `src/Services/LogMonitor.cs:138-143` | 随版（两难问题：补漏判需知截断点，文件系统不提供；改从头读复活旧行重复污染，实际影响小于已修问题，建议文档化保留） |
| KN-10 | **明文旧密钥直接回显**：settings.json 中未加密（旧版明文或手工编辑）的 Webhook 地址/密钥在 GET /api/settings 时明文完整返回，违反「已设置的密钥不回显明文」契约 | `src/Web/ApiSettingsHandler.cs:279-294` | ✅ v0.7.2 已修复（非空一律占位符，判定统一） |
| KN-11 | **Python 判断脚本尾行 JSON 丢失竞态**：`BeginOutputReadLine` + `WaitForExitAsync` 时进程退出瞬间异步输出事件可能未投递完，契约规定的 stdout 尾行 JSON 有丢失风险（误判「无合法输出」） | `src/Services/JudgeScriptRunner.cs:403-420` | ✅ v0.7.4 已修复（退出后补同步 `WaitForExit()` 排空输出缓冲） |
| KN-12 | **modal 焦点陷阱空白点击后失效**：点击弹窗内非焦点区域后 `activeElement` 落到 body，Tab 焦点逃逸到弹窗外（locked 弹窗同样受影响）；建议 mask 补 focusout 兜底 | `wwwroot/core/modal.js:45-64` | ✅ v0.7.3 已修复（mask focusout 兜底拉回） |
| KN-13 | **limits 警告层无障碍缺失**：`role="alertdialog"` 遮罩无 `aria-labelledby`、无初始焦点、无焦点陷阱、无焦点恢复（不走 modal 组件） | `wwwroot/views/limits.js:32-48` | ✅ v0.7.3 已修复（四件套补齐 + Esc 关闭） |
| KN-14 | **触控目标 < 40px**：基础按钮 38px、`.sm` 按钮 32px（≤600px 才升 40px）、侧栏主题按钮 32px、drag-handle 36px，违反「触控目标不得小于 40px」约束 | `wwwroot/style.css:119/180/186/192/255/391` | ✅ v0.7.3 修复 .sm/drag-handle/侧栏按钮；**v0.7.4 补修基础 button 38px→40px**（台账 v0.7.3 时表述过时，未含此缺口） |
| KN-15 | **style.css 硬编码色值 + uppercase eyebrow**：`.badge.*` rgba 背景、`.field-error` box-shadow、`color:#fff` 未走 CSS 变量；`.nav-caption` 与 `.eyebrow` 大写英文小字违反 Notion 基线 | `wwwroot/style.css:111/129/186/191/220/242-245`、`wwwroot/index.html:36` | ✅ v0.7.3 已修复（badge 背景/field-error 环改 CSS 变量；uppercase 移除、8 处英文 eyebrow 中文化） |
| KN-16 | **dispatch 面板轮询整块替换打断交互**：2 秒轮询整块替换运行面板 innerHTML，打断用户滚动/选中日志文本（dashboard 已改局部更新，dispatch 未同步） | `wwwroot/views/dispatch.js:24-31` | ✅ v0.7.3 已修复（按 runId 增删改局部更新 + 贴底才自动滚 + 标题 aria-live） |

## 低优先级（死代码 / 冗余 / 文档）

| 编号 | 问题 | 位置 | 状态 |
|---|---|---|---|
| KN-17 | `SendStrategy` 配置字段全链路无效（死配置，v0.4.x 遗留；CLI 只切 Webhook/SMTP 开关） | `src/Models/AppSettings.cs:25` | ✅ v0.7.4 已修复（字段/Normalize/回显/菜单名同步删除，旧配置残留自动忽略） |
| KN-18 | `Audit.Cli` 常量从未使用（CLI 菜单全部用 `Audit.Manage`） | `src/Services/Audit.cs:10` | ✅ v0.7.4 已修复（删除常量） |
| KN-19 | `JudgeScriptRunner.IsSupportedLanguage` 无调用方 | `src/Services/JudgeScriptRunner.cs:57-60` | ✅ v0.7.4 已修复（删除方法） |
| KN-20 | `LogLevel.ToSetting` 扩展方法无调用方 | `src/Utilities/LogLevel.cs:14-25` | ✅ v0.7.4 已修复（删除方法） |
| KN-21 | `PluginManager.SpecializedPlugins` 属性无调用方 | `src/Plugins/PluginManager.cs:28-29` | ✅ v0.7.4 已修复（删除属性） |
| KN-22 | `DispatchCenter.RunScriptAsync` 的 `runUsers.Add(null)` 兜底不可达（StartScript 已保证有启用用户） | `src/Services/DispatchCenter.cs:400-404` | ✅ v0.7.4 已修复（删除不可达兜底） |
| KN-23 | `PluginManager.NotifyChannels` 中 `OfType<IPlugin>()` 恒真空操作 | `src/Plugins/PluginManager.cs:22` | ✅ v0.7.4 已修复（删除恒真过滤） |
| KN-24 | `SetEnabled` 对内置插件同时写 `DisabledPlugins`（冗余写入，`IsEnabled` 只查 `EnabledPlugins`）；传入不存在的插件名也静默写入 | `src/Plugins/PluginManager.cs:213-220` | ✅ v0.7.4 已修复（插件不存在显式拒绝；DisabledPlugins 写入保留——实为 `ConfigStore.Normalize`「旧配置补默认内置插件」判据，非纯冗余） |
| KN-25 | `RunSession` 尝试日志段首尾不对称（段含「结束」头不含「开始」头） | `src/Services/RunSession.cs:227-229` | ✅ v0.7.4 已修复（段起点移至「开始」头之前，首尾对称；归档日志首行为「开始」头，chaos 归档兜底断言同步兼容） |
| KN-26 | `WebhookSender.Types` 与 `ConfigStore.Normalize` 的 WebhookType 白名单双份维护 | `src/Services/WebhookSender.cs:14`、`src/Persistence/ConfigStore.cs:68-71` | ✅ v0.7.4 已修复（白名单单源化至 `AppSettings.WebhookTypes`） |
| KN-27 | icon 响应未走 `HttpHelper.ServeFile`，漏 CSP 等安全头 | `src/Web/ApiScriptsHandler.cs`（icon 分支） | v0.7.5 |
| KN-28 | `KillTree` 排除游戏名进程时日志文案误导（「PID 已不存在」实为被排除） | `src/Utilities/SystemActions.cs:188-191` | ✅ v0.7.4 已修复（文案区分「被排除/无子进程」） |
| KN-29 | `SessionJudge` 三个嵌套 enum 显式 public（外层 internal，实际无暴露风险，但违背「其余一律 internal」声明） | `src/Services/SessionJudge.cs:13/21/29` | ✅ v0.7.4 已修复（改 internal） |
| KN-30 | `ApiSettingsHandler` PUT 同一请求双次 Save + 双次 Normalize/RefreshLevel | `src/Web/ApiSettingsHandler.cs:80-85` | ✅ v0.7.4 已修复（allowRemoteAccess 冗余二次绑定/保存删除，单次 Save） |
| KN-31 | `AuthFails` 静态字典按远端 IP 累积永不清理（远程模式下内存缓慢增长） | `src/Web/WebServer.cs:33` | v0.7.5 |
| KN-32 | 请求体空键 `""` 时 `field[0]` 抛 IndexOutOfRange（应为 400）——✅ v0.7.2 已修复（空键显式 400「请求体包含空字段名」） | `src/Web/ApiSettingsHandler.cs:247` | ✅ v0.7.2 已修复 |
| KN-33 | Webhook 成功判定只看 body `code==0` 忽略 HTTP 状态码 | `src/Services/WebhookSender.cs:85-87` | v0.7.5（钉钉/飞书真机验证待决） |
| KN-34 | `JsonLiteral` 手写转义未处理 `\b`/`\f` 等控制字符 | `src/Services/WebhookSender.cs:166-175` | ✅ v0.7.4 已修复（改 System.Text.Json 序列化，`UnsafeRelaxedJsonEscaping` 保留中文原样、控制字符正确转义） |
| KN-35 | 历史详情固定查 31 天窗口与列表保留天数（上限 365）不一致，180 天前记录点详情 404 | `src/Web/ApiHistoryHandler.cs:129`、`src/Services/HistoryService.cs` | v0.7.5 |
| KN-36 | `RemoveMutex` 与 `WithSwapLock` 并发时 `WaitOne` 抛 ObjectDisposedException（无 catch，KN-05 同源分支）——✅ v0.7.2 已修复（捕获后移除条目重建互斥体重试） | `src/Services/ConfigSwapPrimitives.cs:86-104` | ✅ v0.7.2 已修复 |
| KN-37 | 用户改名/删除大小写敏感（`==`）与 users/order 的 OrdinalIgnoreCase 不一致 | `src/Web/ApiScriptsHandler.cs:544` | ✅ v0.7.4 已修复（查询/校验/删除统一 OrdinalIgnoreCase） |
| KN-38 | `else if (!queue.NotifyEnabled)` 冗余非运算 | `src/Services/DispatchCenter.cs:574-582` | ✅ v0.7.4 已修复（改 else） |
| KN-39 | Python 解释器多安装时 `candidates[0]` 选中不确定（Directory.GetFiles 顺序未定义） | `src/Services/JudgeScriptRunner.cs:365` | v0.7.5 |
| KN-40 | `/api/plugins/{name}/enable` 之外任意字符串都按 disable 处理（应校验 enable/disable） | `src/Web/ApiPluginsHandler.cs:17` | ✅ v0.7.4 已修复（显式白名单，其余 400） |
| KN-41 | `PromptEdit`/`PromptEditMasked` 高度重复可合并 | `src/Cli/Ui.cs:29-103` | ✅ v0.7.4 已修复（合并为 `PromptEditCore(label, masked)`） |
| KN-42 | 编辑会话 start 的 catch 把 `ex.Message` 直接回给客户端；`keepGate=true` 后写响应异常不释放 gate——✅ v0.7.2 已修复（keepGate 后异常主动清理现场并释放门禁） | `src/Web/ApiScriptsHandler.cs:811-814` | ✅ v0.7.2 已修复 |
| KN-43 | 前端死代码：`core/format.js` 的 `dayDesc/actionLabel`、`core/pager.js` 的 `unregisterPager`、`views/scripts.js` 的 `FALLBACK_ICON` 冗余别名 | `wwwroot/core/format.js:25-35`、`wwwroot/core/pager.js:18-20`、`wwwroot/views/scripts.js:15` | ✅ v0.7.4 已修复（全部删除，scripts/queues 直接引用 `scriptFallbackIcon`） |
| KN-44 | 前端 `select` 的 click 与 change 双触发（当前调用点均幂等，属隐患模式）；`selectField` option 未走 `esc()` | `wwwroot/app.js:59-67`、`wwwroot/core/forms.js:20` | ✅ v0.7.4 已修复（click 委托跳过 select/option，change 唯一分发；option value/文本转义） |
| KN-45 | `ui.js` localStorage 写入无异常保护（隐私模式/禁用存储下白屏） | `wwwroot/core/ui.js:183` | ✅ v0.7.4 已修复（主题/token/重启轮询的 localStorage 读写全量 try/catch 兜底） |
| KN-46 | 队列任务用户数估算与后端校验口径不一致（先加任务后选脚本时估算偏低） | `wwwroot/views/queues.js:168-188` | ✅ v0.7.4 核验：前端 `queueTotalUsers` 与后端 `Limits.QueueTotalUsers` 逐行对应（未选脚本均计 1、启用用户 `Math.max(1,…)`），保存前空任务已过滤——两端口径一致，台账表述过时 |
| KN-47 | 设置页双「保存设置」按钮（语义重复）；侧栏「本地服务 · 127.0.0.1」硬编码文案远程模式不准确 | `wwwroot/views/settings.js:22`、`wwwroot/index.html:30` | ✅ v0.7.3 自动保存已移除双按钮（台账过时，仅剩硬编码文案）；✅ v0.7.4 侧栏地址动态化（`/api/status.actualPort` + 当前 hostname） |
| KN-51 | 托盘「打开管理页面」用 `Settings.WebPort` 而非实际监听端口——设置页改端口未重启服务时打开新端口 404（`WebServer.Port` 实际值未被引用） | `src/TrayApp.cs:52`、`src/Web/WebServer.cs:87` | ✅ v0.7.1 已修复（`WebServer.Current.Port` 优先，回退 Settings.WebPort） |
| KN-52 | CLI 设置菜单历史保留天数仅校验 `>= 1`（越界输入被 `ConfigStore.Normalize` 静默重置为 7），与 Web 端 `Limits.CheckRetentionDays` 校验不一致 | `src/Cli/SettingsMenu.cs:69`、`src/Persistence/ConfigStore.cs:52-55` | ✅ v0.7.1 已修复（上限校验 + 非法输入提示） |
| KN-53 | CLI 脚本菜单未做超时 -1 成对校验（stall=-1 而 total 正常值可通过），Web 端会拒绝——两入口行为不一致 | `src/Cli/ScriptsMenu.cs:149-158` | ✅ 已修复（v0.7.1 核验：保存前已调用 `Limits.CheckScriptTimeouts` 含成对校验，台账过时） |
| KN-48 | DESIGN.md 过时：截断表格（P8 部分截断尾续读）、fresh 判定（LastWriteTime → 长度快照）、保留上限 180（→ limits.json 动态，默认 180/上限 365） | `docs/DESIGN.md:91/222/226/261` |
| KN-49 | ARCHITECTURE.md 依赖方向描述与实现不符：Services→Plugins（UserConfigManager/DispatchCenter）、Plugins→Services（PluginManager/NotifyPlugin）、Logger→RuntimeContext | `docs/ARCHITECTURE.md:40-43`、`src/Services/UserConfigManager.cs:4`、`src/Plugins/PluginManager.cs:3`、`src/Utilities/Logger.cs:33` |
| KN-50 | 文档断言数字/表述残留：DEVELOPMENT.md 旧版 chaos「171」、AGENTS/README 数字同步检查 | `docs/DEVELOPMENT.md`（2026-08-15 重构时修正） |
| KN-54 | 队列完成操作在任务失败（非取消）时仍执行 exit/sleep/reboot/shutdown——语义已与用户确认保留，待文档化说明 | `src/Services/DispatchCenter.cs:573-618` | ✅ v0.7.1 已文档化（README + DESIGN，语义保留） |
| KN-55 | `.session` 标记与 swap-backup `.meta` 用 camelCase 序列化，与「磁盘 JSON = PascalCase」约定相悖（内部瞬态文件，风险低） | `src/Services/ConfigSwapSession.cs:31-35/165` | v0.7.x（修复需兼容旧文件读取，收益低） |
| KN-56 | CLI `Ui.PromptEdit`/`PromptEditMasked` 用 `Console.ReadKey`，stdin 重定向（管道/自动化）时抛 `InvalidOperationException` 未处理异常直接崩溃（2026-08-15 KN-52 复现时发现） | `src/Cli/Ui.cs:29-103` | ✅ v0.7.1 已修复（重定向下降级 `ReadLine`：空行=不变） |
| KN-58 | 表单校验错误仅 toast+高亮，无内联错误文字、无 `aria-invalid`、无修复指引 | `wwwroot/views/scripts.js:369-442`、`users.js:115`、`queues.js:223-235` | ✅ v0.7.3 已修复（`setFieldError`/`clearFieldError`：内联文字 role=alert + aria-invalid + 聚焦） |
| KN-59 | 提交按钮无请求中忙碌态——保存/执行/删除请求期间不禁用、无 spinner，重复点击可双提交 | `wwwroot/core/api.js`（无状态钩子） | ✅ v0.7.3 已修复（`withBusy`：请求期间禁用 + spinner，7 个视图提交类 actions 全覆盖） |
| KN-60 | 拖拽排序无键盘替代——脚本/队列/用户/弹窗定时/弹窗任务 5 处列表对键盘用户不可用 | `wwwroot/core/dnd.js:16-77` | ✅ v0.7.3 已修复（drag-handle 可聚焦 + ↑/↓ 键控重排 + focus-visible 环） |
| KN-61 | 路由切换焦点不重置——`#view` 已备 `tabindex=-1` 但从未 focus，键盘用户切页后焦点留在 body | `wwwroot/app.js:42-57` | ✅ v0.7.3 已修复（`render()` 统一 `view.focus({ preventScroll: true })`） |
| KN-62 | 分页/菜单/表格 ARIA 缺失：pager 当前页无 `aria-current`；菜单按钮无 `aria-expanded/aria-controls`；表格 `th` 无 `scope`；`nav-backdrop` 为可点击 div | `wwwroot/core/pager.js:9`、`index.html:32-35`、`views/dashboard.js:9` 等 | ✅ v0.7.3 已修复（aria-current + aria-expanded/controls + th scope + backdrop 改 button + panel-toggle aria-controls） |
| KN-63 | 交互态 hover 反馈缺失：`.list-item`（调度运行行/任务行）、`.plugin-card`、`.timeset-card` 无 hover 背景（Notion 基线要求）；plugin-card 描述行超长可溢出 | `wwwroot/style.css:232/309` | ✅ v0.7.3 已修复（hover 背景补齐 + p-ver line-clamp 2） |
| KN-64 | 队列任务下拉 `<select data-task-idx>` 无 label/aria-label（全库唯一裸控件） | `wwwroot/views/queues.js:165` | ✅ v0.7.3 已修复（aria-label「第 N 个任务：脚本实例」） |

> 注：`docs/DEVELOPMENT.md`、`docs/ASSESSMENT.md` 已于 2026-08-15 文档体系重组中重构/更新（KN-48/KN-49/KN-50 涉及项在重组时一并修正，台账状态以实际为准）。

## v0.7.4 台账外新增登记（2026-08-16 全面评估新发现，已随 v0.7.4 修复）

| 编号 | 问题 | 位置 | 状态 |
|---|---|---|---|
| KN-65 | **`EnsureConfigForEdit` 误删目录型模板配置**：目录型 ConfigPath（如 MaaEnd `config\`）第二次编辑会话时，`PrepareForRun` 刚从 store 还原的用户配置目录被当「误建残留」递归删除并改用默认模板覆盖——用户上次编辑成果从编辑面消失，提交则 store 被模板覆盖（数据损失） | `src/Services/UserConfigManager.cs:229-241` | ✅ v0.7.4 已修复（目录型 ConfigPath 的目录为合法形态：非空=已有配置跳过模板生成，空=复制模板兜底；文件型误建目录保留原清理语义） |
| KN-66 | **pending 系统操作叠加双执行**：60 秒窗口内两个队列先后完成时新 pending 覆盖旧 pending，但旧 sleep 的后台 `Task.Delay` 未取消，到期仍执行休眠（真实系统副作用）；旧 Cts 也不再可从 UI 取消 | `src/Services/DispatchCenter.cs:369-417` | ✅ v0.7.4 已修复（覆盖前先取消旧 pending 的 Cts；`ClearPendingSystemAction` 引用校验已防误清新操作） |
| KN-67 | **尝试日志段首行变化影响归档兜底断言**：KN-25 段对称后归档日志首行为「开始」头，chaos `archivedLogWritten` 首行 `startsWith("DBG seed=")` 失效（测试侧未随语义同步） | `uitest/chaos-queue.mjs:360-372` | ✅ v0.7.4 已修复（兜底取「开始」头之后的实际内容行匹配） |

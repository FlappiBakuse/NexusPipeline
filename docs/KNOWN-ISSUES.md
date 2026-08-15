# 已知问题台账（Known Issues）

**建立日期**：2026-08-15（v0.6.9 全面代码评估产出）｜ **状态**：登记中，按版本分步修复（用户决策：全部记录，按版本分开修复）｜ **v0.6.10 交付**：文档不一致项（KN-48/49/50 涉及项）已随文档体系重组修正

> 本台账登记项目已确认的已知问题（潜在 BUG / 死代码 / 文档不一致 / 约束违规），不包含已修复项（见 [CHANGELOG.md](../CHANGELOG.md)）。修复版本排期建议见 [ROADMAP.md](ROADMAP.md)；代码定位指引见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 分级说明

- **高**：数据丢失 / 配置损坏 / 重复执行 / 资源泄漏风险，建议近期版本优先修复；
- **中**：逻辑瑕疵 / 边界缺陷 / 一致性违规，按版本节奏修复；
- **低**：死代码 / 文档过时 / 样式与自约束相悖，随版顺手清理。

## 高优先级

| 编号 | 问题 | 位置 | 建议版本 |
|---|---|---|---|
| KN-01 | **损坏配置被静默覆盖（数据丢失）**：scripts/queues.json 解析失败仅 Warn 并返回空列表，用户任意一次保存即整体覆盖损坏文件，原数据不可恢复 | `src/Persistence/JsonStore.cs:82-85`、`src/Persistence/ConfigStore.cs:30-33` | v0.7.x |
| KN-02 | **POST 可注入已存在 Id 造成重复记录**：客户端提交已存在 Id 时保留（仅空/不存在才重新生成），集合出现两条同 Id 记录 | `src/Web/ApiScriptsHandler.cs:109-112`、`src/Web/ApiQueuesHandler.cs:55-58` | v0.7.x |
| KN-03 | **队列重复触发**：`Register` 仅对 script 查重，手动 + 定时并发触发同一队列会双跑（重复历史/通知/系统操作，如双关机命令）；Scheduler 的 `_runningQueueIds` 挡不住手动入口 | `src/Services/DispatchCenter.cs:292-305` | v0.7.x |
| KN-04 | **共享集合无锁并发**：`RuntimeContext.Scripts/Queues` 与 `RunningExecution.Records` 被 Web 请求线程与后台线程并发读写，远程模式下可抛 `ArgumentOutOfRangeException`/`InvalidOperationException: 集合已修改`（前端轮询 500） | `src/RuntimeContext.cs:28-30`、`src/Services/DispatchCenter.cs:37`、`src/Web/ApiStatusHandler.cs:41-58` | v0.7.x |
| KN-05 | **CLI 删除脚本/队列不清理**：删除仅移除列表并保存，不清理 `data/{脚本Id}` 目录、不释放 ScriptConfigGate/Mutex、不检查运行状态（Web 端有完整清理，行为不一致，静态字典泄漏） | `src/Cli/ScriptsMenu.cs:70-78`、`src/Cli/QueuesMenu.cs:77-85` | v0.7.x |
| KN-06 | **远程访问下脚本图标全部 401**：`<img src="/api/scripts/{id}/icon">` 无法携带 `Authorization: Bearer` 头，远程模式图标请求必失败（有占位图兜底不崩溃，功能失效） | `wwwroot/views/scripts.js:57`、`wwwroot/views/queues.js:83`、`src/Web/WebServer.cs:197-203` | v0.7.x |

## 中优先级

| 编号 | 问题 | 位置 | 建议版本 |
|---|---|---|---|
| KN-07 | **resolve.json 多占位符静默丢弃**：`ResolveArgs/ResolvePath` 命中第一个占位符即 return，`{launcher} --config {assistant}` 之类的模板会丢掉后续全部内容（文档已声明「仅整体替换」，但应显式校验或报错而非静默丢弃） | `src/Plugins/DataSpecializedPlugin.cs:219-262` | 随版 |
| KN-08 | **bat 游戏启动器等待不随测试加速缩放**：`WaitForGameProcessAsync` 对 bat 用真实 `Task.Delay(timeout)`，加速档（scale=10）下白等 GameWaitSeconds 真实秒数 | `src/Services/RunSession.cs:808-828` | 随版 |
| KN-09 | **日志截断后立即写入的内容漏判窗口**：`ReadNew` 长度检查在 `Length < position` 时把 position 置为新尾——若截断后、下次读取前已写入新内容，截断后新写内容不进入判定输入（失败关键字可能漏判） | `src/Services/LogMonitor.cs:138-143` | 随版 |
| KN-10 | **明文旧密钥直接回显**：settings.json 中未加密（旧版明文或手工编辑）的 Webhook 地址/密钥在 GET /api/settings 时明文完整返回，违反「已设置的密钥不回显明文」契约 | `src/Web/ApiSettingsHandler.cs:279-294` | 随版 |
| KN-11 | **Python 判断脚本尾行 JSON 丢失竞态**：`BeginOutputReadLine` + `WaitForExitAsync` 时进程退出瞬间异步输出事件可能未投递完，契约规定的 stdout 尾行 JSON 有丢失风险（误判「无合法输出」） | `src/Services/JudgeScriptRunner.cs:403-420` | 随版 |
| KN-12 | **modal 焦点陷阱空白点击后失效**：点击弹窗内非焦点区域后 `activeElement` 落到 body，Tab 焦点逃逸到弹窗外（locked 弹窗同样受影响）；建议 mask 补 focusout 兜底 | `wwwroot/core/modal.js:45-64` | 随版 |
| KN-13 | **limits 警告层无障碍缺失**：`role="alertdialog"` 遮罩无 `aria-labelledby`、无初始焦点、无焦点陷阱、无焦点恢复（不走 modal 组件） | `wwwroot/views/limits.js:32-48` | 随版 |
| KN-14 | **触控目标 < 40px**：基础按钮 38px、`.sm` 按钮 32px（≤600px 才升 40px）、侧栏主题按钮 32px、drag-handle 36px，违反「触控目标不得小于 40px」约束 | `wwwroot/style.css:119/180/186/192/255/391` | 随版 |
| KN-15 | **style.css 硬编码色值 + uppercase eyebrow**：`.badge.*` rgba 背景、`.field-error` box-shadow、`color:#fff` 未走 CSS 变量；`.nav-caption` 与 `.eyebrow` 大写英文小字违反 Notion 基线 | `wwwroot/style.css:111/129/186/191/220/242-245`、`wwwroot/index.html:36` | 随版 |
| KN-16 | **dispatch 面板轮询整块替换打断交互**：2 秒轮询整块替换运行面板 innerHTML，打断用户滚动/选中日志文本（dashboard 已改局部更新，dispatch 未同步） | `wwwroot/views/dispatch.js:24-31` | 随版 |

## 低优先级（死代码 / 冗余 / 文档）

| 编号 | 问题 | 位置 |
|---|---|---|
| KN-17 | `SendStrategy` 配置字段全链路无效（死配置，v0.4.x 遗留；CLI 只切 Webhook/SMTP 开关） | `src/Models/AppSettings.cs:25` |
| KN-18 | `Audit.Cli` 常量从未使用（CLI 菜单全部用 `Audit.Manage`） | `src/Services/Audit.cs:10` |
| KN-19 | `JudgeScriptRunner.IsSupportedLanguage` 无调用方 | `src/Services/JudgeScriptRunner.cs:57-60` |
| KN-20 | `LogLevel.ToSetting` 扩展方法无调用方 | `src/Utilities/LogLevel.cs:14-25` |
| KN-21 | `PluginManager.SpecializedPlugins` 属性无调用方 | `src/Plugins/PluginManager.cs:28-29` |
| KN-22 | `DispatchCenter.RunScriptAsync` 的 `runUsers.Add(null)` 兜底不可达（StartScript 已保证有启用用户） | `src/Services/DispatchCenter.cs:400-404` |
| KN-23 | `PluginManager.NotifyChannels` 中 `OfType<IPlugin>()` 恒真空操作 | `src/Plugins/PluginManager.cs:22` |
| KN-24 | `SetEnabled` 对内置插件同时写 `DisabledPlugins`（冗余写入，`IsEnabled` 只查 `EnabledPlugins`）；传入不存在的插件名也静默写入 | `src/Plugins/PluginManager.cs:213-220` |
| KN-25 | `RunSession` 尝试日志段首尾不对称（段含「结束」头不含「开始」头） | `src/Services/RunSession.cs:227-229` |
| KN-26 | `WebhookSender.Types` 与 `ConfigStore.Normalize` 的 WebhookType 白名单双份维护 | `src/Services/WebhookSender.cs:14`、`src/Persistence/ConfigStore.cs:68-71` |
| KN-27 | icon 响应未走 `HttpHelper.ServeFile`，漏 CSP 等安全头 | `src/Web/ApiScriptsHandler.cs`（icon 分支） |
| KN-28 | `KillTree` 排除游戏名进程时日志文案误导（「PID 已不存在」实为被排除） | `src/Utilities/SystemActions.cs:188-191` |
| KN-29 | `SessionJudge` 三个嵌套 enum 显式 public（外层 internal，实际无暴露风险，但违背「其余一律 internal」声明） | `src/Services/SessionJudge.cs:13/21/29` |
| KN-30 | `ApiSettingsHandler` PUT 同一请求双次 Save + 双次 Normalize/RefreshLevel | `src/Web/ApiSettingsHandler.cs:80-85` |
| KN-31 | `AuthFails` 静态字典按远端 IP 累积永不清理（远程模式下内存缓慢增长） | `src/Web/WebServer.cs:33` |
| KN-32 | 请求体空键 `""` 时 `field[0]` 抛 IndexOutOfRange（应为 400） | `src/Web/ApiSettingsHandler.cs:247` |
| KN-33 | Webhook 成功判定只看 body `code==0` 忽略 HTTP 状态码 | `src/Services/WebhookSender.cs:85-87` |
| KN-34 | `JsonLiteral` 手写转义未处理 `\b`/`\f` 等控制字符 | `src/Services/WebhookSender.cs:166-175` |
| KN-35 | 历史详情固定查 31 天窗口与列表保留天数（上限 365）不一致，180 天前记录点详情 404 | `src/Web/ApiHistoryHandler.cs:129`、`src/Services/HistoryService.cs` |
| KN-36 | `RemoveMutex` 与 `WithSwapLock` 并发时 `WaitOne` 抛 ObjectDisposedException（无 catch，KN-05 同源分支） | `src/Services/ConfigSwapPrimitives.cs:86-104` |
| KN-37 | 用户改名/删除大小写敏感（`==`）与 users/order 的 OrdinalIgnoreCase 不一致 | `src/Web/ApiScriptsHandler.cs:544` |
| KN-38 | `else if (!queue.NotifyEnabled)` 冗余非运算 | `src/Services/DispatchCenter.cs:574-582` |
| KN-39 | Python 解释器多安装时 `candidates[0]` 选中不确定（Directory.GetFiles 顺序未定义） | `src/Services/JudgeScriptRunner.cs:365` |
| KN-40 | `/api/plugins/{name}/enable` 之外任意字符串都按 disable 处理（应校验 enable/disable） | `src/Web/ApiPluginsHandler.cs:17` |
| KN-41 | `PromptEdit`/`PromptEditMasked` 高度重复可合并 | `src/Cli/Ui.cs:29-103` |
| KN-42 | 编辑会话 start 的 catch 把 `ex.Message` 直接回给客户端；`keepGate=true` 后写响应异常不释放 gate | `src/Web/ApiScriptsHandler.cs:811-814` |
| KN-43 | 前端死代码：`core/format.js` 的 `dayDesc/actionLabel`、`core/pager.js` 的 `unregisterPager`、`views/scripts.js` 的 `FALLBACK_ICON` 冗余别名 | `wwwroot/core/format.js:25-35`、`wwwroot/core/pager.js:18-20`、`wwwroot/views/scripts.js:15` |
| KN-44 | 前端 `select` 的 click 与 change 双触发（当前调用点均幂等，属隐患模式）；`selectField` option 未走 `esc()` | `wwwroot/app.js:59-67`、`wwwroot/core/forms.js:20` |
| KN-45 | `ui.js` localStorage 写入无异常保护（隐私模式/禁用存储下白屏） | `wwwroot/core/ui.js:183` |
| KN-46 | 队列任务用户数估算与后端校验口径不一致（先加任务后选脚本时估算偏低） | `wwwroot/views/queues.js:168-188` |
| KN-47 | 设置页双「保存设置」按钮（语义重复）；侧栏「本地服务 · 127.0.0.1」硬编码文案远程模式不准确 | `wwwroot/views/settings.js:22`、`wwwroot/index.html:30` |
| KN-48 | DESIGN.md 过时：截断表格（P8 部分截断尾续读）、fresh 判定（LastWriteTime → 长度快照）、保留上限 180（→ limits.json 动态，默认 180/上限 365） | `docs/DESIGN.md:91/222/226/261` |
| KN-49 | ARCHITECTURE.md 依赖方向描述与实现不符：Services→Plugins（UserConfigManager/DispatchCenter）、Plugins→Services（PluginManager/NotifyPlugin）、Logger→RuntimeContext | `docs/ARCHITECTURE.md:40-43`、`src/Services/UserConfigManager.cs:4`、`src/Plugins/PluginManager.cs:3`、`src/Utilities/Logger.cs:33` |
| KN-50 | 文档断言数字/表述残留：DEVELOPMENT.md 旧版 chaos「171」、AGENTS/README 数字同步检查 | `docs/DEVELOPMENT.md`（2026-08-15 重构时修正） |

> 注：`docs/DEVELOPMENT.md`、`docs/ASSESSMENT.md` 已于 2026-08-15 文档体系重组中重构/更新（KN-48/KN-49/KN-50 涉及项在重组时一并修正，台账状态以实际为准）。

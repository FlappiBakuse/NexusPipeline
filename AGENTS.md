# AGENTS.md

NexusPipeline（枢链）：C#/.NET 8 (net8.0-windows) WinForms 托盘 + 纯静态 Web UI（HttpListener，零前端构建）的脚本管理器。仓库公开、MIT 协议。

## 构建与测试（顺序重要）

```powershell
# 1. 构建（产物输出到 release/，不提交）
build.cmd                      # 提权版（requireAdministrator，唯一构建形态；无 /test 无提权版）
# 源码在 src/，运行物在 release/；程序必须以管理员身份运行（非管理员启动拒绝并退出，exit 2）；
# 开机自启为计划任务（onlogon + highest）

# 2. 端到端测试（headless，系统 Edge，无窗口）
$env:PLAYWRIGHT_BROWSERS_PATH = "uitest\browsers"
node uitest\test.mjs           # 全量 466 项断言（发布前本地回归）；先跑 build.cmd，否则 setupRuntime 直接中止
node uitest\test.mjs --ci      # CI 核心回归集：443 项断言（剔除纯外观用例 testResponsiveShell，含响应式粗检）
```

- e2e 测试自带 `uitest/runtime/` 隔离目录（复制 release 版 exe+wwwroot+plugins），**不得污染项目根**；断言数字 466 / 443（用例增减须同步更新本文件数字）。
- 测试中日期一律用 `localDate()`（本地时区）；**禁止 `new Date().toISOString()`**（UTC 日期在跨午夜时使历史/日志断言失败——曾踩坑）。
- 新建后的 UI 断言用 `waitForFunction` 轮询文本，不要立即 `textContent`（CI 慢速环境偶发时序失败）。

## Git 协作规范（强制）

- **v1.0.0 之前（早期开发阶段）**：`main` 无分支保护（仓库无 ruleset），**直接 push main**，不走 PR；提交前先 `git pull` 避免分叉，**禁止 force push**；版本发布一律 **Pre-release**。
- **v1.0.0 起（正式版本）**：所有改动**只能通过 Pull Request** 合入 `main`（CI「构建 + e2e 测试」全绿后 squash 合并），禁止直接 push/force push main。
- 分支：`feat/`、`fix/`、`docs/`、`refactor/`、`test/`、`chore/` 前缀。
- 提交标题：Conventional Commits（完整规范见 `docs/DEVELOPMENT.md`），type/scope 英文 + 描述中文（如 `fix: 修复历史详情时区错位`、`feat(plugins)!: ...`）；type 表含 feat/fix/docs/refactor/perf/test/build/ci/chore/style/revert。
- 版本发布（规则见 `docs/DEVELOPMENT.md` §5-§6）：tag `vX.Y.Z` + `gh release create --prerelease --title vX.Y.Z --notes-file`；release 标题与 notes 格式参考 v0.3.1（`## vX.Y.Z（Pre-release）` + `###` 功能分组 + 要点列表）。
- **资产与 SHA（强制）**：zip 资产 `NexusPipeline-vX.Y.Z-win-x64.zip`（exe+wwwroot+plugins+README+LICENSE，**排除 config/**）；SHA 资产为与 zip **同名成对的 `.sha256`** 文件（`NexusPipeline-vX.Y.Z-win-x64.zip.sha256`），**内容为纯 hash、不含文件名**（遵守 v0.2.1 及之前规则），禁止 v0.3.1 的 `SHA256.txt` 汇总格式。

## 运行时数据（易混淆，勿改错）

| 位置 | 内容 |
|---|---|
| `config/settings|scripts|queues.json` | 用户配置（PascalCase，**含加密密钥与用户数据，永不提交**；`Program.MigrateLegacyConfig()` 负责旧配置迁移） |
| `history/YYYY-MM-DD/HH-mm-ss.json` + `.log` | 运行状态（PascalCase，如 `Attempts`/`FinalStatus`/`LogFile`）+ 脚本日志全文（成对，冲突加 `-1` 后缀） |
| `logs/YYYY-MM-DD.log` | 脚本控制台输出（与脚本日志严格分离） |
| `logs/nexus-pipeline-YYYYMMDD.log` | 管理器日志，审计行 `[审计] 来源 \| 操作（详情）`，来源 web/manage/cli/scheduler/system |

- **磁盘 JSON = PascalCase；Web API 返回 camelCase**（`JsonOpts.Web`）；读测试 JSON 前先 `.replace(/^\uFEFF/, "")` 去 BOM。
- `FinalStatus`：success / partial（重试>1 或日志含 ERROR|错误|异常|失败）/ failed / cancelled。
- `plugins/` 必须有占位文件（git 不跟踪空目录）——删除时保留 `plugins/.gitkeep`。

## 日志级别（v0.1.1+）

- 管理器日志（`logs/nexus-pipeline-YYYYMMDD.log`）带级别：`[HH:mm:ss] [LEVEL] 消息`，LEVEL 为 DEBUG/INFO/WARN/ERROR/FATAL。
- **禁止使用 `Logger.Log(msg)`**：一律显式调用 `Logger.Debug/Info/Warn/Error/Fatal(msg)`（审计行 `Audit.Log` 为 INFO，跟随阈值不过滤豁免）。
- 阈值取自 `settings.json` 的 `LogLevel`（debug/info/warn/error/fatal，默认 info），**即时生效**；`ConfigStore.Normalize` 校验非法值回退 info。
- DEBUG 级 Web 请求记录在 `WebServer.HandleAsync`，`GET /api/status` 轮询豁免不记录。
- 控制台按级别着色（WARN 黄 / ERROR 红 / FATAL 红底白字），仅在 `Console.IsOutputRedirected == false` 时启用；`logs/YYYY-MM-DD.log`（脚本控制台输出，ConsoleLog）不参与级别过滤。

## 环境陷阱（Windows PowerShell 5.1）

- `Set-Content` 会破坏 UTF-8 中文：写文件用编辑工具或 `[System.IO.File]::WriteAllText(..., [Text.Encoding]::UTF8)`。
- **0x800700E8 (ERROR_NO_DATA)：无控制台父进程启动 cmd.exe、PowerShell 等控制台程序必须带有效 stdio（CreateProcess + RedirectStandardOutput/Error=true 并消费），否则报 232**（CreateProcess 抛异常 / ShellExecute 弹「出现错误」对话框）。`BuildScriptStartInfo`、`StartVisible` 与批处理游戏启动均按此实现；**禁止**对 bat 用 UseShellExecute、禁止无重定向启动 cmd（本机曾三度踩坑）。
- `build.cmd` 与 `uitest\run-uitest.cmd` 必须保持非交互，不得加入无条件 `pause`，否则 PowerShell/CI 调用会一直等待按键。
- `git mv` 不展开通配符：`Get-ChildItem -Filter "*.cs" | ForEach-Object { git mv $_.Name "src\$($_.Name)" }`。
- `gh api --jq` 的复杂表达式（含逗号/引号）会被 PowerShell 拆参：用无空格表达式或输出 JSON 再本地处理；`gh pr create --body` 含引号/长文时改用 `--body-file`。
- 运行进程残留会锁定 `release\nexus-pipeline.exe`，重构建前先 `Get-Process nexus-pipeline | Stop-Process`。
- **gh/PowerShell 中文操作三坑（曾踩，修复 release 时中招）**：
  1. `gh api ... --jq .body` 多行输出被 PowerShell 5.1 捕获为 `string[]`，`[IO.File]::WriteAllText(路径, 数组)` 会用空格连接、**换行全部丢失**；必须先 `[Console]::OutputEncoding = UTF8`，并用 `($body -join "`n")` 显式转字符串。
  2. 未设 UTF8 输出编码时 gh 的 UTF-8 中文被 GBK 误读（mojibake），且经 GB18030 往返**有损不可逆**；含中文的 gh 写操作一律走文件：`gh release edit --notes-file`（UTF-8 无 BOM），命令内不写中文字面量。
  3. **修改已发布 release（edit body / 资产）前，先 `gh api ... --jq .body` 把原正文备份到本地文件**，再动手。
- 脚本自启动参数（Args）以显式路径开头（`X:\`、`\\`、`.\`、`..\`）时 =「运行时启动目标」（管理端/执行端分离）：整段到 `?` 为止为路径（路径段去尾随空格），相对脚本根目录标准语义解析，含空格无需引号；`?` 后为启动目标参数；**Args 一律禁止引号**（引号视为普通参数内容，不用于路径，避免歧义）；解析失败回退主程序并警告。其他路径字段（RootPath/MainExe/ConfigPath/LogPath/GameExe）保留去成对首尾引号功能。
- 脚本启动 `Win32Exception 740`（ERROR_ELEVATION_REQUIRED）＝目标程序 manifest 要求管理员：**程序必须管理员运行**（`Program.Main` 启动自检 `WindowsPrincipal.IsInRole(Administrator)`，非管理员 → FATAL + 提示框 + exit 2），管理员下同权限直接 CreateProcess，740 不再发生；仍出现时给出明确中文错误并失败，**禁止 runas 降级提权**（不接管 stdout、脚本独立弹窗，违背"必须管理员"意图）；`StartVisible`（编辑配置）同样处理。

## 主要入口

- `src/Program.cs`：CLI 分发（服务/manage/status/web/run-script/run-queue/cancel/register/unregister）+ 配置迁移；启动编排见 `src/Bootstrap.cs`。
- `src/Web/WebServer.cs`：HTTP 骨架 + 路由表（每个 `/api/*` 资源一个 `ApiXxxHandler`，见 `src/Web/`）；`GET /api/status` 不记审计（轮询豁免）。
- `src/Cli/`：命令行菜单（MainMenu + 脚本/队列/调度/历史/插件/设置/通知渠道 7 个菜单类）。
- `wwwroot/`（项目根目录，非 src 下）：前端 `app.js` 只做路由 + 各视图 `actions` 注册表合并分发；视图一域一文件（`views/scripts|users|queues|dispatch|history|plugins|settings|dashboard.js`），共享模板在 `core/forms.js`，弹窗在 `core/modal.js`。页面结构：仪表盘首行 4 卡（脚本数/队列数/下一调度倒计时/版本）+ 插件 1/4 小卡片；插件页可进 `#/plugins/{name}` 配置二级页；脚本弹窗主程序+参数同行、三个游戏/通知复选框同行（启动游戏｜强制关闭｜发送通知，强制关闭独立于启动游戏）、运行设置区含自定义完成标志（v0.4.0+，见后端约定）；**无系统选择按钮**（用户手填路径）。
- 模块边界与定位指南见 `docs/ARCHITECTURE.md`（v0.2.0+）。
- CI：`.github/workflows/ci.yml`（windows-latest，build.cmd + npm ci + e2e）。

## 后端分层约定（v0.2.0+）

- 命名空间：`NexusPipeline`（核心域）/ `NexusPipeline.Web` / `NexusPipeline.Cli` / `NexusPipeline.Plugins`。
- **public 仅限契约**：Program、插件契约（IPlugin/ISpecializedScriptPlugin/ScriptProfile/PluginContext/INotifyChannel）、插件签名需要的领域模型（AppSettings/ScriptInstance/ScriptUser/DispatchQueue/QueueTask/QueueTimeSet/RunRecord/RunAttempt）；其余一律 `internal`。
- 新 API 路由：`src/Web/` 的 `ApiXxxHandler` + 路由表注册一行；新菜单：`src/Cli/` 对应菜单类；新服务：核心域 + RuntimeContext 持有。
- 插件只能通过 `PluginContext` 与宿主交互；通知能力实现 `INotifyChannel`（`DispatchCenter` 经 `PluginManager.NotifyScriptAsync/NotifyQueueAsync` 分发，无静态委托）；专用插件实现 `ISpecializedScriptPlugin`（`Resolve(rootPath)` 推导主程序/参数/配置/日志与**完成标志**，保存专用脚本实例时固化快照，完成标志同步固化；`GameName` 提供中文游戏名，脚本卡片徽章显示「{GameName}专项」，**游戏名不得写入主程序**，仅由插件提供；外部插件默认启用，显式禁用记入 `DisabledPlugins`）。
- **完成标志**：判定优先级（v0.4.0+）= 判断脚本（`JudgeScriptEnabled`+代码，脚本优先，忽略关键字）→ 成功/失败关键字（`SuccessKeywords`/`FailureKeywords`，行内逗号 AND、换行 OR，失败命中立即终止本次尝试，成功命中等待退出 60 秒）→ 专用插件固化标志（BetterGI=`一条龙和配置组任务结束`、March7thAssistant=`游戏终止：StarRail`、ZenlessZoneZeroOneDragon=`关闭游戏成功`）→ 无任何配置时按「进程自行退出」判定成功；**专用插件脚本实例强制清空自定义字段**（关键字与判断脚本均不允许，后端 `ApplyProfile` 兜底）。
- **判断脚本契约（v0.4.0+）**：输入为临时 JSON（脚本字段+用户+`config`（运行时生效配置 ConfigPath，只读）与 `script`（`data/{脚本Id}/{用户名}/script`，可读写；无用户兜底 `data/{脚本Id}/script`）全递归文件清单+`scriptDir`+日志全文），JS 用内置 Jint 引擎（注入 `__NEXUS_INPUT__`/`nexus.readFile`（限 config/script 范围、单文件 2MB）/`nexus.writeFile`（相对 script 目录、防 `../` 与绝对路径逃逸）/`nexus.listFiles()`/`console.log`，无 Node 库），Python 用系统 `python.exe`（`sys.argv[1]` 输入 JSON 路径，读写边界由文档约定）；输出 stdout 尾行 JSON `{"status":"success|failed","reason":"必填","notifyText":"可选","replaceConfigs":["相对script目录路径"]}`，无输出/非 JSON/缺 status 或 reason=继续运行（仍受无日志更新超时约束），单次执行 30 秒上限，执行错误=警告+继续运行；`notifyText` 替换脚本级通知正文（`RunRecord.CustomNotifyText`，不落盘）。
- **插队替换配置（v0.4.0+）**：判断脚本返回 `failed` + `replaceConfigs` 时，宿主从 script 目录复制覆盖到 config 对应位置（首次替换前备份到 `data/{脚本Id}/{用户名}/replace-backup`，`.meta` 记录 configPath），本次尝试失败后由重试循环自动用新配置重试（支持多轮替换，计入 MaxAttempts）；运行结束（成功或失败至最大次数）从 replace-backup 还原全部被替换文件并清空 script 目录与备份（有用户时配置交换机制亦会还原，备份为双保险）；启动崩溃恢复（`UserConfigManager.RecoverInterrupted`）扫描 replace-backup 残留自动还原。
- **判断脚本触发时机（v0.4.0+）**：① 每次日志新增批次触发一次（串行不叠加）；② 日志阻塞（进程存活、已有日志但 30 秒无新内容）周期触发一次（不重置无更新超时）；③ 主进程退出且本次尝试无判定结果时**最终触发一次**（拿最终判定，仅一次防循环）。
- **脚本路径校验**（`Limits.CheckScriptPaths`，Web/API/CLI 三处统一）：通用脚本根目录/主程序/配置路径必须存在（主程序需可执行），日志路径仅格式合规（支持占位符与通配符，不查存在性）；专项脚本（插件固化路径）仅校验根目录存在；游戏路径一律必填且必须为可执行文件（运行前启动游戏、运行后强制关闭游戏均与游戏路径填写解绑；任务失败时无条件强制结束游戏进程）。游戏配置卡在弹窗内**常驻显示**（不与启动游戏复选框绑定）。
- **脚本图标**：`ApiScriptsHandler.ExtractIcon` 取主程序 PE 资源最高分辨率图标（`EnumResourceNames` 遍历 RT_GROUP_ICON，GRPICONDIR 选最大，含 256×256），无资源回退关联图标，bat/cmd 直接 404（前端占位图）。
- 日志路径为「路径格式」（如 `{YYYY-MM-DD}.log`、`{YYYY-MM-DD-*}.log`、固定文件 `log.txt`），严格按格式匹配（`LogPattern.ResolveFile`），禁止格式外猜测；脚本启动后无日志条目按"日志无更新超时"失败。

## 前端开发强约束（v0.2.0+）

- wwwroot 必须保持零构建、零 CDN 依赖；使用原生 ES modules，浏览器直接加载 `.js` 文件，不引入需要打包步骤的框架或工具链。
- 模块边界固定为：`app.js`（启动/路由/注册表分发）、`core/api.js`（请求）、`core/state.js`（生命周期与跨域缓存）、`core/ui.js`（页面/Toast/主题/`initAutoScroll` 长文本滚动）、`core/modal.js`（弹窗）、`core/forms.js`（表单模板，长提示用 `scrollField` 滚动浮层，禁止超长原生 placeholder）、`core/dom.js`（查询）、`core/format.js`（格式化）、`views/`（页面，一域一文件）、`effects/`（独立视觉效果）。业务视图不得修改另一个视图的 DOM；新增交互 = 视图导出函数 + 加入该视图 `actions` 注册表（不再往 app.js 加 case）。
- 所有颜色、背景、边框、阴影、圆角、间距和层级必须使用 CSS 变量；禁止在视图模板中写 `style="..."`，禁止新增散落的颜色字面量。
- 所有页面必须在 360px 手机、768px 平板、1280px 电脑视口可用；禁止固定宽度导致溢出，密集数据必须放入横向滚动容器，表单必须允许堆叠，触控目标不得小于 40px。
- 禁止新增 inline `onclick`、`onchange` 等事件；交互统一使用 `data-action` + `app.js` 事件委托。可交互元素必须使用原生 `button`、`a`、`input`、`select` 或 `textarea`。
- e2e 依赖的节点必须提供稳定的 `data-testid`；测试不得依赖按钮顺序、随机 CSS 层级或仅依赖装饰性文案。已有业务 ID 和 `data-action` 变更时必须同步测试。
- 轮询页面必须通过 `state.js` 注册 timer 和 AbortController，并在路由切换时清理；轮询只能更新状态区域，不得覆盖用户正在编辑的表单、焦点和滚动位置。
- 表单标签必须使用 `label[for]`，必填字段使用 `required` 或等效错误提示；弹窗必须有 `role="dialog"`、`aria-modal`、标题关联、Esc 关闭、焦点陷阱和关闭后的焦点恢复；Toast 必须使用 `role="status"`/`aria-live`。
- 主题至少支持 `light`、`dark`、`system` 三种状态，选择持久化到 `localStorage`；主题切换不能改变业务数据和路由。
- **Notion 风格基线（v0.3.6+，与上方约束同权，新样式必须遵守）**：
  - 浅色主题为 Notion 米色系：背景 `#f7f6f3`、面板纯白、正文墨色 `#37352f`、强调 `#2eaadc`（深色主题保留品牌深蓝）；
  - 字体一律系统栈（`-apple-system, "Segoe UI", "Microsoft YaHei"`，禁止引入 Inter 等外置字体）；
  - 圆角上限 12px（`--radius-lg`），徽章/进度条/输入框 6-8px；禁止胶囊（999px）与大圆角（`rounded-2xl` 级）；
  - 阴影仅 `--shadow` 轻量双层，禁止重阴影；
  - **禁止渐变**（唯一例外 `brand-mark` 品牌徽章）、**禁止 backdrop-filter 玻璃态**（侧栏/顶栏/弹窗遮罩一律实体背景 `--mask`）；
  - 禁止装饰性 translate/scale 动画与 hover 位移/阴影跳变；hover 反馈一律背景色（`--hover-bg`/`--panel-hover`），过渡仅 `transition-colors` 级；
  - 禁止 uppercase 小字 eyebrow；页面 kicker 用常规 `muted` 小字。
- **展示模式（v0.3.6+）**：脚本实例/调度队列/用户列表统一为紧凑列表行（`.script-card`/`.user-card` 行样式 + 卡内分隔线 + 行 hover 背景）；禁止新增其他卡片式条目布局，新条目一律先复用行样式。
- **密钥字段语义（v0.3.6+）**：密钥/敏感字段（Webhook 地址与签名密钥、SMTP 授权码）合并进「保存设置」统一提交，仅非空值提交（留空=不变，不提供清除按钮）；Webhook 地址用 `type="text"`、其余密钥类用 `type="password"`；已设置的密钥不回显明文，placeholder 提示「（已设置，留空不变）」。
- **自定义完成标志前端（v0.4.0+，仅通用脚本弹窗「运行设置」区显示，专用脚本整块不渲染）**：默认显示成功/失败关键字填写框（各独占一行 textarea，placeholder 说明「每行一组，组内逗号分隔为 AND，换行为 OR」）；底部公共操作区为「上传脚本文件」按钮（仅脚本模式显示）与「使用判断脚本（脚本优先）」**切换按钮**（`data-action="toggle-judge-mode"` + `aria-pressed`，激活态 accent 高亮，点击在关键字/脚本两区互斥切换——禁止双复选框同 id）；脚本模式显示语言下拉（JavaScript 内置引擎 / Python 系统解释器）+ 等宽代码框（placeholder 含 `replaceConfigs` 契约说明）+ 上传按钮（`.js`/`.py` 扩展名自动识别语言并读入代码框，文件 ≤256KB）；保存校验：开启且代码为空时报错。
- 粒子效果必须使用独立 `effects/particles.js`，`pointer-events:none`，默认低透明度（v0.3.6 起：粒子点 0.12 / 连线 0.05 / 数量 ≤48 / 连线距离 ≤90px）；必须响应 `prefers-reduced-motion`、页面隐藏和窗口尺寸变化，不得阻塞主业务交互。
- 每次前端改动必须运行 `build.cmd` 和完整 e2e；新增或删除断言后同步本文件中的断言数字，并补充手机/平板/电脑至少一档回归。

# AGENTS.md

NexusPipeline（枢链）：C#/.NET 8 (net8.0-windows) WinForms 托盘 + 纯静态 Web UI（HttpListener，零前端构建）的脚本管理器。仓库公开、MIT 协议。

## 构建与测试（顺序重要）

```powershell
# 1. 构建（产物输出到 release/，不提交）
build.cmd                      # 提权版（requireAdministrator，唯一构建形态；无 /test 无提权版）
# 源码在 src/，运行物在 release/；程序必须以管理员身份运行（非管理员启动拒绝并退出，exit 2）；
# 开机自启为计划任务（onlogon + highest）
# 增量构建（v0.6.4+）：src 未变时跳过 dotnet publish，仅同步 wwwroot/plugins（指纹 .build-src-hash，不入库）

# 2. 端到端测试（headless，系统 Edge，无窗口；@playwright/test 框架，tests/ 按域 7 个 spec 文件）
$env:PLAYWRIGHT_BROWSERS_PATH = "uitest\browsers"
npx playwright test            # 全量 57 用例（发布前本地回归）；先跑 build.cmd，否则 globalSetup 中止
$env:NEXUS_CI = "1"; npx playwright test   # CI 核心回归集：56 用例（剔除响应式外壳外观用例）
# 时间加速（v0.6.4+，唯一加速档 NEXUS_TIME_SCALE=10；run-uitest.cmd 已默认内置）：宿主等待按比例缩放
# （1 分钟 stall → 6 秒、周期触发 30 秒 → 3 秒、marker 宽限 60 秒 → 6 秒、监控循环 1 秒 → 100ms），
# 三套测试（e2e 56 + judge 115 + chaos 171）合计约 10 分钟；发布前用真实计时档（不设该变量）跑全量回归
$env:NEXUS_TIME_SCALE = "10"; npx playwright test   # 加速档
Remove-Item Env:NEXUS_TIME_SCALE; npx playwright test   # 真实计时档

# 3. 单元测试（v0.6.4+，毫秒级，无管理员）：判定状态机/关键字规则/日志路径解析/模型规则校验等纯逻辑
dotnet test src\NexusPipeline.Tests\NexusPipeline.Tests.csproj --nologo   # 58 断言；CI 每次必跑
```

- e2e 测试自带 `uitest/runtime/` 隔离目录（复制 release 版 exe+wwwroot+plugins），**不得污染项目根**；服务生命周期由 `tests/global-setup|teardown.mjs` 管理（PID 文件 `service.pid` 跨进程兜底）；用例数 57 / 56（用例增减须同步更新本文件数字；自建 assert 计数机制已随 v0.5.1 迁移废弃）。
- 专项稳定性测试 `uitest/judge-scenarios.mjs`：115 项断言（场景 A/B/C/D、MaaEnd 专项判断脚本选择性重试、零日志 stall、修复验证 12 项、配置交换崩溃恢复），发布前与全量 e2e 一并运行；先跑 build.cmd。加速档：`$env:NEXUS_TIME_SCALE = "10"; node uitest\judge-scenarios.mjs`；发布前真实计时档不设该变量。
- 混沌调度队列压力测试 `uitest/chaos-queue.mjs`：171 项断言（固定/随机种子轮：队列串行进度、多用户配置交换、五种干扰判定 reason、崩溃注入、通知双模式、无残留；需管理员 shell），先跑 build.cmd。加速档：`$env:NEXUS_TIME_SCALE = "10"; node uitest\chaos-queue.mjs`。
- **加速档测试契约**（v0.6.2+，v0.6.4 统一 scale=10）：测试伪造脚本与判断脚本必须按 `NEXUS_TIME_SCALE`/`input.timeScale` 同步缩放墙钟常量（`ping -n 75` → 加速档 `ping -n 6`（judge，卡住 5s > 周期 3s）/ `ping -n 8`（chaos，卡住 7s > stall 6s）、`> 20000` → `> 20000 / scale` 等），保证场景语义（卡住时长仍远大于缩放后的周期触发间隔）；新增依赖真实墙钟的用例须同时给出加速与真实两档实现。
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
| `history/YYYY-MM-DD/HH-mm-ss.json` + `-{尝试号}.log` | 运行状态（PascalCase，如 `Attempts`/`FinalStatus`/`LogFile`，含每次尝试详情与各尝试 `LogFile`）+ **按尝试分批**的脚本日志全文（v0.5.3 起：`.json` 纯状态不含日志内容；每次尝试一个 `HH-mm-ss-{n}.log`；同秒冲突加 `-1` 后缀；旧版 `.console.log`/`runs-*.jsonl` 已废弃不写入） |
| `logs/nexus-pipeline-YYYY-MM-DD.log` | 管理器日志，审计行 `[审计] 来源 \| 操作（详情）`，来源 web/manage/cli/scheduler/system |

- **磁盘 JSON = PascalCase；Web API 返回 camelCase**（`JsonOpts.Web`）；读测试 JSON 前先 `.replace(/^\uFEFF/, "")` 去 BOM。
- `FinalStatus`：success / partial（重试>1 或日志含 ERROR|错误|异常|失败）/ failed / cancelled。
- `plugins/`（v0.6.3 起为数据化专项插件源目录，build.cmd 整体复制到 release/plugins/）：插件名子目录 = `plugin.json`（根文件）+ `data/`（`resolve.json` 推导配置、`judge.{js,py}` 判断脚本、可选 `config-template/` 默认配置模板目录），开发指南见 `plugins/README.md`。
- **清理（v0.4.4+）**：历史/管理器日志/旧聚合文件按保留天数（`HistoryRetentionDays`，默认 7、上限 180 由 `limits.json` 的 `MaxHistoryRetentionDays` 约束）每天清理一次（启动时 + 调度器每日首次 tick，服务持续运行同样生效）。

## 日志级别（v0.1.1+）

- 管理器日志（`logs/nexus-pipeline-YYYY-MM-DD.log`）带级别：`[HH:mm:ss] [LEVEL] 消息`，LEVEL 为 DEBUG/INFO/WARN/ERROR/FATAL。
- **禁止使用 `Logger.Log(msg)`**：一律显式调用 `Logger.Debug/Info/Warn/Error/Fatal(msg)`（审计行 `Audit.Log` 为 INFO，跟随阈值不过滤豁免）。
- 阈值取自 `settings.json` 的 `LogLevel`（debug/info/warn/error/fatal，默认 info），**即时生效**；`ConfigStore.Normalize` 校验非法值回退 info。
- DEBUG 级 Web 请求记录在 `WebServer.HandleAsync`，`GET /api/status` 轮询豁免不记录。
- 控制台按级别着色（WARN 黄 / ERROR 红 / FATAL 红底白字），仅在 `Console.IsOutputRedirected == false` 时启用；控制台输出不参与级别过滤（v0.4.4 起随历史按次保存，`ConsoleLog` 聚合文件已废弃）。

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
- `src/Web/WebServer.cs`：HTTP 骨架 + **特性路由表**（v0.5.0+：`[ApiRoute("资源名")]` 标注在 handler 类/方法上，`WebServer.Routes` 启动反射扫描注册，新增 API 无需改路由表；每个 `/api/*` 资源一个 `ApiXxxHandler`，见 `src/Web/`）；`GET /api/status` 不记审计（轮询豁免）。
- `src/Cli/`：命令行菜单（MainMenu + 脚本/队列/调度/历史/插件/设置/通知渠道 7 个菜单类）。
- `wwwroot/`（项目根目录，非 src 下）：前端 `app.js` 只做路由 + 各视图 `actions` 注册表合并分发；视图一域一文件（`views/scripts|users|queues|dispatch|history|plugins|settings|dashboard.js`），共享模板在 `core/forms.js`，弹窗在 `core/modal.js`。页面结构：仪表盘首行 4 卡（脚本数/队列数/下一调度倒计时/版本）+ 插件 1/4 小卡片；插件页可进 `#/plugins/{name}` 配置二级页；脚本弹窗主程序+参数同行、三个游戏/通知切换按钮同行（启动游戏｜强制关闭｜运行通知，强制关闭独立于启动游戏）、运行设置区含自定义完成标志（v0.4.0+，见后端约定）；**无系统选择按钮**（用户手填路径）。
- 模块边界与定位指南见 `docs/ARCHITECTURE.md`（v0.2.0+）。
- CI：`.github/workflows/ci.yml`（windows-latest，build.cmd + npm ci + e2e）。

## 后端分层约定（v0.2.0+，v0.5.0 目录重组）

- 命名空间：`NexusPipeline`（入口/组合根：Program/Bootstrap/RuntimeContext/TrayApp）/ `NexusPipeline.Models`（领域模型）/ `NexusPipeline.Services`（服务）/ `NexusPipeline.Persistence`（持久化）/ `NexusPipeline.Utilities`（工具，JsonOpts/Logger/TextRules 等）/ `NexusPipeline.Web` / `NexusPipeline.Cli` / `NexusPipeline.Plugins`。
- 依赖方向：Models 无依赖；Services 依赖 Models/Persistence/Utilities；Persistence 依赖 Utilities；根命名空间不依赖子域反向。
- **壳式 DI（v0.5.0+）**：`RuntimeContext` 组合根内建 `ServiceProvider`（注册 DispatchCenter/HistoryService/PluginManager/Scheduler），外部访问方式不变（`RuntimeContext.Instance.Xxx`）；新增服务注册进组合根构造，经 `RuntimeContext.Resolve<T>()` / 插件 `PluginContext.Resolve<T>()` 解析。
- **public 仅限契约**：Program 与领域模型（AppSettings/ScriptInstance/ScriptUser/DispatchQueue/QueueTask/QueueTimeSet/RunRecord/RunAttempt）；其余一律 `internal`（v0.6.3 起插件契约为宿主内置，无外部 DLL 消费者：IPlugin/INotifyChannel/PluginContext/ScriptProfile 均 internal）。
- 新 API 路由：`src/Web/` 的 `ApiXxxHandler` + 类上 `[ApiRoute("资源名")]`（子路由标在方法上，如 cancel），`WebServer` 反射扫描自动注册（v0.5.0+）；新菜单：`src/Cli/` 对应菜单类；新服务：`src/Services/` 新增类 + 注册进 `RuntimeContext` 组合根容器。
- **完成判定策略（v0.5.0 拆分）**：判定状态机内聚于 `SessionJudge`（`src/Services/SessionJudge.cs`）：判断脚本/关键字两模式；`RunSession` 监控循环经 `judge.HandleLine/ApplyJudgeResult/IsFailure/IsMarker` 驱动，判定语义不变。
- **插件体系（v0.6.3 数据化）**：内置 C# 插件（NotifyPlugin，仅此一个，走 `IPlugin`/`INotifyChannel`/`PluginContext`，`PluginManager.DiscoverBuiltIn` 注册）+ **数据化专项插件**（`DataSpecializedPlugin`，扫描 `plugins/` 下含有效 `plugin.json` 的子目录注册；`Resolve(rootPath)` 读 `data/resolve.json` 推导——`require` 全部满足（file 相对脚本根目录、`searchUpward=true` 逐级向上最多 4 层）才成功，`paths` 模板 `{var}`=绝对路径/`{rel:var}`=相对路径、args 无占位符原样返回，判断脚本 `data/judge.{js,py}` 按扩展名定语言，`data/config-template/` 为默认配置模板目录）；保存专用脚本实例时固化快照（`ApplyProfile` 覆盖主程序/参数/配置/日志/判断脚本），`GameName` 提供中文游戏名，脚本卡片徽章显示「{GameName}专项」，**游戏名不得写入主程序**，仅由插件提供；数据插件外部默认启用，显式禁用记入 `DisabledPlugins`；开发指南见 `plugins/README.md`。
- **完成判定**：判定优先级 = 判断脚本（`JudgeScriptEnabled`+代码，脚本优先，忽略关键字）→ 成功/失败关键字（`SuccessKeywords`/`FailureKeywords`，行内逗号 AND、换行 OR，失败命中立即终止本次尝试，成功命中等待退出 60 秒）→ 无任何配置时按「进程自行退出」判定成功；**专用插件脚本实例强制清空自定义关键字字段**（v0.6.0 起判断脚本由插件固化：`ApplyProfile` 每次保存覆盖 `JudgeScriptEnabled/Language/JudgeScript = profile.JudgeScript`，语言按插件判断脚本扩展名（.js→javascript / .py→python），用户不可编辑，判定走脚本模式；通用完成标志 `SuccessMarkers` 已废弃，v0.6.3 起全链路删除，旧配置残留字段反序列化自动忽略、下次保存自然丢弃）。
- **判断脚本契约（v0.4.0+）**：输入为临时 JSON（脚本字段+用户+`config`（运行时生效配置 ConfigPath，只读）与 `script`（`data/{脚本Id}/{用户名}/script`，可读写；无用户兜底 `data/{脚本Id}/script`）全递归文件清单+`scriptDir`+**本次尝试日志段**（v0.5.2+：判断脚本输入按尝试切片，只读当前尝试内容——上次尝试的失败/成功行不跨尝试污染判定；v0.4.3+：超过 4MB 仅提供尾部并置 `logTruncated`=true，防大日志拖垮内置引擎）），JS 用内置 Jint 引擎（注入 `__NEXUS_INPUT__`/`nexus.readFile`（限 config/script 范围、单文件 2MB）/`nexus.writeFile`（相对 script 目录、防 `../` 与绝对路径逃逸）/`nexus.listFiles()`/`console.log`，无 Node 库），Python 用系统 `python.exe`（`sys.argv[1]` 输入 JSON 路径，读写边界由文档约定）；输出 stdout 尾行 JSON `{"status":"success|failed","reason":"必填","notifyText":"可选","replaceConfigs":["相对script目录路径"]}`，无输出/非 JSON/缺 status 或 reason=继续运行（仍受无日志更新超时约束），单次执行 30 秒上限，执行错误=警告+继续运行；`notifyText` 替换脚本级通知正文（`RunRecord.CustomNotifyText`，不落盘）。
- **插队替换配置（v0.4.0+）**：判断脚本返回 `failed` + `replaceConfigs` 时，宿主从 script 目录复制覆盖到 config 对应位置（首次替换前备份到 `data/{脚本Id}/{用户名}/swap-backup`，`.meta` 记录 configPath 与新增文件清单，还原时删除新增文件）；config 为单文件时 replaceConfigs 项须等于该文件名（忽略大小写）方可替换，其余目标拒绝；本次尝试失败后由重试循环自动用新配置重试（支持多轮替换，计入 MaxAttempts）；运行结束（成功或失败至最大次数）从 swap-backup 还原全部被替换文件并清空 script 目录与备份（有用户时配置交换机制亦会还原，备份为双保险）；启动崩溃恢复（`UserConfigManager.RecoverInterrupted`）扫描 swap-backup 残留自动还原。
- **判断脚本触发时机（v0.4.0+）**：① 每次日志新增批次触发一次（串行不叠加）；② 日志阻塞（进程存活、已有日志但 30 秒无新内容）周期触发一次（不重置无更新超时）；③ 主进程退出且本次尝试无判定结果时**最终触发一次**（拿最终判定，仅一次防循环；日志超时/未找到日志文件失败路径同样补最终触发，判断脚本可借此返回替换配置再重试）。
- **运行前置（v0.4.2+）**：脚本实例运行必须至少有一个启用用户；手动运行（Web/CLI/调度中心）无启用用户时拒绝启动并报错，调度队列运行时跳过该脚本实例并记录 failed 历史（「脚本实例未配置启用用户，已跳过」），队列进度不计入该任务。
- **运行超时（v0.4.3+）**：`TotalTimeoutMinutes`（运行总时间超时）按**整个运行**（含全部重试与前置/后置用户脚本）计时，超时判定失败且不再重试；单次尝试时长由日志无更新超时控制。
- **远程访问（v0.4.4+）**：默认仅绑定 `127.0.0.1` 无认证；`settings.json` 的 `AllowRemoteAccess=true` 时绑定 `http://+:{port}/`（**禁止用 `0.0.0.0` 前缀**——http.sys 不接受，绑定必失败），**远程请求**（非回环地址）须带请求头 `Authorization: Bearer <token>`（令牌 DPAPI 加密存 `AccessToken`），本地请求豁免；开启时 `WebServer.Start` 绑定生效需重启，令牌校验即时生效。**防火墙**：开启远程（设置保存或启动时）自动 `netsh` 添加入站允许规则（`FirewallRule.EnsureAllowInbound`，失败仅告警），局域网设备访问须用本机局域网 IP（`NetInfo.ListLanAddresses` 枚举，写入启动日志与 `/api/settings` 的 `status.remote.lanAddresses`）。`Bootstrap.StartWebWithRetry` 每次重试必须新建 WebServer 实例（HttpListener.Start 失败后实例不可复用，复用抛 ObjectDisposedException 会闪退——已踩坑），非端口冲突异常返回 null 不崩溃。
- **脚本路径校验**（`Limits.CheckScriptPaths`，Web/API/CLI 三处统一）：通用脚本根目录/主程序/配置路径必须存在（主程序需可执行），日志路径仅格式合规（支持占位符与通配符，不查存在性）；专项脚本（插件固化路径）仅校验根目录存在；游戏路径一律必填且必须为可执行文件（运行前启动游戏、运行后强制关闭游戏均与游戏路径填写解绑；任务失败时无条件强制结束游戏进程）。游戏配置卡在弹窗内**常驻显示**（不与启动游戏复选框绑定）。
- **脚本图标**：`ApiScriptsHandler.ExtractIcon` 取主程序 PE 资源最高分辨率图标（`EnumResourceNames` 遍历 RT_GROUP_ICON，GRPICONDIR 选最大，含 256×256），无资源回退关联图标，bat/cmd 直接 404（前端占位图）。
- 日志路径为「路径格式」（如 `{YYYY-MM-DD}.log`、`{YYYY-MM-DD-*}.log`、固定文件 `log.txt`），严格按格式匹配（`LogPattern.ResolveFile`），禁止格式外猜测；脚本启动后无日志条目按"日志无更新超时"失败。**日志监控（v0.5.2+，v0.6.5 收紧 fresh 判定）**：忽略运行前已有内容——尝试开始前不存在的文件才从头读；已存在（含残留日志）从「尝试开始时文件长度」续读，只读本次尝试新增内容（残留被启动后追加写刷新 LastWriteTime 不再误判从头读，旧内容不污染判定输入）；同路径文件被替换（move 归档重建/删除重建）按 FileId 检测（`LogMonitor.FileReplaced`）重开从头读；文件被截断（长度归零）自动从头重读。

## 前端开发强约束（v0.2.0+）

- wwwroot 必须保持零构建、零 CDN 依赖；使用原生 ES modules，浏览器直接加载 `.js` 文件，不引入需要打包步骤的框架或工具链。
- 模块边界固定为：`app.js`（启动/路由/注册表分发）、`core/api.js`（请求）、`core/state.js`（生命周期与跨域缓存）、`core/ui.js`（页面/Toast/主题/`initAutoScroll` 长文本滚动）、`core/modal.js`（弹窗）、`core/forms.js`（表单模板，长提示用 `scrollField` 滚动浮层，禁止超长原生 placeholder）、`core/dom.js`（查询）、`core/format.js`（格式化）、`core/pager.js`（通用分页组件，无业务依赖）、`views/`（页面，一域一文件，含 `views/limits.js` 约束警告卡片——v0.5.1 起由 core 归位）、`effects/`（独立视觉效果）。业务视图不得修改另一个视图的 DOM；新增交互 = 视图导出函数 + 加入该视图 `actions` 注册表（不再往 app.js 加 case）。
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
- **自定义完成标志前端（v0.4.0+，仅通用脚本弹窗「运行设置」区显示；专用脚本整块不渲染——判断脚本由插件固化，用户不可编辑）**：默认显示成功/失败关键字填写框（各独占一行 textarea，placeholder 说明「每行一组，组内逗号分隔为 AND，换行为 OR」）；底部公共操作区为「上传脚本文件」按钮（仅脚本模式显示）与「使用判断脚本（脚本优先）」**切换按钮**（`data-action="toggle-judge-mode"` + `aria-pressed`，激活态 accent 高亮，点击在关键字/脚本两区互斥切换——禁止双复选框同 id）；脚本模式显示语言下拉（JavaScript 内置引擎 / Python 系统解释器）+ 等宽代码框（placeholder 含 JSON 契约一行摘要）+ 上传按钮（`.js`/`.py` 扩展名自动识别语言并读入代码框，文件 ≤256KB）；保存校验：开启且代码为空时报错。
- **专项配置模板（v0.6.0+，v0.6.3 起目录形态）**：专项脚本 `ConfigPath` 指向独立配置文件（BetterGI=`User\OneDragon\NexusPipeline.json`，不直接使用 BetterGI 自带配置）；数据化插件 `data/config-template/` 目录直接放入**设置好的默认配置文件**；**编辑用户配置会话 start 时**（`HandleEditConfigAsync`）若 `ConfigPath` 不存在且插件提供模板目录 → `UserConfigManager.EnsureConfigForEdit` 将模板目录内容**整体复制**到配置位置（复制文件相对清单写入 `ConfigSessionMark.TemplateFiles` 随 `.session` 持久化；cancel 与重启恢复按清单精确清理，无清单回退清理 ConfigPath 单文件）；判断脚本的配置交换文件名必须与 `ConfigPath` 文件名一致（BetterGI 默认脚本 `replaceConfigs:["NexusPipeline.json"]`）。**编辑会话隐藏机制（v0.6.0+）**：专项脚本 + config 为单文件时，start 将 config 同目录下其他 `*.json` 配置（如 BetterGI 自带「默认配置.json」）移入 `data/{脚本Id}/{用户}/edit-hidden`（hideDir），使编辑目标成为唯一可选配置；done/cancel 恢复，崩溃残留由下次 start 幂等恢复（`RecoverInterrupted` 亦恢复 hideDir）。**编辑会话锁定与恢复（v0.6.0+，v0.6.6 自动结束进程）**：「配置编辑中」卡片为锁定弹窗（`showModal(..., locked)`：Esc/遮罩/× 均不可关闭，只能完成/取消）；用户管理页加载时查询 `GET /api/scripts/edit-sessions` 恢复进行中会话的锁定卡片（刷新后可继续）；**done/cancel（v0.6.6+）自动结束脚本进程并确认退出**（`KillAndConfirmExited` 按启动目标名轮询强杀，处理防崩溃自重启脚本如 BetterGI；持续自重启杀不干净 → 拒绝执行文件交换，返回 400「脚本程序无法完全退出（可能持续自重启），请先在托盘退出脚本后重试」，会话保留可原地重试，`.session` 标记由自愈/后台重试兜底）；重启后 `.session` 标记（`GeneratedTemplate`）驱动恢复——original 空时删除编辑会话生成的配置模板、hideDir 移回，还原编辑前状态；**恢复等待进程退出（v0.6.6+）**：`TryRecoverItem` 检测到脚本进程仍在运行（如「强制关闭服务 + 先启动脚本再启动服务」）时跳过全部恢复动作，记待办，进程退出后由后台重试循环自动完成恢复（避免误删/误覆盖正在使用的配置）。**配置交换形态语义**：`PrepareForRun` 仅当原配置形态为目录（`OriginalKind==Dir`）时重建目录——缺失（Missing）不建目录；`RestoreKind` 对 Missing 按**文件**还原（避免文件快照以目录形态落位成「目录/同名文件」残留）；`EnsureConfigForEdit` 对误建/残留的同名目录**递归清理**后再写模板（自愈）。**Missing 还原语义（v0.6.0+）**：`DoRestore` 在 original 空且原形态为 Missing（运行/编辑前 config 位置不存在）时，删除会话期间在 config 位置产生的文件/目录（运行生效的 store 快照、编辑模板），还原为「不存在」——删除失败保留标记交由自愈/后台重试；修复运行结束后 store 快照残留 config 位置并污染后续快照的问题。**运行收尾顺序（v0.6.0+，v0.6.5 进程树清理排除游戏）**：杀脚本进程（`KillAndConfirmExited`（v0.6.6+ 返回 bool，true=确认退出/false=持续自重启杀不干净）：进程树清理 + 轮询按名强杀直至确认退出，处理「被杀后自重启」的脚本如 BetterGI）→ 按设置处理游戏进程 → 配置交换还原，确保还原前进程已完全退出（消除文件占用导致的还原失败窗口）。**进程树清理（v0.6.5+ 自实现）**：Toolhelp 快照枚举父子关系 BFS 遍历逐进程 `taskkill /F`（替代 `taskkill /T` 全树）——**与 `GameExe` 同名的进程（脚本自启动的游戏，即使父进程是脚本）不属于脚本树**，跳过其整棵子树，生杀归游戏管理（`ForceCloseGame`/失败路径按名关闭），`ForceCloseGame=false` 时游戏不会被连带误杀；快照失败回退 `/T` 全树。**窗口处理（v0.6.0+，v0.6.5 分场景重构）**：运行脚本实例/调度队列时——脚本主窗口**最小化**（`SystemActions.MinimizeWindow`，命令行/日志已接管输出；GUI 脚本启动后窗口让位，控制台脚本无窗口自动跳过），游戏进程**统一前置**（`BringGameToFrontIfRunning`：与 `LaunchGame` 配置无关，检测到游戏进程存在即前置其窗口——v0.6.6+ 启动瞬间检测保留，并改为**运行期间轮询检测**：监控循环每轮按名检测，启动器延迟拉起的游戏出现即前置（复用 `BringToFront` 30 秒窗口覆盖「进程出现但窗口未建」），`_gameFronted` 标志保证只前置一次；游戏启动方式复杂（启动器常驻等）由脚本专门适配，宿主不重复启动游戏，`LaunchGame=true` 的宿主启动保留为用户可选能力）；编辑用户配置时——主程序窗口**前置**（用户在窗口内编辑配置）。前置实现（`SystemActions.BringToFront`）：轮询可见主窗口后组合前置——还原最小化 + `AttachThreadInput` 模拟前台线程输入（绕过 Windows 前台锁定，后台常驻服务进程直接 `SetForegroundWindow` 几乎必然失败）+ `BringWindowToTop` 置顶 + `SetForegroundWindow` 激活，失败每 1 秒重试至 30 秒超时（超时 Warn 可观测）；找不到可见窗口静默跳过。均仅前置一次，后台 fire-and-forget 且观察异常。**数据目录命名（v0.6.0+）**：`data/{脚本Id}/{用户}/` 下为 `store/`（配置快照）、`original/`（原配置暂存）、`script/`、`swap-backup/`、`edit-hidden/`、`.session`；启动恢复前 `MigrateLegacyLayout` 将旧名残留（config/cache/edit-hide/replace-backup）幂等迁移到新名，旧版本崩溃现场仍可完整恢复。
- **切换按钮（v0.5.4+，全部开关控件统一形态）**：所有开关一律 `.mode-toggle` 切换按钮（`data-action` 切换 + `aria-pressed`，激活态 accent 高亮，状态读取用 `getAttribute("aria-pressed") === "true"`）；长语义说明移入按钮旁 `muted` 小字（`.toggle-row`，`align-items: flex-end` 与按钮底部对齐）；`.toggle-row`/`.toggle-grid`/`.field-btn-row` 自带 `margin: 12px 0` 与上下内容间距一致（`panel-body` 内由 gap 管理、`margin: 0` 覆盖；modal 内 `.toggle-row` 与上方填写框统一 20px）；按钮与输入框同行时高度一致（40px，`.field-btn-row`），同一行内按钮等宽（定时卡片「启用/删除」64px、任务行 ↑/↓/删除 52px）；星期周期按钮 `.days-btn-grid` 桌面/平板 7 个等宽一行、手机（≤600px）4+3 两行；`.toggle-grid` 桌面 3 等宽一行、手机 2 列换行。
- **手机端表单间距（v0.5.4+，≤600px）**：`.form-grid`/`.row` 系列 gap 与 `.modal-body` 块间距统一 **12px**（`gap: 4px` 废弃），subsection 区域分隔保持 24px；禁止再出现同 grid 内字段间距远小于 grid 间间距的割裂。
- **select 下拉（v0.5.4+）**：保持原生 `<select>`（禁止自定义 div 下拉组件），`appearance:none` + 自定义箭头 + `option/optgroup` 背景色跟随主题（`--panel-solid`/`--text`）、选中项 `--accent-soft` 高亮、聚焦边框 accent。
- **提示文字规范（v0.5.4+）**：placeholder/label 说明采用通用路径与参数示例（不出现具体软件/插件名）；不提示配置状态（如访问令牌统一「留空=不修改」）；超长 API/契约说明不放入原生 placeholder，改弹窗内常驻 `muted` 说明（placeholder 仅一行摘要）。
- **响应式细节（v0.5.4+）**：侧边栏无关闭按钮（关闭靠遮罩点击与路由切换）；toast 手机端 `width: max-content` + `max-width: 50vw`（短文字自适应、长文字限半屏换行）。
- 粒子效果必须使用独立 `effects/particles.js`，`pointer-events:none`，默认低透明度（v0.3.6 起：粒子点 0.12 / 连线 0.05 / 数量 ≤48 / 连线距离 ≤90px）；必须响应 `prefers-reduced-motion`、页面隐藏和窗口尺寸变化，不得阻塞主业务交互。
- **测试范围分层（v0.5.4+）**：仅前端改动（wwwroot/ 与 uitest/tests 断言）→ `build.cmd` + `npx playwright test` 全量 56（免跑专项；局部迭代可按域筛选，如 `npx playwright test tests/04-schedule.spec.mjs`）；涉及后端（src/、plugins/）→ `build.cmd` + e2e 全量 + `judge-scenarios.mjs`（115）+ `chaos-queue.mjs`（171）+ `dotnet test`（58 单测），均默认跑加速档；**版本发布前**一律真实计时档全量（e2e + 专项）。新增或删除断言后同步本文件中的断言数字，并补充手机/平板/电脑至少一档回归。

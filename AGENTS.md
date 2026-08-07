# AGENTS.md

NexusPipeline（枢链）：C#/.NET 8 (net8.0-windows) WinForms 托盘 + 纯静态 Web UI（HttpListener，零前端构建）的脚本管理器。仓库公开、MIT 协议。

## 构建与测试（顺序重要）

```powershell
# 1. 构建（产物输出到 release/，不提交）
build.cmd                      # 源码在 src/，运行物在 release/

# 2. 端到端测试（headless，系统 Edge，无窗口）
$env:PLAYWRIGHT_BROWSERS_PATH = "uitest\browsers"
node uitest\test.mjs           # 149 项用例；先跑 build.cmd，否则 setupRuntime 直接中止
```

- e2e 测试自带 `uitest/runtime/` 隔离目录（复制 release 版 exe+WebRoot+plugins），**不得污染项目根**；断言数字 149（用例增减须同步更新本文件数字）。
- 测试中日期一律用 `localDate()`（本地时区）；**禁止 `new Date().toISOString()`**（UTC 日期在跨午夜时使历史/日志断言失败——曾踩坑）。
- 新建后的 UI 断言用 `waitForFunction` 轮询文本，不要立即 `textContent`（CI 慢速环境偶发时序失败）。

## Git 协作规范（强制）

- `main` 受 GitHub ruleset 保护（`current_user_can_bypass: never`）：**任何改动必须走 PR**，CI「构建 + e2e 测试」全绿后 **squash 合并**；禁止直接 push/force push main。
- 分支：`feat/`、`fix/`、`docs/`、`refactor/`、`test/`、`chore/` 前缀。
- 提交与 PR 标题：Conventional Commits，type 英文 + 描述中文（如 `fix: 修复历史详情时区错位`）。
- 版本发布：tag `vX.Y.Z` + `gh release create --prerelease`，资产打包 `dist/`（exe+WebRoot+plugins+README+LICENSE，**排除 config/**），附 SHA256。

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

## 环境陷阱（Windows PowerShell 5.1）

- `Set-Content` 会破坏 UTF-8 中文：写文件用编辑工具或 `[System.IO.File]::WriteAllText(..., [Text.Encoding]::UTF8)`。
- **0x800700E8 (ERROR_NO_DATA)：无控制台父进程启动 cmd.exe、PowerShell 等控制台程序必须带有效 stdio（CreateProcess + RedirectStandardOutput/Error=true 并消费），否则报 232**（CreateProcess 抛异常 / ShellExecute 弹「出现错误」对话框）。`BuildScriptStartInfo`、`StartVisible` 与批处理游戏启动均按此实现；**禁止**对 bat 用 UseShellExecute、禁止无重定向启动 cmd（本机曾三度踩坑）。
- `build.cmd` 与 `uitest\run-uitest.cmd` 必须保持非交互，不得加入无条件 `pause`，否则 PowerShell/CI 调用会一直等待按键。
- `git mv` 不展开通配符：`Get-ChildItem -Filter "*.cs" | ForEach-Object { git mv $_.Name "src\$($_.Name)" }`。
- `gh api --jq` 的复杂表达式（含逗号/引号）会被 PowerShell 拆参：用无空格表达式或输出 JSON 再本地处理；`gh pr create --body` 含引号/长文时改用 `--body-file`。
- 运行进程残留会锁定 `release\nexus-pipeline.exe`，重构建前先 `Get-Process nexus-pipeline | Stop-Process`。

## 主要入口

- `src/Program.cs`：CLI 分发（服务/manage/status/web/run-script/run-queue/cancel/register/unregister）+ 配置迁移。
- `src/WebServer.cs`：全部 `/api/*` 路由；`GET /api/status` 不记审计（轮询豁免）。
- `src/WebRoot/`（根目录，非 src 下）：前端 `app.js` 用 `window.__scripts/__queues` 缓存 + `data-action` 事件委托。页面结构：仪表盘首行 4 卡（脚本数/队列数/下一调度倒计时/版本）+ 插件 1/4 小卡片；插件页可进 `#/plugins/{name}` 配置二级页；脚本弹窗主程序+参数同行、三个游戏/通知复选框同行（启动游戏｜强制关闭｜发送通知，强制关闭独立于启动游戏）；**无系统选择按钮**（用户手填路径）。
- CI：`.github/workflows/ci.yml`（windows-latest，build.cmd + npm ci + e2e）。

## 前端开发强约束（v0.1.0+）

- WebRoot 必须保持零构建、零 CDN 依赖；使用原生 ES modules，浏览器直接加载 `.js` 文件，不引入需要打包步骤的框架或工具链。
- 模块边界固定为：`app.js`（启动/路由/事件委托）、`core/api.js`（请求与格式化）、`core/state.js`（单一状态与生命周期）、`core/ui.js`（通用 UI）、`views/`（页面）、`effects/`（独立视觉效果）。业务视图不得修改另一个视图的 DOM。
- 所有颜色、背景、边框、阴影、圆角、间距和层级必须使用 CSS 变量；禁止在视图模板中写 `style="..."`，禁止新增散落的颜色字面量。
- 所有页面必须在 360px 手机、768px 平板、1280px 电脑视口可用；禁止固定宽度导致溢出，密集数据必须放入横向滚动容器，表单必须允许堆叠，触控目标不得小于 40px。
- 禁止新增 inline `onclick`、`onchange` 等事件；交互统一使用 `data-action` + `app.js` 事件委托。可交互元素必须使用原生 `button`、`a`、`input`、`select` 或 `textarea`。
- e2e 依赖的节点必须提供稳定的 `data-testid`；测试不得依赖按钮顺序、随机 CSS 层级或仅依赖装饰性文案。已有业务 ID 和 `data-action` 变更时必须同步测试。
- 轮询页面必须通过 `state.js` 注册 timer 和 AbortController，并在路由切换时清理；轮询只能更新状态区域，不得覆盖用户正在编辑的表单、焦点和滚动位置。
- 表单标签必须使用 `label[for]`，必填字段使用 `required` 或等效错误提示；弹窗必须有 `role="dialog"`、`aria-modal`、标题关联、Esc 关闭、焦点陷阱和关闭后的焦点恢复；Toast 必须使用 `role="status"`/`aria-live`。
- 主题至少支持 `light`、`dark`、`system` 三种状态，选择持久化到 `localStorage`；主题切换不能改变业务数据和路由。
- 粒子效果必须使用独立 `effects/particles.js`，`pointer-events:none`，默认低透明度；必须响应 `prefers-reduced-motion`、页面隐藏和窗口尺寸变化，不得阻塞主业务交互。
- 每次前端改动必须运行 `build.cmd` 和完整 e2e；新增或删除断言后同步本文件中的断言数字，并补充手机/平板/电脑至少一档回归。

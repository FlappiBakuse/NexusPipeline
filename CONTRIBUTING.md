# 贡献指南（Contribution Guidelines）

感谢参与 NexusPipeline（枢链）开发。本文件是**协作规范**：如何提交 Issue、如何提交 PR/推送、Commit Message 格式、代码风格要求与测试流程。

- 版本发布（tag / release / 资产）见 [docs/RELEASING.md](docs/RELEASING.md)；
- 开发环境搭建与调试见 [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)；
- 版本路线与后续开发清单见 [docs/ROADMAP.md](docs/ROADMAP.md)；
- 已知问题台账见 [docs/KNOWN_ISSUES.md](docs/KNOWN_ISSUES.md)；
- 安全漏洞报告见 [SECURITY.md](SECURITY.md)。

## 目录

1. [版本与发布权](#1-版本与发布权)
2. [协作模式（以 v1.0.0 为界）](#2-协作模式以-v100-为界)
3. [如何提交 Issue](#3-如何提交-issue)
4. [如何提交代码（PR / push）](#4-如何提交代码pr--push)
5. [提交信息规范（Conventional Commits）](#5-提交信息规范conventional-commits)
6. [代码风格要求](#6-代码风格要求)
7. [测试流程（质量门禁）](#7-测试流程质量门禁)

---

## 1. 版本与发布权

- 普通贡献通过 Pull Request 提交；版本 tag、Release 与发布资产由项目维护者负责。
- 版本号变更应对应已确认的版本开发计划，并同步更新相关文档。
- 不得提交运行产物、用户配置、日志、密钥；配置与用户数据永不进入版本库。

## 2. 协作模式（以 v1.0.0 为界）

| 阶段 | 模式 |
|---|---|
| **v1.0.0 之前** | `main` 无分支保护，**直接 push main**，不走 PR；提交前先 `git pull` 避免分叉，**禁止 force push**；版本发布一律 **Pre-release** |
| **v1.0.0 起** | 所有改动**只能通过 Pull Request** 合入 `main`（CI「构建 + e2e 测试」全绿后 squash 合并），禁止直接 push / force push main |

如确需开分支协作，按前缀命名：`feat/`、`fix/`、`docs/`、`refactor/`、`test/`、`chore/`。

## 3. 如何提交 Issue

缺陷报告请包含：

1. **版本号**：`nexus-pipeline.exe status` 或 `/api/status` 的 `version` 字段（或 Release 标题）；
2. **复现步骤**：尽量具体（脚本实例配置要点、触发方式、日志路径）；
3. **现场信息**：
   - 管理器日志：`logs/nexus-pipeline-YYYY-MM-DD.log`（含审计行）；
   - 历史记录：`history/YYYY-MM-DD/` 下对应 `.json` + 按尝试分批 `.log`；
   - 配置现场（注意脱敏：`config/` 含加密密钥，请勿直接粘贴密文）。
4. **预期行为 vs 实际行为**。

功能建议请说明：场景、期望行为、与现有功能（脚本实例/调度队列/判断脚本/通知）的关系。

## 4. 如何提交代码（PR / push）

1. 从最新 `main` 创建带前缀的工作分支：`feat/`、`fix/`、`docs/`、`refactor/`、`test/` 或 `chore/`；
2. 本地验证全绿（见第 7 节）；
3. 按第 5 节规范提交，并通过 Pull Request 说明变更范围与验证结果；
4. 维护者按当前版本阶段处理合并与推送；禁止 force push；
5. 涉及发布：按 [docs/RELEASING.md](docs/RELEASING.md) 执行。

## 5. 提交信息规范（Conventional Commits）

采用 [Conventional Commits 1.0.0](https://www.conventionalcommits.org/zh-hans/v1.0.0/)，type / scope 用英文，描述用中文。

### 5.1 格式

```
<type>[<scope>][!]: <描述>

[可选 正文]          ← 空行分隔，说明变更原因与影响

[可选 脚注]          ← 空行分隔，token 用 - 连字符（如 Refs: #123）
```

- `<type>[<scope>][!]:` 后必须有英文半角冒号 + 一个空格；
- `<scope>`：圆括号内的名词，描述变更范围（见 5.3）；
- `!`：破坏性变更标记，放在冒号前（如 `feat!:`）；
- `<描述>`：中文，动词开头（新增 / 修复 / 优化 / 抽取 / 移除…），简短、不带结尾句号；
- 正文与脚注可选；正文必须起始于描述后的空行。

### 5.2 type 表

| type | 含义 | SemVer 对应 |
|---|---|---|
| `feat` | 新功能 | MINOR |
| `fix` | 缺陷修复 | PATCH |
| `docs` | 文档（README / 架构 / 规范） | 无 |
| `refactor` | 重构（不改变行为） | 无 |
| `perf` | 性能优化 | 无 |
| `test` | 测试用例增改 | 无 |
| `build` | 构建系统、依赖变更 | 无 |
| `ci` | CI 工作流变更 | 无 |
| `chore` | 杂务（版本号、脚本、工具配置） | 无 |
| `style` | 代码样式（缩进、空格、空行，不改变逻辑） | 无 |
| `revert` | 还原提交，脚注 `Refs: <被还原提交>` | 无 |

### 5.3 scope 表（可选）

| scope | 范围 |
|---|---|
| `core` | 核心域（调度、进程、日志、存储） |
| `web` | Web API / 路由 / Handler |
| `cli` | 命令行菜单 |
| `plugins` | 插件契约与插件管理器 |
| `dispatch` | 调度中心 / 队列执行 |
| `history` | 历史记录 |
| `e2e` | Playwright 端到端测试 |
| `release` | 发布流程相关（打包 / 版本号） |

### 5.4 破坏性变更

- 必须在提交信息中标记：`<type>(<scope>)!:` 或脚注 `BREAKING CHANGE: <描述>`（大写的 BREAKING CHANGE + 冒号 + 空格 + 描述）；
- 影响范围大、会破坏既有配置 / API / 契约的改动**先询问用户**再动手。

### 5.5 示例

```
feat(dispatch): 新增调度中心批量执行

fix(history): 修复历史详情时区错位
docs: 补充文件结构说明
refactor(core): 抽取运行会话状态机
test(e2e): 增加调度队列端到端用例
chore(release): 更新 build.cmd 发布脚本
feat(plugins)!: 变更插件契约签名

BREAKING CHANGE: IPlugin.Init 改为异步签名
```

## 6. 代码风格要求

### 6.1 后端（C#）

- **分层与命名空间**：`NexusPipeline`（入口/组合根）/ `Models` / `Services` / `Persistence` / `Utilities` / `Web` / `Cli` / `Plugins`；依赖方向 Models → Services → Persistence → Utilities（详见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)）。
- **public 仅限契约**：仅 `Program` 与领域模型（`AppSettings`/`ScriptInstance`/`ScriptUser`/`DispatchQueue`/`QueueTask`/`QueueTimeSet`/`RunRecord`/`RunAttempt`）为 public；其余一律 internal。
- 新 API 路由：`src/Web/` 的 `ApiXxxHandler` + 类上 `[ApiRoute("资源名")]`（子路由标在方法上），`WebServer` 反射扫描自动注册。
- 日志：一律显式调用 `Logger.Debug/Info/Warn/Error/Fatal(msg)`，禁止 `Logger.Log`。

### 6.2 前端（wwwroot，强约束）

- **零构建、零 CDN**：原生 ES modules，浏览器直接加载；不引入打包框架或外置字体。
- 模块边界固定：`app.js`（路由/注册表分发）+ `core/`（通用能力）+ `views/`（业务视图，一域一文件）+ `effects/`。
- 交互统一 `data-action` + 事件委托；禁止 inline `onclick`/`onchange`、禁止内联 `style`、颜色一律 CSS 变量。
- 主题 light/dark/system 三态 + localStorage 持久化；弹窗/Toast 无障碍（role=dialog、aria-modal、焦点陷阱、焦点恢复）。
- 响应式三档（360 / 768 / 1280 视口）；触控目标 ≥ 40px；Notion 风格基线（米色浅色系、小圆角、轻阴影、禁渐变/玻璃态/uppercase eyebrow）。
- 轮询页面必须经 `state.js` 注册 timer/AbortController，路由切换时清理。
- 完整强约束见根目录 `AGENTS.md`「前端开发强约束」——**与本文冲突时以 AGENTS.md 为准**。

## 7. 测试流程（质量门禁）

**每次改动后必须运行**，全绿方可提交：

| 改动范围 | 必跑 |
|---|---|
| 仅前端 | `build.cmd` + e2e 全量（局部迭代可按域筛选） |
| 涉及后端 | `build.cmd` + e2e 全量 + judge-scenarios + chaos-queue + 单元测试，默认加速档 |
| 版本发布前 | **真实计时档**全量（不设 `NEXUS_TIME_SCALE`） |

常用命令：

```powershell
# 1. 构建
build.cmd                                     # 提权版（增量构建：src 未变仅同步 wwwroot/plugins）

# 2. 单元测试（毫秒级，无管理员）
dotnet test tests\NexusPipeline.Tests\NexusPipeline.Tests.csproj --nologo

# 3. e2e（先 build.cmd；加速档为日常迭代默认）
Push-Location tests\e2e
$env:PLAYWRIGHT_BROWSERS_PATH = "browsers"
$env:NEXUS_TIME_SCALE = "10"
npx playwright test                            # 全量回归
$env:NEXUS_CI = "1"; npx playwright test       # CI 核心集
Remove-Item Env:NEXUS_TIME_SCALE               # 切回真实计时档

# 4. 专项测试（需管理员 shell；先 build.cmd）
$env:NEXUS_TIME_SCALE = "10"
node judge-scenarios.mjs
node chaos-queue.mjs
Pop-Location
```

- 时间加速（v0.6.4+）：唯一加速档 `NEXUS_TIME_SCALE=10`，`tests\e2e\run-e2e.cmd` 已默认内置；
- 测试数量与断言数量只记录在 CHANGELOG、Release Notes 或 CI 验证结果中；
- 发布前真实计时档全量回归 + flake 台账（`tests/e2e/FLAKE-LEDGER.md`）更新；
- 永不提交：`release/`、`config/`、`history/`、`logs/`、`tests/e2e/runtime/`、密钥与账号信息。

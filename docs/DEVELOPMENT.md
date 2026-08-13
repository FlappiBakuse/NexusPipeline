# NexusPipeline 开发与提交规范

本文件是项目开发、代码提交与版本发布的**权威规范**（依据 [Conventional Commits 1.0.0](https://www.conventionalcommits.org/zh-hans/v1.0.0/) 落地）。提交与发布操作**必须先经用户明确同意**（见「版本与发布权」），任何情况下不得擅自 commit / push / release。

## 目录

1. [版本与发布权](#1-版本与发布权)
2. [开发流程](#2-开发流程)
3. [提交信息规范](#3-提交信息规范)
4. [分支与协作模式](#4-分支与协作模式)
5. [版本号规则](#5-版本号规则)
6. [Release 分发规则](#6-release-分发规则)
7. [质量门禁](#7-质量门禁)

---

## 1. 版本与发布权

- commit / push / pull request / release 的创建与发布，必须先经用户明确同意，未经同意不得执行（含 git commit、push、gh pr、gh release、打 tag）。
- 同一版本内的多轮对话修改，全部累积为同一版本的一部分；未经用户要求，不得中途拆分提交或单独发布。
- 版本号（bump）仅随用户要求的版本开发进行，不得擅自递增。
- 不得提交运行产物、用户配置、日志、密钥；配置与用户数据永不进入版本库。

## 2. 开发流程

1. 同步最新代码：`git checkout main && git pull`（提交前必做，避免分叉）；
2. 在 `main` 上完成改动（v1.0.0 前阶段）；确需协作时开前缀分支（见第 4 节）；
3. 本地验证：`build.cmd` → 全量 e2e（`npx playwright test`，56 用例）全绿 + 单元测试（`dotnet test src\NexusPipeline.Tests\NexusPipeline.Tests.csproj`，58 断言），或先跑 CI 核心回归集（`$env:NEXUS_CI = "1"; npx playwright test`，55 用例）；专项测试 `node uitest\judge-scenarios.mjs`（115 断言）与 `node uitest\chaos-queue.mjs`（171 断言，需管理员 shell）同样应全绿。**时间加速（v0.6.4+）**：日常迭代默认加速档（`$env:NEXUS_TIME_SCALE = "10"`，`run-uitest.cmd` 已内置，e2e 全量约 3 分钟、三套合计约 10 分钟）；**推送与发布前用真实计时档**（不设 `NEXUS_TIME_SCALE`）跑全量回归，还原真实墙钟等待语义；
4. 按第 3 节规范提交（小改动一条提交，大改动分多条逻辑提交）；
5. 推送：`git push origin main`（禁止 force push）；
6. 如需发布：按第 5、6 节执行。

## 3. 提交信息规范

采用 **Conventional Commits 1.0.0**，type / scope 用英文，描述用中文。

### 3.1 格式

```
<type>[<scope>][!]: <描述>

[可选 正文]          ← 空行分隔，说明变更原因与影响

[可选 脚注]          ← 空行分隔，token 用 - 连字符（如 Refs: #123）
```

- `<type>[<scope>][!]:` 后必须有英文半角冒号 + 一个空格；
- `<scope>`：圆括号内的名词，描述变更范围（见 3.3）；
- `!`：破坏性变更标记，放在冒号前（如 `feat!:`）；
- `<描述>`：中文，动词开头（新增 / 修复 / 优化 / 抽取 / 移除…），简短、不带结尾句号；
- 正文与脚注可选；正文必须起始于描述后的空行。

### 3.2 type 表

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

- `feat` / `fix` 之外的类型均可携带 `!` 表示破坏性变更；
- 一次提交尽量聚焦单一目的；若一次改动符合多种类型，拆分为多条提交。

### 3.3 scope 表（可选）

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

### 3.4 破坏性变更

- 必须在提交信息中标记：`<type>(<scope>)!:` 或脚注 `BREAKING CHANGE: <描述>`（大写的 BREAKING CHANGE + 冒号 + 空格 + 描述）；
- 影响范围大、会破坏既有配置 / API / 契约的改动**先询问用户**再动手。

### 3.5 示例

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

## 4. 分支与协作模式

以 **v1.0.0 为界**：

- **v1.0.0 之前（早期开发阶段）**：`main` 无分支保护，直接 push main，不走 PR；提交前先 `git pull`，**禁止 force push**；版本发布一律 **Pre-release**。
- **v1.0.0 起（正式版本）**：所有改动**只能通过 Pull Request** 合入 `main`（CI「构建 + e2e 测试」全绿后 squash 合并），禁止直接 push / force push main。

如确需开分支协作，按前缀命名：

| 前缀 | 用途 | 示例 |
|---|---|---|
| `feat/` | 新功能 | `feat/dispatch-center` |
| `fix/` | 缺陷修复 | `fix/history-timezone` |
| `docs/` | 文档 | `docs/readme-license` |
| `refactor/` | 重构 | `refactor/session-state` |
| `test/` | 测试 | `test/e2e-queues` |
| `chore/` | 构建、依赖、杂务 | `chore/release-script` |

## 5. 版本号规则

- 采用 SemVer `X.Y.Z`，tag 为 `vX.Y.Z`（纯版本号，无其他前缀）；
- 版本增量映射：

| 提交类型 | 版本增量 |
|---|---|
| `fix`（含 perf、docs 等非新功能） | PATCH（+1 到 Z） |
| `feat` | MINOR（+1 到 Y） |
| BREAKING CHANGE（任何类型带 `!`） | v1.0.0 前：MINOR（+1 到 Y）；v1.0.0 起：MAJOR（+1 到 X） |

- v1.0.0 之前所有版本发布均标记 **Pre-release**；
- 版本号 bump 仅随用户要求的版本开发进行，不得擅自递增。

## 6. Release 分发规则

### 6.1 总览

| 项目 | 规则 |
|---|---|
| tag | `vX.Y.Z`（如 `v0.3.1`） |
| release 标题 | `vX.Y.Z`（纯版本号） |
| pre-release 标记 | v1.0.0 前一律 `--prerelease` |
| 更新内容格式 | 参考 v0.3.1（分组要点式，见 6.3） |
| zip 资产 | `NexusPipeline-vX.Y.Z-win-x64.zip` |
| SHA 资产 | `NexusPipeline-vX.Y.Z-win-x64.zip.sha256`（与 zip 同名成对，内容纯 hash） |

### 6.2 资产内容与 SHA 规则

- zip 打包内容：exe + wwwroot + plugins + README + LICENSE，**排除 config/**（用户配置永不打包）；
- SHA 文件**必须遵守 v0.2.1 及之前的规则**：
  - 与 zip 资产**同名成对**上传：`NexusPipeline-vX.Y.Z-win-x64.zip.sha256`；
  - 文件内容为**纯 hash**，**不含文件名**、不含空格，UTF-8 无 BOM；
  - 禁止使用 v0.3.1 的 `SHA256.txt` 汇总格式（`hash 文件名` 双列）。
- 生成方式示例（PowerShell）：

```powershell
$zip = "NexusPipeline-v0.3.2-win-x64.zip"
Get-FileHash $zip -Algorithm SHA256 | ForEach-Object { $_.Hash.ToLower() } |
    Set-Content -Path "$zip.sha256" -Encoding ascii -NoNewline
```

### 6.3 Release Notes 格式（参考 v0.3.1）

```
## vX.Y.Z（Pre-release）

### 功能分组标题
- 要点一
- 要点二

### 另一个分组
- 要点一

SHA256：见附件 NexusPipeline-vX.Y.Z-win-x64.zip.sha256
```

- 第一行：`## vX.Y.Z（Pre-release）`（v1.0.0 起为 `## vX.Y.Z`）；
- 按功能分组使用 `### 标题` + 无序要点列表，不用面面俱到的逐条罗列提交；
- 结尾注明 SHA 见附件。

### 6.4 发布流程

1. 确认本地构建与测试全绿（见第 7 节）；
2. 按用户要求的版本号完成版本 bump 提交并推送；
3. **文档一致性自检（v0.6.2 起）**：全文检索旧语义关键词（如「固化标志」「插件标志」「0.0.0.0」「StarRailAssistant」「三模式」），确认文档表述与当前实现一致（判定语义以 `docs/DESIGN.md` §5 为唯一权威，README/AGENTS/plugins-README 只做简引）；
4. 打 tag：`git tag vX.Y.Z` → `git push origin vX.Y.Z`（发布操作先经用户同意）；
5. 编写 release notes 到临时文件（`gh pr create --body` 引号坑同理，用 `--notes-file`）；
6. `gh release create vX.Y.Z --prerelease --title vX.Y.Z --notes-file <file>`；
7. 上传资产：`gh release upload vX.Y.Z NexusPipeline-vX.Y.Z-win-x64.zip NexusPipeline-vX.Y.Z-win-x64.zip.sha256`；
8. 校验：`Get-FileHash` 与 `.sha256` 内容一致；下载 zip 重新计算复核。

## 7. 质量门禁

- 每次改动后必须运行 `build.cmd` 与全量 e2e（`npx playwright test`，56 用例）与单元测试（`dotnet test src\NexusPipeline.Tests\NexusPipeline.Tests.csproj`，58 断言），全绿方可提交；
- 开发迭代快速验证可用 CI 核心回归集（`$env:NEXUS_CI = "1"; npx playwright test`，55 用例，剔除响应式外壳外观用例），并默认使用时间加速档（`NEXUS_TIME_SCALE=10`）；**推送与发布前必须真实计时档全量**（不设 `NEXUS_TIME_SCALE`）；
- 专项稳定性测试：`node uitest\judge-scenarios.mjs`（115 断言）与混沌压力测试 `node uitest\chaos-queue.mjs`（171 断言，需管理员 shell），发布前一并运行（加速档为日常迭代档位）；
- 新增或删除测试用例后，同步更新 AGENTS.md 中的断言数字；新增依赖真实墙钟的用例须同时提供加速档与真实档两档实现（见 AGENTS.md「加速档测试契约」）；
- 永不提交：`release/`、`config/`、`history/`、`logs/`、`uitest/runtime/`、密钥与账号信息（含 DPAPI 加密值）。

# NexusPipeline 发布流程手册（Releasing）

本文件是版本发布（tag / release / 资产）的操作手册，供维护者使用。开发环境搭建见 [DEVELOPMENT.md](DEVELOPMENT.md)；协作规范见 [CONTRIBUTING.md](../CONTRIBUTING.md)；版本路线见 [ROADMAP.md](ROADMAP.md)。

> **发布权（最高优先级）**：commit、push、tag、Pull Request 和 Release 由项目维护者按根目录 `AGENTS.md` 授权规则执行。未经维护者授权不得发布。

## 目录

1. [版本号规则](#1-版本号规则)
2. [发布前置](#2-发布前置)
3. [发布流程](#3-发布流程)
4. [资产与 SHA 规则](#4-资产与-sha-规则)
5. [Release Notes 格式](#5-release-notes-格式)
6. [gh/PowerShell 中文操作三坑](#6-ghpowershell-中文操作三坑)
7. [发布后收尾](#7-发布后收尾)

---

## 1. 版本号规则

- 采用 SemVer `X.Y.Z`，tag 为 `vX.Y.Z`（纯版本号，无其他前缀）。
- 版本增量映射：

| 提交类型 | 版本增量 |
|---|---|
| `fix`（含 perf、docs 等非新功能） | PATCH（+1 到 Z） |
| `feat` | MINOR（+1 到 Y） |
| BREAKING CHANGE（任何类型带 `!`） | v1.0.0 前：MINOR（+1 到 Y）；v1.0.0 起：MAJOR（+1 到 X） |

- v1.0.0 之前所有版本发布均标记 **Pre-release**；版本号 bump 仅随用户要求的版本开发进行，不得擅自递增。
- **版本号同步（强约束）**：用户给出新版本号并开始该版本开发时，**开工即同步** `src/NexusPipeline.csproj` 的 `<Version>`（与版本展示相关配置），发布流程中不再单独改（除非发布时发现遗漏）。

## 2. 发布前置

1. 确认本地构建与测试全绿：
   - `build.cmd`（提权版）；
    - 单元测试 `dotnet test tests\NexusPipeline.Tests\NexusPipeline.Tests.csproj --nologo`；
   - UI Smoke 全量回归；涉及进程、端口、解释器、模拟器或插件边界时运行管理员 System Smoke。
   - Stress/Chaos 根据本版本风险选择运行，结果记录在验证记录中；不作为所有版本的固定硬门禁。
2. **文档一致性自检（v0.6.2+）**：全文检索旧语义关键词（如「固化标志」「插件标志」「0.0.0.0」「StarRailAssistant」「三模式」），确认文档表述与当前实现一致（判定语义以 `docs/DESIGN.md` §5 为唯一权威，README/AGENTS/plugins-README 只做简引）。
3. 核对 ROADMAP 勾选状态与 KNOWN_ISSUES 台账（本版应修项状态）。

## 3. 发布流程

1. 按已授权的版本计划完成版本 bump 提交并推送（提交信息见 CONTRIBUTING.md）；
2. 打 tag：`git tag vX.Y.Z` → `git push origin vX.Y.Z`（按根目录 `AGENTS.md` 的发布权规则执行）；
3. 编写 release notes 到临时文件（`gh release create` 引号坑，用 `--notes-file`）；
4. `gh release create vX.Y.Z --prerelease --title vX.Y.Z --notes-file <file>`（v1.0.0 起不加 `--prerelease`）；
5. 上传资产：
   ```
   gh release upload vX.Y.Z NexusPipeline-vX.Y.Z-win-x64.zip NexusPipeline-vX.Y.Z-win-x64.zip.sha256
   ```
6. 校验：`Get-FileHash` 与 `.sha256` 内容一致；下载 zip 重新计算复核。

## 4. 资产与 SHA 规则

| 项目 | 规则 |
|---|---|
| tag | `vX.Y.Z`（如 `v0.10.0`） |
| release 标题 | `vX.Y.Z`（纯版本号） |
| pre-release 标记 | v1.0.0 前一律 `--prerelease` |
| zip 资产 | `NexusPipeline-vX.Y.Z-win-x64.zip` |
| SHA 资产 | `NexusPipeline-vX.Y.Z-win-x64.zip.sha256`（与 zip 同名成对，内容纯 hash） |

- **zip 布局（v0.10.0 起标准化，更新引擎可见性依赖此约定）**：扁平根 = `nexus-pipeline.exe` + `wwwroot/` + `plugins/` + `README` + `LICENSE`，**排除 `config/`、`data/`、`history/`、`logs/`**（用户配置与运行数据永不打包）；更新引擎同时兼容「包内单个顶层目录」形态（自动归一），拒绝含绝对路径 / `..` / 重复目录的条目。
- SHA 文件内容为**纯 hash**，**不含文件名**、不含空格，UTF-8 无 BOM；禁止 `SHA256.txt` 汇总格式（v0.3.1 错误先例）。
- 生成方式示例（PowerShell）：

```powershell
$zip = "NexusPipeline-v0.10.0-win-x64.zip"
Get-FileHash $zip -Algorithm SHA256 | ForEach-Object { $_.Hash.ToLower() } |
    Set-Content -Path "$zip.sha256" -Encoding ascii -NoNewline
```

### 更新引擎可见性自检（v0.10.0+，每次发布后必做）

- 每次 `gh release create --prerelease` 必须**同时上传 zip 与 sha256**，且都上传完成后更新引擎才能发现新版本（缺失任一资产会被清单过滤跳过）；
- 上传并校验后，在本机服务设置页点击「检查更新」（或 `POST /api/update/check`）应返回 `available=true` 且最新版本为刚发布的 `vX.Y.Z`；
- 若检查不到：先核对资产是否齐全（`gh release view vX.Y.Z` 的 assets 列表），再核对 zip 布局（flat root 是否含 `nexus-pipeline.exe` 与 `wwwroot/`）。

## 5. Release Notes 格式

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

## 6. gh/PowerShell 中文操作三坑

（曾踩坑，修复 release 时中招——严格遵守）

1. **多行输出被拆成数组**：`gh api ... --jq .body` 多行输出被 PowerShell 5.1 捕获为 `string[]`，`[IO.File]::WriteAllText(路径, 数组)` 会用空格连接、换行全部丢失。必须先 `[Console]::OutputEncoding = UTF8`，并用 `($body -join "`n")` 显式转字符串。
2. **GBK 误读中文**：未设 UTF8 输出编码时 gh 的 UTF-8 中文被 GBK 误读（mojibake），且经 GB18030 往返**有损不可逆**；含中文的 gh 写操作一律走文件：`gh release edit --notes-file`（UTF-8 无 BOM），命令内不写中文字面量。
3. **修改已发布 release 前先备份**：edit body / 资产前，先 `gh api ... --jq .body` 把原正文备份到本地文件，再动手。

## 7. 发布后收尾

- 更新 [KNOWN_ISSUES.md](KNOWN_ISSUES.md) 台账（本版修复项状态 → 已修复）；
- 更新 [ROADMAP.md](ROADMAP.md)（版本状态勾选）；
- 更新 `tests/e2e/FLAKE-LEDGER.md`（发布前回归的 flake 记录）；
- 确认 CHANGELOG.md 已含本版条目（Keep a Changelog 规范，见 [CHANGELOG.md](../CHANGELOG.md)）。

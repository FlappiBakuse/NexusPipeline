# NexusPipeline 发布流程手册（Releasing）

本文件只说明版本发布、tag、Release、发布资产和发布后收尾。开发环境与调试见 [DEVELOPMENT.md](DEVELOPMENT.md)，测试层级与完整门禁见 [TESTING.md](TESTING.md)，协作规范见 [CONTRIBUTING.md](../CONTRIBUTING.md)，版本路线见 [ROADMAP.md](ROADMAP.md)。

> **发布权**：commit、push、tag、Pull Request 和 Release 由项目维护者按根目录 `AGENTS.md` 授权规则执行。未经明确授权不得发布。

## 目录

1. [版本号规则](#1-版本号规则)
2. [发布前置](#2-发布前置)
3. [发布流程](#3-发布流程)
4. [资产与 SHA 规则](#4-资产与-sha-规则)
5. [Release Notes 格式](#5-release-notes-格式)
6. [gh 与 PowerShell 操作注意事项](#6-gh-与-powershell-操作注意事项)
7. [发布后收尾](#7-发布后收尾)

## 1. 版本号规则

- 采用 SemVer `X.Y.Z`，tag 为 `vX.Y.Z`。
- `fix`、`perf` 和文档/工程治理的补丁性变更使用 PATCH；`feat` 使用 MINOR；带 `!` 或 `BREAKING CHANGE` 的变更按项目当前阶段升级。
- v1.0.0 之前所有版本发布均标记 Pre-release；v1.0.0 起按正式版本规则发布。
- 用户指定新版本并开始开发后，立即同步 `src/NexusPipeline.csproj` 的 `<Version>` 和版本展示所需配置；发布流程不重复 bump。
- 版本开发期间的本地 `backup/vX.Y.Z-*` 还原点只存在本地，不推送到 origin。

## 2. 发布前置

1. 确认版本开发计划、CHANGELOG、ROADMAP 和 KNOWN_ISSUES 已反映当前状态；
2. 按 [TESTING.md](TESTING.md) 执行默认质量门禁，并运行修改范围适用的 System Smoke、Stress 或 Soak；
3. 确认 `git diff --check` 通过，工作树中没有运行产物、用户配置、日志、密钥和测试 runtime；
4. 核对发布包只包含程序运行所需文件，用户配置和运行数据不进入资产；
5. 确认 Release Notes 使用当前版本的真实变更，SHA 资产与 zip 一一对应。

## 3. 发布流程

以下步骤需要维护者明确授权：

1. 完成版本开发并获得全部适用质量门禁结果；
2. 按协作策略提交并推送版本变更；
3. 创建 tag：`git tag vX.Y.Z`，再按授权推送 `git push origin vX.Y.Z`；
4. 将 Release Notes 写入 UTF-8 无 BOM 临时文件；
5. v1.0.0 前执行：

   ```text
   gh release create vX.Y.Z --prerelease --title vX.Y.Z --notes-file <file>
   ```

6. 上传 zip 与 SHA 资产；
7. 在本地校验 SHA，并下载 Release 资产重新计算复核；
8. 在设置页或更新 API 执行一次更新可见性检查，确认新版本和两项资产均被识别。

## 4. 资产与 SHA 规则

| 项目 | 规则 |
|---|---|
| tag | `vX.Y.Z` |
| Release 标题 | `vX.Y.Z` |
| Pre-release | v1.0.0 前使用 `--prerelease` |
| zip 资产 | `NexusPipeline-vX.Y.Z-win-x64.zip` |
| SHA 资产 | `NexusPipeline-vX.Y.Z-win-x64.zip.sha256` |

发布包采用扁平根布局：

```text
nexus-pipeline.exe
wwwroot/
plugins/
  .nxp-root
README.md
LICENSE
```

包内 `plugins/.nxp-root` 仅用于兼容旧版本更新器。主程序更新引擎只交换 `nexus-pipeline.exe` 和 `wwwroot/`，不会覆盖运行时 `plugins/`。包内排除 `config/`、`data/`、`history/` 和 `logs/`。更新引擎兼容包内单个顶层目录形态，并拒绝绝对路径、`..` 路径和重复目录条目。

SHA 文件内容为纯 hash，不含文件名和空格，使用 UTF-8 无 BOM。PowerShell 示例：

```powershell
$zip = "NexusPipeline-vX.Y.Z-win-x64.zip"
Get-FileHash $zip -Algorithm SHA256 | ForEach-Object { $_.Hash.ToLower() } |
    Set-Content -Path "$zip.sha256" -Encoding ascii -NoNewline
```

### 更新引擎可见性自检

- Release 必须同时具备 zip 与 sha256 资产；缺少任一项时更新清单会跳过该版本；
- 上传后在本机设置页点击“检查更新”，或调用 `POST /api/update/check`，确认 `available=true` 且版本为刚发布的 tag；
- 如果检查不到，先核对 `gh release view vX.Y.Z` 的资产列表、资产命名和 zip 根布局。

## 5. Release Notes 格式

```text
## vX.Y.Z（Pre-release）

### 功能分组标题
- 要点一
- 要点二

### 另一个分组
- 要点一

SHA256：见附件 NexusPipeline-vX.Y.Z-win-x64.zip.sha256
```

按用户价值或工程主题分组，列出可核对的结果。版本历史的完整记录进入 [CHANGELOG.md](../CHANGELOG.md)。

## 6. gh 与 PowerShell 操作注意事项

1. 修改已发布 Release 的正文或资产前，先通过 `gh api` 备份原正文到本地文件；
2. 多行 gh 输出在 PowerShell 中可能成为字符串数组，写入文件前显式合并换行；
3. 含中文的 Release Notes 使用 UTF-8 无 BOM 文件和 `--notes-file`，避免命令行转义与编码转换；
4. 修改已发布 Release 属于外部状态变更，先确认授权和目标版本。

## 7. 发布后收尾

- 将发布版本的已知问题状态同步到 [KNOWN_ISSUES.md](KNOWN_ISSUES.md)，并从 [ROADMAP.md](ROADMAP.md) 移出已完成计划；
- 确认远端 Release 资产上传成功、下载复核和 SHA256 校验全部通过；
- 完成确认后，清理项目内本次发布的 zip、`.sha256`、Release Notes 临时文件和打包暂存目录；
- 清理仅针对当前项目内已核对的精确路径，不删除源码、测试、插件、用户运行数据或后续开发所需目录；
- 备份 tag 只保留最近三个版本的现存里程碑，删除旧 tag 前先核对保留清单和删除清单。

# NexusPipeline 开发手册（环境、调试、协作与发布）

本文件面向开发者，说明源码构建、运行方式、调试技巧、运行时数据、协作规范和发布流程。测试层级与完整命令见 [TESTING.md](TESTING.md)；核心设计见 [DESIGN.md](DESIGN.md)；模块导航见 [DESIGN.md](DESIGN.md) 第 10 节。

> 使用 AI 工具参与开发时，操作级约束以根目录 `AGENTS.md` 为准。本文件提供环境、协作和发布背景，不复制产品语义或完整测试命令。

## 目录

1. [环境要求](#1-环境要求)
2. [从源码编译](#2-从源码编译)
3. [运行程序](#3-运行程序)
4. [测试入口](#4-测试入口)
5. [调试技巧](#5-调试技巧)
6. [运行时数据](#6-运行时数据)
7. [常见故障排查](#7-常见故障排查)
8. [协作与提交规范](#8-协作与提交规范)
9. [发布流程](#9-发布流程)

## 1. 环境要求

| 依赖 | 版本 | 用途 |
|---|---|---|
| Windows | 10/11 | 唯一支持平台（WinForms 托盘 + Win32 API） |
| .NET SDK | 8.x | 编译与运行；部署机需要 .NET 8 Desktop Runtime |
| Node.js | 20.x | Web Logic、System Smoke 和 Playwright 测试 |

- 网页管理界面为纯静态 ES modules，浏览器直接加载；源码构建不需要前端打包链。
- 正式程序以管理员身份运行，构建产物带 `requireAdministrator` 清单；Codex 本地 UI/System 反馈使用 `NexusTestHost=true` 的 Test Host，GitHub Administrator Gate 使用生产 release 并在 Administrator / High Integrity 或 System Integrity 下执行。
- `tests/e2e/` 已声明 Playwright 依赖；安装和运行方式见 [TESTING.md](TESTING.md)。

## 2. 从源码编译

在项目根目录执行：

```text
build.cmd
```

产物输出到隔离的 `release/` 目录：

```text
release/
├── nexus-pipeline.exe   ← 框架依赖的单文件、requireAdministrator
├── wwwroot/              ← 纯静态网页
└── plugins/              ← 用户插件运行目录（由插件管理器维护）
```

构建脚本由 `build.cmd` 调用 `tools/source-hash.mjs` 计算宿主 `src/` 的源码指纹，并排除构建产生的 `bin/`、`obj/`；插件实现由独立的 `NexusPipeline-Plugins` 仓库打包。`release/` 属于运行产物，不提交到版本库。

重构建前若提示 exe 被占用，确认没有正在运行的服务进程后执行：

```cmd
taskkill /IM nexus-pipeline.exe /T /F
```

需要指定参数时可使用等价的 .NET 发布命令：

```text
dotnet publish src\NexusPipeline.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o release
```

## 3. 运行程序

| 命令 | 行为 |
|---|---|
| `release\nexus-pipeline.exe` | 常驻服务模式：托盘、Web 和调度器 |
| `release\nexus-pipeline.exe web` | 网页模式；按回车或在 stdin 结束时退出 |
| `release\nexus-pipeline.exe manage` | 交互式命令行管理菜单 |
| `release\nexus-pipeline.exe status` | 查看当前状态 |
| `release\nexus-pipeline.exe run script ...` / `run queue ...` / `run cancel ...` | 经常驻服务 HTTP 通道提交或取消任务 |
| `release\nexus-pipeline.exe register` / `unregister` | 注册或取消开机自启动任务 |

网页默认地址为 `http://127.0.0.1:58731/`；端口被占用时按顺序寻找可用端口。首次运行会创建当前运行时目录并执行崩溃恢复扫描。

## 4. 测试入口

测试分层、归属、默认命令、CI 顺序、System Smoke 和清理要求统一见 [TESTING.md](TESTING.md)。统一入口为 `node tests/run.mjs codex <suite>` 或 `node tests/run.mjs admin <suite>`；每次改动按照修改范围执行对应模式；涉及进程、端口、解释器、模拟器、插件或更新事务时，先运行 Codex System Smoke，再由 GitHub Administrator Gate 验证生产 release。

## 5. 调试技巧

### 5.1 日志

- 管理器日志：`logs/nexus-pipeline-YYYY-MM-DD.log`，包含级别、来源和审计行。
- 脚本日志：`history/YYYY-MM-DD/<用户昵称>/<HH-mm-ss>/<HH-mm-ss>-{attempt}.log`，按运行目录和 Attempt 分批保存；同一秒的运行目录按 `-2`、`-3` 递增。
- `LogLevel=debug` 可以查看 Web 请求级日志；`GET /api/status` 轮询不记录。
- 判断脚本异常时，结合管理器日志中的 JudgeError、历史状态文件和对应尝试日志定位。

### 5.2 测试钩子

以下环境变量只用于测试或调试：

| 环境变量 | 作用 |
|---|---|
| `NEXUS_TIME_SCALE` | 缩放宿主等待时长；判断脚本单次 30 秒上限保持真实墙钟语义 |
| `NEXUS_SYSTEM_ACTION_DRYRUN=1` | 记录休眠、重启或关机请求，不执行真实系统操作 |
| `NEXUS_SYSTEM_SMOKE=1` | 启用通过统一 runner 选择的 System Smoke suite |
| `NEXUS_TEST_MODE=codex` | 选择本地 Test Host 反馈语义，由统一 runner 设置 |
| `NEXUS_TEST_MODE=admin` | 选择生产 Administrator Gate 语义，由统一 runner 设置 |
| `NEXUS_PLUGIN_CATALOG_URL` | 将插件 catalog 指向本地测试源；生产环境不设置 |

### 5.3 Windows 环境注意事项

- 控制台、管道和文件使用 UTF-8；批处理和中文文件操作应保持无 BOM 的 UTF-8。
- 无控制台父进程启动 cmd/bat 时必须提供并消费重定向的 stdout/stderr；构建和测试脚本保持非交互，不加入无条件 `pause`。
- 正式脚本运行和 Administrator UI/System Gate 必须在管理员上下文中完成；Codex UI/System 反馈使用 Test Host。每个 suite 使用隔离 runtime 验证脚本与解释器边界；目标程序返回 Win32Exception 740 时应明确失败，保留正式运行边界。
- 以显式路径开头的 `Args` 表示运行时启动目标，`?` 后为目标参数；Args 不使用引号表达路径。
- 使用 `cmd.exe` 运行批处理时，注意工作目录和环境变量继承；运行进程残留会锁定 `release\nexus-pipeline.exe`。

### 5.4 单元与组件测试定位

工程位于 `tests/NexusPipeline.Tests/`，通过 `InternalsVisibleTo` 覆盖宿主内部契约。常见定位方向包括：

- `SessionJudge`、`KeywordRule`：完成判定和关键字规则；
- `LogPattern`、`LogMonitor`：日志路径解析和增量读取；
- `UserNameRule`、`QueueRule`：模型约束；
- `ConfigSwapSyncTests`：快照同步、还原描述和事务镜像；
- `ProcessTreeTests`、执行准入和更新测试：进程清理、资源租约和更新状态机。

## 6. 运行时数据

| 位置 | 内容 |
|---|---|
| `config/settings.json`、`scripts.json`、`config/judge-scripts/`、`queues.json` | 用户配置、脚本声明、通用判断脚本资产和队列数据，永不提交 |
| `config/limits.json` | 约束配置 |
| `config/plugins/` | managed-code 插件配置和 DPAPI 密钥 |
| `history/YYYY-MM-DD/<用户昵称>/<运行目录>/` | 运行状态 JSON、按 Attempt 分批的脚本日志和当前保留截图 |
| `logs/` | 管理器日志 |
| `data/{脚本Id}/{UserId}/` | 配置交换快照、恢复标记、脚本目录和临时事务 |
| `.nxp/runtime/` | `service.pid`、`web.port` 等可重建运行标记 |
| `.nxp/state/` | `scheduler-state.json` 等需要跨重启保留的内部运行状态 |
| `.nxp/state/plugins/` | 插件仓库 catalog 缓存、商店归属、待重启事务以及 staging/backup 操作现场 |

`.nxp-update/`、`.nxp-backup/`、`.nxp-version` 和根目录 update worker 属于更新事务协议，继续留在安装根目录；它们与 `.nxp/` 当前运行状态目录职责分离。

磁盘 JSON 使用 PascalCase，Web API 返回 camelCase。测试和调试应使用隔离 runtime，不能把运行时数据写入项目根目录。

## 7. 常见故障排查

| 现象 | 排查方向 |
|---|---|
| 启动即退出 | 正式程序确认管理员上下文；管理员测试检查隔离 runtime、启动日志和 exit code |
| 检测到已在运行 | 检查任务管理器中的残留进程；确认单实例互斥体没有被其他服务占用 |
| Web 打不开 | 确认服务正在运行、端口正确，轻量模式不会启动 Web |
| 重构建失败或 exe 被锁定 | 停止对应服务进程后重新构建 |
| 测试出现级联失败 | 检查对应 suite 的隔离 runtime 和残留进程，按 [TESTING.md](TESTING.md) 清理 |
| 配置还原异常 | 检查 `data/{脚本Id}/{UserId}/` 下的 `.session`、`original/` 和 `swap-backup/`，保留现场后再进行恢复操作 |

## 8. 协作与提交规范

### 8.1 版本与发布权

- 版本 tag、Release 与发布资产由项目维护者负责；未经明确授权，不执行 commit、push、tag、Pull Request 或 Release（按项目规约创建的本地开发备份 tag 除外）。
- 版本号变更对应已确认的版本开发计划；用户指定新版本并开始开发后，立即同步项目版本配置。
- 架构重构、新功能和破坏性版本开工前创建本地开发基线备份 tag；备份 tag 只保留在本地，不推送到 origin。
- 不得提交运行产物、用户配置、日志、密钥；配置与用户数据永不进入版本库。

### 8.2 分支策略

| 参与者或阶段 | 提交路径 |
|---|---|
| 外部贡献者 | fork 或工作分支 → Pull Request |
| v1.0.0 之前的项目维护者 | 按当前主分支策略直接 push `main`，提交前先同步远端，禁止 force push |
| v1.0.0 起的项目维护者 | 工作分支 → Pull Request；CI 全绿后 squash 合入 `main`，禁止直接 push 或 force push |

如需开分支，使用 `feat/`、`fix/`、`docs/`、`refactor/`、`test/` 或 `chore/` 前缀。版本发布在 v1.0.0 前统一标记为 Pre-release。

### 8.3 提交信息

采用 [Conventional Commits 1.0.0](https://www.conventionalcommits.org/zh-hans/v1.0.0/)，type 和 scope 使用英文，描述使用中文。

```text
<type>[<scope>][!]: <描述>

[可选正文]

[可选脚注]
```

- 冒号后使用一个空格；`<scope>` 使用圆括号，可省略；`!` 放在冒号前表示破坏性变更；
- 描述以动词开头，简短说明结果，不加句号；正文和脚注用空行分隔。

| type | 含义 |
|---|---|
| `feat` | 新功能 |
| `fix` | 缺陷修复 |
| `docs` | 文档与规范 |
| `refactor` | 不改变行为的重构 |
| `perf` | 性能优化 |
| `test` | 测试用例增改 |
| `build` | 构建系统与依赖 |
| `ci` | CI 工作流 |
| `chore` | 版本、脚本与工具配置 |
| `style` | 不改变逻辑的代码样式 |
| `revert` | 还原提交，并在脚注注明被还原提交 |

示例：

```text
feat(dispatch): 新增调度中心批量执行
fix(history): 修复历史详情时区错位
refactor(core): 抽取运行会话状态机
```

破坏性变更使用 `feat(scope)!:` 或在脚注写明 `BREAKING CHANGE: 说明迁移方式和兼容性影响`。涉及既有 API、配置格式、磁盘布局或 Plugin API 契约的变化，先说明迁移方案并取得维护者确认。

### 8.4 文档治理

一个主题只保留一份完整规则，其他地方用摘要和链接；evergreen 文档不记录已完成版本的流水账、旧验证数字和「当前最新 vX.Y」矩阵。发现代码、测试和 DESIGN 对产品行为的描述不一致时：先确认当前实现与回归测试，再判断 DESIGN 是 intended contract 还是陈旧描述；需要改变产品行为时停止文档范围内的自动修改，向维护者报告并等待决定。

## 9. 发布流程

> **发布权**：commit、push、tag、Pull Request 和 Release 由项目维护者按根目录 `AGENTS.md` 授权规则执行。未经明确授权不得发布。

### 9.1 版本号规则

- 采用 SemVer `X.Y.Z`，tag 为 `vX.Y.Z`；`fix`、`perf` 和文档/工程治理的补丁性变更使用 PATCH，`feat` 使用 MINOR，带 `!` 或 `BREAKING CHANGE` 的变更按项目当前阶段升级。
- v1.0.0 之前所有版本发布均标记 Pre-release；v1.0.0 起按正式版本规则发布。
- 用户指定新版本并开始开发后，立即同步 `src/NexusPipeline.csproj` 的 `<Version>` 和版本展示所需配置；发布流程不重复 bump。
- 版本开发期间的本地 `backup/vX.Y.Z-*` 还原点只存在本地，不推送到 origin。

### 9.2 发布前置

1. 确认版本开发计划、CHANGELOG 与 `docs/STATUS.md` 已反映当前状态；
2. 按 [TESTING.md](TESTING.md) 执行默认质量门禁，并运行修改范围适用的 System Smoke、Stress 或 Soak；
3. 确认 `git diff --check` 通过，工作树中没有运行产物、用户配置、日志、密钥和测试 runtime；
4. 核对发布包只包含程序运行所需文件，用户配置和运行数据不进入资产；
5. 确认 Release Notes 使用当前版本的真实变更，SHA 资产与 zip 一一对应。

### 9.3 发布步骤

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

### 9.4 资产与 SHA 规则

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
README.md
LICENSE
```

主程序更新引擎只交换 `nexus-pipeline.exe` 和 `wwwroot/`，不会覆盖运行时 `plugins/`。包内排除 `config/`、`data/`、`history/` 和 `logs/`。更新引擎支持当前发布包布局，并拒绝绝对路径、`..` 路径和重复目录条目。

SHA 文件内容为纯 hash，不含文件名和空格，使用 UTF-8 无 BOM。PowerShell 示例：

```powershell
$zip = "NexusPipeline-vX.Y.Z-win-x64.zip"
Get-FileHash $zip -Algorithm SHA256 | ForEach-Object { $_.Hash.ToLower() } |
    Set-Content -Path "$zip.sha256" -Encoding ascii -NoNewline
```

更新引擎可见性自检：

- Release 必须同时具备 zip 与 sha256 资产；缺少任一项时更新清单会跳过该版本；
- 上传后在本机设置页点击「检查更新」，或调用 `POST /api/update/check`，确认 `available=true` 且版本为刚发布的 tag；
- 如果检查不到，先核对 `gh release view vX.Y.Z` 的资产列表、资产命名和 zip 根布局。

### 9.5 Release Notes 格式

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

### 9.6 gh 与 PowerShell 操作注意事项

1. 修改已发布 Release 的正文或资产前，先通过 `gh api` 备份原正文到本地文件；
2. 多行 gh 输出在 PowerShell 中可能成为字符串数组，写入文件前显式合并换行；
3. 含中文的 Release Notes 使用 UTF-8 无 BOM 文件和 `--notes-file`，避免命令行转义与编码转换；
4. 修改已发布 Release 属于外部状态变更，先确认授权和目标版本。

### 9.7 发布后收尾

- 将发布版本的已知问题状态同步到 [STATUS.md](STATUS.md)，并移出已完成计划；
- 确认远端 Release 资产上传成功、下载复核和 SHA256 校验全部通过；
- 完成确认后，清理项目内本次发布的 zip、`.sha256`、Release Notes 临时文件和打包暂存目录；
- 清理仅针对当前项目内已核对的精确路径，不删除源码、测试、插件、用户运行数据或后续开发所需目录；
- 备份 tag 只保留最近三个版本的现存里程碑，删除旧 tag 前先核对保留清单和删除清单。

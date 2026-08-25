# NexusPipeline 开发环境搭建与调试指南

本文件面向**开发者**：如何从源码编译、搭建运行环境、运行测试、排查故障。项目协作规范（Issue/PR/提交信息/代码风格）见 [CONTRIBUTING.md](../CONTRIBUTING.md)；版本发布流程见 [RELEASING.md](RELEASING.md)；核心设计理念见 [DESIGN.md](DESIGN.md)；模块导航见 [ARCHITECTURE.md](ARCHITECTURE.md)。

> **AI 协作（重要）**：使用 AI 工具（opencode 等）参与开发时，以根目录 `AGENTS.md` 为操作级权威规范（构建/测试顺序、运行时数据、环境陷阱、前端强约束）。本文件与 AGENTS.md 冲突时以 AGENTS.md 为准。

## 目录

1. [环境要求](#1-环境要求)
2. [从源码编译](#2-从源码编译)
3. [运行程序](#3-运行程序)
4. [运行测试](#4-运行测试)
5. [调试技巧](#5-调试技巧)
6. [运行时数据](#6-运行时数据)
7. [常见故障排查](#7-常见故障排查)

---

## 1. 环境要求

| 依赖 | 版本 | 用途 |
|---|---|---|
| Windows | 10/11 | 唯一支持平台（WinForms 托盘 + Win32 API） |
| .NET SDK | 8.x | 编译与运行（`dotnet --version` 验证；正式版 exe 为框架依赖，部署机仅需 .NET 8 Desktop Runtime） |
| Node.js（仅测试） | 20.x | Playwright 端到端测试（`tests/e2e/` 已装入依赖，不全局安装） |

- **无其他外部依赖**：不依赖数据库、云平台、前端构建链；网页管理界面为纯静态 ES modules，浏览器直接加载。
- **管理员权限**：程序必须以管理员身份运行（正式版构建带 `requireAdministrator` 清单，双击自动提权；非管理员启动拒绝并退出，exit 2）。开发调试请使用管理员 shell。

## 2. 从源码编译

```
build.cmd
```

双击或在 PowerShell/cmd 中执行即可，产物输出到 `release/`（与源码分离，不入库）：

```
release/
├── nexus-pipeline.exe   ← 主程序（单文件，框架依赖 .NET 8；提权版 requireAdministrator）
├── wwwroot/             ← 网页管理界面（纯静态，整体复制）
└── plugins/             ← 专项插件目录（整体复制）
```

- **增量构建（v0.9.6）**：`src/` 或 `plugins/` 内容未变化时跳过 `dotnet publish`，仅同步 `wwwroot/` 与 `plugins/`（指纹文件 `.build-src-hash`，不入库）；managed-code 插件文件变化会触发发布指纹更新。
- **无 /test 提权版**：`build.cmd` 只产出提权版（唯一构建形态；CI runner 以管理员运行，直接使用提权版）。
- 重构建前若提示 exe 被占用：`Get-Process nexus-pipeline | Stop-Process`（运行进程会锁定 `release\nexus-pipeline.exe`）。
- 手动等价命令（如需要指定参数）：

```
dotnet publish src\NexusPipeline.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o release
```

## 3. 运行程序

| 命令 | 行为 |
|---|---|
| `release\nexus-pipeline.exe` | 常驻服务模式：托盘 + Web + 调度器 |
| `... exe web` | 仅网页模式（退出循环：按回车 / stdin EOF） |
| `... exe manage` | 交互式命令行管理菜单 |
| `... exe status` | 查看状态 |
| `... exe run-script / run-queue / cancel` | 经常驻服务 HTTP 通道提交任务并轮询 |
| `... exe register / unregister` | 注册/取消开机自启动（计划任务 onlogon + highest） |

- 网页管理界面默认 `http://127.0.0.1:58731/`（端口被占用自动 +1，最多试 20 次）。
- 首次运行注意：配置迁移（旧版配置文件自动从 exe 同目录迁入 `config/`）、崩溃恢复扫描自动执行。

## 4. 运行测试

完整测试分层、归属规则、CI 和发布门禁见 [TESTING.md](TESTING.md)。v0.9.8 起默认执行以下顺序：

```powershell
# L1/L2：Unit + Component
dotnet test tests\NexusPipeline.Tests\NexusPipeline.Tests.csproj --nologo

# L3：原生 ES module 纯函数
$webTests = @(Get-ChildItem tests/web -Filter *.test.mjs -File | ForEach-Object { $_.FullName })
if ($webTests.Count -eq 0) { throw "未找到 Web Logic 测试文件" }
node --test $webTests

# 静态检查新增 UI Smoke
Get-ChildItem tests/e2e/tests -Filter *.smoke.spec.mjs | ForEach-Object { node --check $_.FullName }

# 构建隔离 runtime
build.cmd

# L5：Playwright UI Smoke（在 tests/e2e/ 下执行）
Push-Location tests\e2e
$env:PLAYWRIGHT_BROWSERS_PATH = "browsers"
npx playwright test
Pop-Location
```

System Smoke 需要管理员终端并依赖已完成的 `build.cmd`：

```powershell
tests\system\run-system.cmd
```

时间缩放只用于明确依赖宿主等待的专项脚本；判断脚本单次执行 30 秒上限保持真实墙钟语义。测试运行时数据位于 `tests/e2e/runtime/`、`tests/system/runtime/` 或 `tests/stress/runtime/`，日期断言使用项目现有 `localDate()` 规则。`tests/e2e/FLAKE-LEDGER.md` 保留历史记录，`flake-monitor.mjs` 按需启动。

## 5. 调试技巧

### 5.1 日志

- 管理器日志：`logs/nexus-pipeline-YYYY-MM-DD.log`，带级别 `[HH:mm:ss.fff] [LEVEL]`，阈值取设置 `LogLevel`（即时生效）。
- 脚本日志：按尝试分批落盘 `history/YYYY-MM-DD/HH-mm-ss-{尝试号}.log`（运行结束随历史保存）。
- 控制台按级别着色（WARN 黄 / ERROR 红 / FATAL 红底白字），仅未重定向时生效。
- 设置 `LogLevel` 为 debug 可看到 Web 请求级日志（`GET /api/status` 轮询豁免）。

### 5.2 测试钩子（生产零影响）

| 环境变量 | 作用 |
|---|---|
| `NEXUS_TIME_SCALE` | 缩放宿主等待时长（见 4.2） |
| `NEXUS_SYSTEM_ACTION_DRYRUN=1` | 系统操作（休眠/重启/关机）仅记录日志不真正执行——e2e global-setup 设置，防止 CI 真关机 |
| `NEXUS_SYSTEM_SMOKE=1` | 启用管理员 System Smoke（进程/端口/解释器/模拟器边界） |

### 5.3 Windows 环境陷阱（曾踩坑，勿重蹈；pwsh 7 + 系统 UTF-8 后大部分已消除）

- **工具链基线（v0.7.0 起）**：系统级 UTF-8 默认（ACP/OEMCP/MACCP=65001）+ opencode 使用 pwsh 7（profile 强制控制台/管道 UTF-8、`PYTHONUTF8=1`）；控制台/管道/文件写入默认 UTF-8，GBK 乱码与有损往返坑已根治。**Python 优先**：测试/批量文件操作/数据处理/临时脚本一律用 `python`（本机 Python 3.13），必须用 pwsh 的场景才用 pwsh。
- `Set-Content` 破坏 UTF-8 中文的坑（5.1 时代）已消除；稳妥起见写中文文件仍用编辑工具或 `[IO.File]::WriteAllText(..., [Text.Encoding]::UTF8)`（无 BOM）。
- **0x800700E8**：无控制台父进程启动 cmd/bat 必须带有效 stdio（`CreateProcess + RedirectStandardOutput/Error=true` 并消费）；**禁止**对 bat 用 `UseShellExecute`、禁止无重定向启动 cmd。
- **Win32Exception 740**：目标程序要求管理员——程序已强制管理员运行，仍出现时明确报错失败；**禁止 runas 降级提权**。
- `build.cmd` / `run-e2e.cmd` 不得加入无条件 `pause`（CI/PowerShell 调用会挂死）。
- 脚本自启动参数（Args）以显式路径开头（`X:\`、`\\`、`.\`、`..\`）= 运行时启动目标（管理端/执行端分离），`?` 后为参数；**Args 一律禁止引号**。
- gh 中文操作（曾踩坑）：修改已发布 release 前先备份原正文；含中文的 gh 写操作建议走文件（`--notes-file`，UTF-8 无 BOM）。

### 5.4 单元测试

- 工程：`tests/NexusPipeline.Tests/`（xUnit，`InternalsVisibleTo` 暴露 internal 契约）。
- 覆盖：判定状态机（SessionJudge）、关键字规则（KeywordRule）、日志路径解析（LogPattern）、模型规则校验（ScriptUserRule/QueueRule）、进程树清理（ProcessTreeTests）、自动更新配置同步（ConfigSwapSyncTests：还原描述执行器 array/map/稳定 ID 定位/事务镜像/内容有效性校验/首次检测时机，v0.7.6+）。

## 6. 运行时数据

| 位置 | 内容 |
|---|---|
| `config/settings\|scripts\|queues.json` | 用户配置（PascalCase，含加密密钥，**永不提交**） |
| `config/limits.json` | 约束配置（三层校验，FATAL 拒绝启动） |
| `config/plugins/<插件名>.json` / `.secrets.json` | managed-code 插件 JSON 配置 / DPAPI 密钥 |
| `history/YYYY-MM-DD/HH-mm-ss.json` + `-{尝试号}.log` | 运行状态（纯状态，PascalCase）+ 按尝试分批的脚本日志 |
| `logs/nexus-pipeline-YYYY-MM-DD.log` | 管理器日志（审计行 `[审计] 来源 \| 操作`，来源 web/manage/cli/scheduler/system） |
| `data/{脚本Id}/{用户}/` | 配置交换数据目录（store/store.previous/store.tmp/retry-store/original/script/swap-backup/edit-hidden/.session；v0.7.6 起 store 运行后自动更新回写——任务完成记录/计数保留延续） |

- 磁盘 JSON = PascalCase；Web API 返回 camelCase；读测试 JSON 前先 `.replace(/^\uFEFF/, "")` 去 BOM。
- 历史/管理器日志按保留天数每日清理（启动时 + 调度器每日首次 tick）。

## 7. 常见故障排查

| 现象 | 排查方向 |
|---|---|
| 启动即退出（exit 2） | 非管理员运行；用提权版或以管理员身份启动 |
| 「检测到已在运行」 | 单实例互斥体占用：检查任务管理器残留进程；强杀后首次启动会接管遗弃互斥体（v0.6.5+ 自动处理） |
| 端口被占用自动 +1 | 预期行为；检查是否有旧实例残留 |
| 重构建报 exe 锁定 | `Get-Process nexus-pipeline \| Stop-Process` 后重试 |
| Web 打不开 / ECONNREFUSED | 确认服务在运行、端口正确、轻量模式未开启（轻量模式无 Web） |
| 测试全量级联失败 | 服务残留：`tests/e2e/runtime` 清理残留进程后再跑；检查 `tests/e2e/flake-monitor-logs/` 采样 |
| 脚本判定异常 | 检查 `logs/` 管理器日志与 `history/` 按尝试分批日志；判断脚本模式看 JudgeError 输出 |
| 配置还原异常 | `data/{脚本Id}/{用户}/` 的 `.session` 标记与 swap-backup；重启服务触发自愈 |

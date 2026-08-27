# NexusPipeline 开发环境搭建与调试指南

本文件面向开发者，说明源码构建、运行方式、调试技巧、运行时数据和环境排查。协作流程见 [CONTRIBUTING.md](../CONTRIBUTING.md)；测试层级与完整命令见 [TESTING.md](TESTING.md)；发布流程见 [RELEASING.md](RELEASING.md)；核心设计见 [DESIGN.md](DESIGN.md)；模块导航见 [ARCHITECTURE.md](ARCHITECTURE.md)。

> 使用 AI 工具参与开发时，操作级约束以根目录 `AGENTS.md` 为准。本文件提供环境和调试背景，不复制产品语义或完整测试命令。

## 1. 环境要求

| 依赖 | 版本 | 用途 |
|---|---|---|
| Windows | 10/11 | 唯一支持平台（WinForms 托盘 + Win32 API） |
| .NET SDK | 8.x | 编译与运行；部署机需要 .NET 8 Desktop Runtime |
| Node.js | 20.x | Web Logic、System Smoke 和 Playwright 测试 |

- 网页管理界面为纯静态 ES modules，浏览器直接加载；源码构建不需要前端打包链。
- 程序必须以管理员身份运行。正式构建带 `requireAdministrator` 清单，开发调试请使用管理员 shell。
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
| `release\nexus-pipeline.exe run-script ...` / `run-queue ...` / `cancel ...` | 经常驻服务 HTTP 通道提交或取消任务 |
| `release\nexus-pipeline.exe register` / `unregister` | 注册或取消开机自启动任务 |

网页默认地址为 `http://127.0.0.1:58731/`；端口被占用时按顺序寻找可用端口。首次运行会执行旧配置迁移和崩溃恢复扫描。

## 4. 测试入口

测试分层、归属、默认命令、CI 顺序、System Smoke 和清理要求统一见 [TESTING.md](TESTING.md)。统一入口为 `node tests/run.mjs default|ui|system|all`；每次改动按照修改范围执行对应门禁；涉及进程、端口、解释器、模拟器、插件或更新事务时，追加管理员 System Smoke。

## 5. 调试技巧

### 5.1 日志

- 管理器日志：`logs/nexus-pipeline-YYYY-MM-DD.log`，包含级别、来源和审计行。
- 脚本日志：`history/YYYY-MM-DD/HH-mm-ss-{attempt}.log`，按尝试分批保存。
- `LogLevel=debug` 可以查看 Web 请求级日志；`GET /api/status` 轮询不记录。
- 判断脚本异常时，结合管理器日志中的 JudgeError、历史状态文件和对应尝试日志定位。

### 5.2 测试钩子

以下环境变量只用于测试或调试：

| 环境变量 | 作用 |
|---|---|
| `NEXUS_TIME_SCALE` | 缩放宿主等待时长；判断脚本单次 30 秒上限保持真实墙钟语义 |
| `NEXUS_SYSTEM_ACTION_DRYRUN=1` | 记录休眠、重启或关机请求，不执行真实系统操作 |
| `NEXUS_SYSTEM_SMOKE=1` | 启用管理员 System Smoke 运行模式 |
| `NEXUS_PLUGIN_CATALOG_URL` | 将插件 catalog 指向本地测试源；生产环境不设置 |

### 5.3 Windows 环境注意事项

- 控制台、管道和文件使用 UTF-8；批处理和中文文件操作应保持无 BOM 的 UTF-8。
- 无控制台父进程启动 cmd/bat 时必须提供并消费重定向的 stdout/stderr；构建和测试脚本保持非交互，不加入无条件 `pause`。
- 脚本运行必须在管理员上下文中完成。目标程序返回 Win32Exception 740 时应明确失败，保留管理员运行边界。
- 以显式路径开头的 `Args` 表示运行时启动目标，`?` 后为目标参数；Args 不使用引号表达路径。
- 使用 `cmd.exe` 运行批处理时，注意工作目录和环境变量继承；运行进程残留会锁定 `release\nexus-pipeline.exe`。

### 5.4 单元与组件测试定位

工程位于 `tests/NexusPipeline.Tests/`，通过 `InternalsVisibleTo` 覆盖宿主内部契约。常见定位方向包括：

- `SessionJudge`、`KeywordRule`：完成判定和关键字规则；
- `LogPattern`、`LogMonitor`：日志路径解析和增量读取；
- `ScriptUserRule`、`QueueRule`：模型约束；
- `ConfigSwapSyncTests`：快照同步、还原描述和事务镜像；
- `ProcessTreeTests`、执行准入和更新测试：进程清理、资源租约和更新状态机。

## 6. 运行时数据

| 位置 | 内容 |
|---|---|
| `config/settings.json`、`scripts.json`、`queues.json` | 用户配置、密钥和脚本/队列数据，永不提交 |
| `config/limits.json` | 约束配置 |
| `config/plugins/` | managed-code 插件配置和 DPAPI 密钥 |
| `history/YYYY-MM-DD/` | 运行状态 JSON 与按尝试分批的脚本日志 |
| `logs/` | 管理器日志 |
| `data/{脚本Id}/{UserId}/` | 配置交换快照、恢复标记、脚本目录和临时事务 |
| `.nxp/runtime/` | `service.pid`、`web.port` 等可重建运行标记 |
| `.nxp/state/` | `scheduler-state.json` 等需要跨重启保留的内部运行状态；旧根目录状态由取得单实例所有权的服务幂等迁移 |
| `.nxp/state/plugins/` | 插件仓库 catalog 缓存、商店归属、待重启事务以及 staging/backup 操作现场 |

`.nxp-update/`、`.nxp-backup/`、`.nxp-version` 和根目录 update worker 属于更新事务协议，继续留在安装根目录，不纳入普通运行状态收纳迁移。旧版本的根目录 `service.pid`、`web.port` 会在服务取得单实例互斥体后作为过期标记清理；旧 `scheduler-state.json` 在新文件不存在时原子移动，新旧同时存在时旧文件进入 `.nxp/state/recovery/`。

磁盘 JSON 使用 PascalCase，Web API 返回 camelCase。测试和调试应使用隔离 runtime，不能把运行时数据写入项目根目录。

## 7. 常见故障排查

| 现象 | 排查方向 |
|---|---|
| 启动即退出 | 确认以管理员身份运行，检查程序启动日志和 exit code |
| 检测到已在运行 | 检查任务管理器中的残留进程；确认单实例互斥体没有被其他服务占用 |
| Web 打不开 | 确认服务正在运行、端口正确，轻量模式不会启动 Web |
| 重构建失败或 exe 被锁定 | 停止对应服务进程后重新构建 |
| 测试出现级联失败 | 检查对应 suite 的隔离 runtime 和残留进程，按 [TESTING.md](TESTING.md) 清理 |
| 配置还原异常 | 检查 `data/{脚本Id}/{UserId}/` 下的 `.session`、`original/` 和 `swap-backup/`，保留现场后再进行恢复操作 |

# AGENTS.md

NexusPipeline（枢链）是 Windows 上的本地游戏自动化脚本管家：C#/.NET 8 WinForms 托盘程序、HttpListener Web 服务和零构建静态 ES module 前端。核心代码位于 `src/`，用户界面位于 `wwwroot/`，数据化插件位于 `plugins/`，测试位于 `tests/`。

## 权威文档路由

| 需要知道什么 | 阅读 |
|---|---|
| 用户功能、安装、快速使用 | [README.md](README.md) |
| 当前产品行为、不变量、已接受约束 | [docs/DESIGN.md](docs/DESIGN.md) |
| 模块边界、依赖和代码定位 | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| 开发环境、构建、调试和运行时数据 | [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) |
| 测试层级、完整命令和质量门禁 | [docs/TESTING.md](docs/TESTING.md) |
| 协作、Commit 和文档治理 | [CONTRIBUTING.md](CONTRIBUTING.md) |
| 发布、资产和 SHA | [docs/RELEASING.md](docs/RELEASING.md) |
| 当前版本路线和未完成计划 | [docs/ROADMAP.md](docs/ROADMAP.md) |
| 当前未解决问题与技术风险 | [docs/KNOWN_ISSUES.md](docs/KNOWN_ISSUES.md) |
| Plugin SDK、manifest 和扩展契约 | [plugins/README.md](plugins/README.md) |
| 版本历史 | [CHANGELOG.md](CHANGELOG.md) |

## Agent 强制规则

- 动手前在项目目录检查 `git status --short --branch` 和 `git rev-parse HEAD`，保留用户已有改动；Workspace 根目录不是 Git 仓库。
- 版本开发开始时按用户指定版本同步项目版本号；架构重构、新功能和破坏性版本开工前创建本地 `backup/vX.Y.Z-*` 还原点。备份 tag 不推送。
- 未经用户明确授权，不执行 commit、push、tag、Pull Request 或 Release；版本开发期间不自行拆分发布。
- `config/`、`data/`、`history/`、`logs/` 和测试 runtime 属于用户或运行时现场。阅读时脱敏，修改和测试时使用隔离目录，禁止把凭据、账号、日志和运行产物加入版本库。
- 文档任务保持文档范围。发现代码、测试和 DESIGN 的行为描述不一致时，先核对事实；需要改变产品行为时停止该冲突项并向用户报告。
- 修改行为前阅读 DESIGN，寻找实现前阅读 ARCHITECTURE，修改测试前阅读 TESTING，修改插件前阅读 `plugins/README.md`，发布前阅读 RELEASING。
- 测试失败时保留失败证据并修复根因；禁止通过自动重试、跳过失败或静默重定向掩盖失败。完整测试命令只维护在 `docs/TESTING.md`。

## Windows 与工具链注意事项

- 程序和需要进程控制的测试在管理员上下文中运行；目标程序要求管理员时保留明确失败边界，不使用 runas 降级提权。
- 控制台、管道和文件保持 UTF-8；启动 cmd/bat 时提供并消费有效的 stdout/stderr；`build.cmd` 和 `tests/e2e/run-e2e.cmd` 保持非交互，不加入无条件 `pause`。
- 以显式路径开头的脚本 `Args` 表示运行时启动目标，`?` 后是目标参数；Args 不使用引号表达路径。
- 能用 Python 完成的测试、批量文件操作、数据处理和临时脚本使用 Python；项目既有的 `.cmd`、`.ps1`、`dotnet`、`node` 和 `npm` 流程按原入口运行。
- 重构建前确认没有残留的 `nexus-pipeline` 进程锁定 `release/nexus-pipeline.exe`。清理时使用已核对的精确路径。

## 前端硬约束

- `wwwroot/` 保持零构建、零 CDN、原生 ES modules；模块按 `app.js`、`core/`、`views/`、`effects/` 分层，视图通过 `actions` 注册表接入事件委托。
- 交互使用原生控件、`data-action` 和稳定 `data-testid`；禁止 inline 事件、内联 style、散落颜色字面量和需要打包的依赖。
- 页面支持 360px、768px 和 1280px 视口，触控目标至少 40px；轮询经 `core/state.js` 管理并在路由离开时清理。
- 主题、弹窗、Toast、焦点和无障碍行为遵循现有界面约束；新增样式复用 CSS 变量和既有紧凑列表模式。

## 最短验证入口

按 [docs/TESTING.md](docs/TESTING.md) 执行与修改范围对应的门禁。活动测试统一经 Node 调度器运行；涉及真实进程、端口、解释器、模拟器、managed plugin 或更新事务时，在默认门禁后从管理员终端运行：

```text
node tests\run.mjs default
node tests\run.mjs system
```

UI Smoke 使用 `node tests\run.mjs ui`；`tests\system\run-system.cmd` 与 `tests\e2e\run-e2e.cmd` 仅保留为兼容转发入口。服务普通运行状态位于 `.nxp\runtime\` 与 `.nxp\state\`，更新事务目录仍按原协议留在安装根目录。

# NexusPipeline

NexusPipeline 是 Windows 11 x64 本地自动化管家，统一管理脚本、账号配置、运行队列、日程、日志、截图与插件。Electron 桌面和浏览器使用同一 Vue 界面，通过 Host 的受控入口访问业务。

当前源码面向 0.18 系列；发行与未完成验收见[当前状态](docs/status.md)。0.17 与 0.18 使用独立目录和实例，原版本及原数据须保留。

## 开始使用

1. 按[安装与启动](docs/user/install.md)准备完整软件，双击应用目录的 `NexusPipeline.exe`。
2. 按[第一次运行](docs/user/quickstart.md)登记脚本、建立账号绑定并预览计划。
3. 运行后在[历史](docs/user/history.md)核对结果、日志与图片。

从源码开始：按[开发环境与首次运行](docs/getting-started/development.md)准备工具，在仓外构建槽编译并启动独立 Test 实例。

## 技术与导航

后端使用 C# / .NET 10、SQLite 八库状态组；Web 使用 TypeScript / Vue 3 / Vite；桌面使用锁定的 Electron。Python 工具负责源码快照、构建、验证与发行来源。插件通过独立的 .NET 和前端 SDK 扩展能力。

| 任务 | 入口 |
|---|---|
| 使用软件、备份或排障 | [用户指南](docs/user/README.md) |
| 开发与贡献 | [CONTRIBUTING](CONTRIBUTING.md) |
| 理解架构、定位模块和接口 | [文档导航](docs/README.md) |
| 使用 AI Agent 开发 | [AGENTS](AGENTS.md) → [任务地图](docs/agents/README.md) |
| 开发官方插件 | [NexusPipeline-Plugins](https://github.com/FlappiBakuse/NexusPipeline-Plugins) |

许可证见 [LICENSE](LICENSE)。

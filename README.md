# NexusPipeline（枢链）

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![CI](https://github.com/FlappiBakuse/NexusPipeline/actions/workflows/ci.yml/badge.svg)](https://github.com/FlappiBakuse/NexusPipeline/actions)
[![Release](https://img.shields.io/github/v/release/FlappiBakuse/NexusPipeline?include_prereleases)](https://github.com/FlappiBakuse/NexusPipeline/releases)

NexusPipeline 是一个运行在 Windows 上的本地游戏自动化脚本管家。它可以按计划启动脚本、监控日志、处理重试、隔离多账号配置，并在任务结束后发送通知或执行关机、休眠、重启等操作。

程序常驻托盘，管理页面默认位于 `http://127.0.0.1:58731/`。配置、历史、日志和插件都保存在本机，不需要云平台或数据库。运行程序需要 .NET 8 Desktop Runtime 和管理员权限。

## 主要功能

- 脚本实例：管理主程序、参数、配置文件、日志路径、判断规则和运行限制。
- 多用户配置：每个用户绑定独立配置，运行前切换，运行后恢复；运行产生的配置进度可按文件差异回写快照。
- 完成判定：支持成功/失败关键字，也支持 JavaScript 或 Python 判断脚本；结果包含 `success`、`partial`、`failed`、`cancelled` 和 `skipped`。
- 调度队列：按顺序运行多个脚本，支持按星期/时间触发、重试、资源冲突检查和完成后系统操作。
- 历史与通知：保存状态、每次尝试的日志和运行截图；支持 Webhook、SMTP、飞书、钉钉、企业微信、Slack、Discord 等通知目标。
- 专项插件：官方 [NexusPipeline-Plugins](https://github.com/FlappiBakuse/NexusPipeline-Plugins) 提供 BetterGI、March7th Assistant、ZenlessZoneZeroOneDragon、MaaEnd 等适配。
- 模拟器支持：可使用通用 ADB，或由专项插件提供 MuMuManager 等模拟器能力。
- 控制面：网页、`manage` 菜单和正式 CLI 共享本机服务；可选启用 loopback MCP Server。
- 内建更新：检查、下载和校验 GitHub 发布包，支持立即应用或下次启动应用。

## 安装

1. 从 [GitHub Releases](https://github.com/FlappiBakuse/NexusPipeline/releases) 下载 `NexusPipeline-vX.Y.Z-win-x64.zip`。
2. 解压到固定目录，例如 `D:\NexusPipeline\`。
3. 双击 `nexus-pipeline.exe`，按系统提示允许管理员权限。
4. 浏览器打开 `http://127.0.0.1:58731/`，完成脚本、用户和队列设置。
5. 需要开机运行时，在「设置」中开启开机自启动。

未安装运行时的电脑请从 [.NET 8 下载页](https://dotnet.microsoft.com/download/dotnet/8.0)安装 Desktop Runtime 8.x。

## 升级前备份（重要）

v0.13.6 起，程序按当前持久化格式工作，不再自动转换历史版本的数据、目录或兼容字段。升级前请退出 NexusPipeline，并备份完整的安装目录中的运行时数据，至少包括：

```text
config/    data/    history/    logs/    plugins/    .nxp/
```

「更新」页中的版本备份用于更新文件切换与回滚，不能替代用户数据备份。更新完成后若发现无法识别的旧现场，请保留现场目录和日志，使用备份恢复数据，再根据当前版本格式重新配置。

手动升级时，保留上述运行时目录，仅替换新版本发布包中的程序文件和 `wwwroot/`。插件仓库独立维护，插件版本和最低宿主版本以插件 manifest 为准。

## 快速开始

以 BetterGI 为例：

1. 安装并启动 BetterGI。
2. 打开 NexusPipeline 的「插件」页，在「插件仓库」安装 BetterGI，然后按提示重启。
3. 在「脚本实例」中新建 BetterGI 专项脚本，只填写 BetterGI 安装根目录。
4. 在「用户管理」中新建用户，并把脚本绑定到该用户。
5. 在「调度中心」执行脚本，或创建按时间运行的队列。
6. 在「历史记录」查看状态、尝试日志和截图。

通用脚本可直接填写 exe/bat、参数、配置路径和日志路径。其他专项插件的配置流程相同。

## CLI 与 MCP

常驻服务运行后，可以使用正式 CLI：

```text
nexus-pipeline.exe status --json
nexus-pipeline.exe script list --json
nexus-pipeline.exe user create --name "我的账号"
nexus-pipeline.exe run script <脚本 ID 或名称> --detach --json
nexus-pipeline.exe run cancel <运行 ID> --json
```

复杂对象通过 `--file <json 文件>` 或 `--file -` 传入。带 `--json` 的命令输出单个稳定 envelope，目标按 ID 或唯一名称解析。

在「设置 → MCP Agent」启用 MCP 并重启后，Agent 可连接：

```text
http://127.0.0.1:58732/mcp
```

MCP 仅监听本机 loopback；运行队列若带有休眠、重启、关机或退出动作，会要求通过本地管理路径确认。

## 文档

| 文档 | 内容 |
|---|---|
| [CHANGELOG.md](CHANGELOG.md) | 版本变更与升级注意事项 |
| [docs/DESIGN.md](docs/DESIGN.md) | 运行流程、持久化和模块边界 |
| [docs/CONTROL_PLANE.md](docs/CONTROL_PLANE.md) | Web、CLI、MCP 能力入口 |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | 构建、调试、协作与发布 |
| [docs/TESTING.md](docs/TESTING.md) | 测试层级与质量门禁 |
| [docs/PLUGIN_API.md](docs/PLUGIN_API.md) | 插件开发接口 |
| [docs/STATUS.md](docs/STATUS.md) | 后续计划与已知问题 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 贡献指南 |
| [SECURITY.md](SECURITY.md) | 安全问题报告 |

## 常见问题

**为什么需要管理员权限？**

脚本启动、输出捕获、进程树清理和部分游戏自动化场景需要管理员权限。

**端口被占用怎么办？**

Web 默认端口被占用时会顺延到可用端口；实际端口可在状态页或「设置」查看。MCP 使用独立端口，端口被占用时保持关闭。

**运行脚本产生的配置文件会保留吗？**

运行结束时，配置交换目录会恢复到运行前现场；开启自动更新配置后，宿主会把允许同步的差异写入该用户快照。脚本的其他运行数据请放在配置目录之外。

**如何报告问题？**

请在提交问题前准备版本号、复现步骤和相关日志，并移除令牌、账号信息及其他敏感数据，然后在 [GitHub Issues](https://github.com/FlappiBakuse/NexusPipeline/issues) 提交。

## License

[MIT](LICENSE) © 2026 FlappiBakuse

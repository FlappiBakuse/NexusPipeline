# NexusPipeline（枢链）

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![CI](https://github.com/FlappiBakuse/NexusPipeline/actions/workflows/ci.yml/badge.svg)](https://github.com/FlappiBakuse/NexusPipeline/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/FlappiBakuse/NexusPipeline?include_prereleases)](https://github.com/FlappiBakuse/NexusPipeline/releases)

NexusPipeline 是一个运行在 Windows 上的本地游戏自动化脚本管家。它可以按计划启动脚本、监控日志、处理重试、隔离多账号配置，并在任务结束后发送通知或执行关机、休眠、重启等操作。

程序常驻托盘，管理页面默认位于 `http://127.0.0.1:58731/`。配置、历史、日志和插件都保存在本机，不需要云平台或数据库。运行程序需要 .NET 8 Desktop Runtime、ASP.NET Core Runtime 8 和管理员权限。

## 主要功能

- 脚本实例：管理主程序、参数、配置文件、日志路径、判断规则和运行限制。
- 通用脚本迁移：可将通用脚本配置导出为 `.nxpscript.json`，在新建通用脚本时导入并按当前根目录解析路径。
- 多用户配置：每个用户绑定独立配置，运行前切换，运行后恢复；运行产生的配置进度可按文件差异回写快照。
- 完成判定：支持成功/失败关键字，也支持 JavaScript 或 Python 判断脚本；结果包含 `success`、`partial`、`failed`、`cancelled` 和 `skipped`。
- 调度队列：按顺序运行多个脚本，支持按星期/时间触发、重试、资源冲突检查和完成后系统操作。
- 历史与通知：保存状态、每次尝试的日志和运行截图；支持 Webhook、SMTP、飞书、钉钉、企业微信、Slack、Discord 等通知目标。
- 独立签到：GameCheckIn 提供多个独立签到任务，每个任务可设置游戏、凭据、本机时区的星期/时间计划和宿主全局通知；任务可覆盖 SMTP 收件人，不依赖绑定用户或脚本实例。
- 专项插件：官方 [NexusPipeline-Plugins](https://github.com/FlappiBakuse/NexusPipeline-Plugins) 提供 BetterGI、March7th Assistant、ZenlessZoneZeroOneDragon、MaaEnd 等适配。
- 模拟器支持：宿主内置 Generic ADB 与 MuMuManager；雷电、夜神和 BlueStacks 的专属识别与实例关闭由可选的“模拟器支持扩展”插件提供，安装并启用后可使用。专项插件通过 `emulator` capability 声明支持模拟器实例。
- 控制面：网页、`manage` 菜单和正式 CLI 共享本机服务；可选启用 loopback MCP Server。
- 诊断与可验证性：提供系统诊断、脱敏支持包和运行计划 dry-run，便于确认环境、恢复现场与准入原因。
- 内建更新：定期检查、下载和校验 GitHub 发布包，支持立即应用或下次启动应用；开启“闲时自动更新”后，宿主下次启动会先检查并在可用时下载、应用更新，再启动服务，运行期间仍可等待闲时更新；另有独立的插件自动更新开关。跨越声明的破坏性版本时提示手动迁移。

## 安装

1. 从 [GitHub Releases](https://github.com/FlappiBakuse/NexusPipeline/releases) 下载当前版本提供的安装器或便携包。
2. 首次使用安装器时按向导选择空目录；同一 Windows 用户已登记的安装实例可选择原路径升级，安装器会先暂存并核验应用文件，再交给宿主现有更新事务切换。使用便携包时解压到固定空目录，例如 `D:\NexusPipeline\`。请勿把新包直接解压覆盖旧实例，安装器也不会接管未登记的旧便携目录。
3. 双击 `nexus-pipeline.exe`，按系统提示允许管理员权限。
4. 浏览器打开 `http://127.0.0.1:58731/`，完成脚本、用户和队列设置。
5. 需要开机运行时，在「设置」中开启开机自启动。

便携版缺少运行时会显示 .NET 自带的提示。可直接打开包内的 `wwwroot/help/runtime-prerequisites.html`，或分别下载官方 [Desktop Runtime 8.0.31 x64](https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.31/windowsdesktop-runtime-8.0.31-win-x64.exe) 和 [ASP.NET Core Runtime 8.0.31 x64](https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/8.0.31/aspnetcore-runtime-8.0.31-win-x64.exe)。安装器会检查两项依赖；便携版不会自动下载。

## 升级前备份（重要）

程序按当前持久化格式工作，不自动转换旧格式的数据、目录或兼容字段。升级前请退出 NexusPipeline，并备份完整的安装目录中的运行时数据，至少包括：

```text
config/    data/    history/    logs/    plugins/    .nxp/
```

「更新」页中的版本备份用于更新文件切换与回滚，不能替代用户数据备份。更新完成后若发现无法识别的旧现场，请保留现场目录和日志，使用备份恢复数据，再根据当前版本格式重新配置。

手动升级时，保留上述运行时目录，仅替换新版本发布包中的 `nexus-pipeline.exe`、`wwwroot/` 和 `README.md`。不要将新版包的 `plugins/` 覆盖已有插件或用户数据。如果更新页提示跨越破坏性版本屏障，请手动下载对应安装包，按发布说明迁移配置后再启动。插件仓库独立维护，插件版本和最低宿主版本以插件 manifest 为准。

从 v0.16.8 通过内置更新到 v0.16.9 时，旧版更新 worker 只交换 EXE 和 `wwwroot/`。新版首次启动会在原目录没有 `README.md` 时，从已暂存的包补上说明文件；已存在的 README 保留原字节，必要时可按上述手动步骤更新。旧实例中的插件及启用状态保持原样；两个预装插件仅用于全新安装。

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
nexus-pipeline.exe doctor --json
nexus-pipeline.exe doctor export --output "D:\Temp\nexus-pipeline-diagnostics.zip" --json
nexus-pipeline.exe script list --json
nexus-pipeline.exe user create --name "我的账号"
nexus-pipeline.exe run script <脚本 ID 或名称> --dry-run --user "我的账号" --json
nexus-pipeline.exe run script <脚本 ID 或名称> --detach --json
nexus-pipeline.exe run cancel <运行 ID> --json
```

复杂对象通过 `--file <json 文件>` 或 `--file -` 传入。带 `--json` 的命令输出单个稳定 envelope，目标按 ID 或唯一名称解析。
`doctor` 读取宿主、监听器、更新/配置恢复现场、插件和依赖状态；`doctor export` 生成不含配置内容、密钥、令牌、Cookie 或截图的脱敏支持包。`run ... --dry-run` 只展示冻结运行计划、用户状态、资源和准入原因，不登记或启动任务。

在「设置 → MCP Agent」启用 MCP 并重启后，Agent 可连接：

```text
http://127.0.0.1:58732/mcp
```

MCP 仅监听本机 loopback；运行队列若带有休眠、重启、关机或退出动作，会要求通过本地管理路径确认。

## 文档

| 文档 | 内容 |
|---|---|
| [docs/user/README.md](docs/user/README.md) | 用户安装、运行、结果、升级、卸载与 FAQ |
| [docs/user/README.en.md](docs/user/README.en.md) | Key user limits in English |
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

**如何准备诊断信息？**

打开「设置 → 系统诊断」查看检查结果，或运行 `nexus-pipeline.exe doctor export --output <路径>` 导出脱敏支持包。支持包只包含有限大小的诊断事实、插件状态、运行状态和最近日志尾部。

**运行脚本产生的配置文件会保留吗？**

运行结束时，配置交换目录会恢复到运行前现场；开启自动更新配置后，宿主会把允许同步的差异写入该用户快照。脚本的其他运行数据请放在配置目录之外。

**如何报告问题？**

请在提交问题前准备版本号、复现步骤和相关日志，并移除令牌、账号信息及其他敏感数据，然后在 [GitHub Issues](https://github.com/FlappiBakuse/NexusPipeline/issues) 提交。

## License

NexusPipeline 的 MIT 许可全文如下；源码中的 [LICENSE](LICENSE) 与本文相同。

```text
MIT License

Copyright (c) 2026 FlappiBakuse

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

专项任务支持只读任务预览、日志证据、受控选择重试和历史报告，详见[专项任务协议](docs/reference/plugin-api/task-protocol.md)。

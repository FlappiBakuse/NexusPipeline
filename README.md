# NexusPipeline（枢链）

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![CI](https://github.com/FlappiBakuse/NexusPipeline/actions/workflows/ci.yml/badge.svg)](https://github.com/FlappiBakuse/NexusPipeline/actions)
[![Release](https://img.shields.io/github/v/release/FlappiBakuse/NexusPipeline?include_prereleases)](https://github.com/FlappiBakuse/NexusPipeline/releases)

**本地游戏自动化脚本管家**：一个常驻 Windows 托盘的小程序，按计划替你启动、监控、重试、收尾游戏自动化脚本（BetterGI、March7th Assistant、绝区零一条龙、MaaEnd 等）。支持多账号配置自动切换，跑完把结果推送到手机（飞书/钉钉/企业微信/邮件），全部任务结束后还能自动关机、休眠或重启。

- 网页管理界面：浏览器打开即用，手机也能操作
- 纯本地运行：不需要任何云平台、数据库，解压即用，配置全部留在你自己的电脑上
- C# / .NET 8，框架依赖的单文件 exe；不依赖数据库、云平台或前端构建环境，运行机器需安装 .NET 8 Desktop Runtime

## 它能帮你做什么

| 场景 | 用法 |
|---|---|
| **挂机日常** | 睡前在网页上点一下，让原神/崩铁/终末地的脚本跑完日常、清体力，跑完自动关机 |
| **多账号轮换** | 同一个脚本配多个账号，每个账号独立配置，按顺序自动切换运行 |
| **失败自动重试** | 任务失败自动重试（最多 N 次）；支持「只重跑失败的任务」（如 BetterGI、MaaEnd 专项判断） |
| **定时调度** | 每天固定时间、每周任意几天自动开跑；开机自启后全自动 |
| **跑完通知** | Webhook（飞书/钉钉/企业微信/Slack/Discord）或邮件推送运行结果 |

## 核心特性

- **脚本实例**：一个脚本 = 主程序 + 参数 + 配置文件 + 日志路径 + 运行规则。可同时管理多个脚本。
- **专项插件**：插件仓库提供 BetterGI（原神）、March7th Assistant（崩坏：星穹铁道）、ZenlessZoneZeroOneDragon（绝区零）、MaaEnd（明日方舟：终末地）适配——在「插件」页安装后重启服务，新建脚本时选「专项」，自动推导主程序/配置/日志路径。
- **全局用户管理**：在仪表盘下方统一管理用户头像、全局运行顺序和多个脚本绑定；每个绑定独立保存配置、前后置脚本、参与运行和通知设置，用户以稳定 ID 关联自己的脚本数据。
- **多账号配置隔离**：每个账号绑定一份独立配置，运行前自动切换、运行后自动还原，互不干扰；运行产生的任务完成记录/运行计数默认自动回写该绑定快照（自动更新配置），下次运行延续。同步使用临时快照事务，失败时保留旧快照。
- **智能完成判定**：通过监控脚本日志判断任务成功/失败（而非只看进程是否退出）。支持自定义「成功/失败关键字」，也支持用 JavaScript/Python 写判断脚本（可读写文件、可改写配置后自动重试）。
- **调度队列**：多个脚本按顺序链式执行，并按资格矩阵并行调度——仅含已验证安卓模拟器脚本的队列可互相并行，并可与最多一个普通队列同时运行，普通队列之间保持串行；脚本、用户数据、进程、配置路径、日志模式、前/后置脚本和模拟器 ADB 端点发生资源冲突时拒绝准入；定时（按星期/时间）或启动时自动触发，瞬时资源冲突会保留触发并在资源释放后重试；第一个队列提交完成操作后运行组进入收尾状态，新的执行等完成操作执行或取消后再加入；整个并行运行组完成后可执行：退出软件 / 休眠 / 重启 / 关机（执行前 60 秒倒计时可取消）。任务失败也会照常执行完成操作，手动取消可跳过。
- **历史记录**：每次运行完整留档（状态 + 按尝试分批的脚本日志），默认保留 7 天（可调，最长 180 天）。
- **游戏联动**：可选在脚本前启动游戏、运行中自动把游戏窗口置前（防截图识别被遮挡）、失败时强制结束游戏进程。
- **安卓模拟器基础设施**：脚本实例可选择「安卓模拟器」启动方式——填模拟器 ADB 地址（如 `127.0.0.1:16384`）+ am start 参数，运行前自动连接模拟器、启动/关闭应用、失败重试时关闭目标应用。通用目标使用独立 ADB driver；被 MuMuManager 精确识别的目标从连接、启动、探测到收尾始终使用 MuMuManager。专用插件是否支持模拟器由 capability 声明（目前仅 MaaEnd 专项支持）。
- **通知推送**：Webhook / SMTP 双通道并行，属于宿主内置通知能力，配置入口位于「设置」页，密钥本地加密存储（DPAPI）。
- **插件仓库**：固定官方仓库提供 catalog、安装、更新和卸载；插件包下载后进行 SHA256、路径和 manifest 校验，操作在重启服务时完成。
- **网络代理**：设置页支持无代理、使用系统设置和自定义 HTTP/HTTPS 代理，覆盖宿主外部 HTTP 请求；本机服务、MCP、SMTP 和插件子进程保持原有网络行为。
- **内建更新**：设置页可检查 GitHub 最新版本、下载校验（SHA256）后就绪、「立即更新」或「下次启动更新」；应用走独立切换进程（备份旧版本、失败自动回滚、启动自愈），配置、历史、用户数据和插件目录保持不变。
- **命令行与控制面**：`manage`、正式 noun/subcommand CLI、网页和后续自动化入口共享常驻服务控制面；支持脚本/用户/队列/运行/历史/设置/插件/更新/维护等操作，并提供稳定的 `--json` 输出。
- **MCP Agent 控制面**：可选在同一 `nexus-pipeline.exe` 内启动官方 Streamable HTTP MCP Server；端点固定为 `http://127.0.0.1:58732/mcp`（端口可配置），工具按只读、常规变更和破坏性操作分层，长任务返回 `runId` 供轮询。

## 安装

1. 从 [GitHub Releases](https://github.com/FlappiBakuse/NexusPipeline/releases) 下载最新的 `NexusPipeline-vX.Y.Z-win-x64.zip`；
2. 解压到任意位置（建议放到非系统盘根目录，如 `D:\NexusPipeline\`）；
3. 双击 `nexus-pipeline.exe`（**必须以管理员身份运行**——程序会自动请求提权；脚本程序需要管理员权限才能被接管运行）；
4. 浏览器打开 `http://127.0.0.1:58731/` 即可看到管理界面（程序常驻托盘，托盘图标可随时打开页面/退出）；
5. （可选）在「设置」里开启**开机自启动**，登录 Windows 时自动以最高权限静默启动。

> 需要 .NET 8 运行时：未安装时从 [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0) 下载「.NET Desktop Runtime 8.x」（大多数情况下系统已内置）。
> 升级：优先在「设置 → 更新」中检查并应用更新。手动升级时先退出程序，再用新版本发布包覆盖 `nexus-pipeline.exe`、`wwwroot/`、README 和 LICENSE；请保留 `plugins/`、`config/`、`data/`、`history/`、`logs/` 等运行时目录。

## 快速上手（以 BetterGI 原神专项为例）

假设你已安装 [BetterGI](https://github.com/babalae/better-genshin-impact)：

1. **启动服务**：双击 `nexus-pipeline.exe`，浏览器打开 `http://127.0.0.1:58731/`；
2. **安装插件**：进入「插件」→「插件仓库」，安装 BetterGI，按提示重启服务；
3. **新建脚本**：进入「脚本实例」→ 点击「新建 BetterGI 专项脚本实例」卡片；
4. **填写根目录**：只填 BetterGI 的安装根目录（如 `D:\Games\BetterGI`），保存——主程序、配置、日志路径自动推导，图标自动显示；
5. **添加用户**：进入仪表盘下方「用户管理」，新建一个用户（如 `我的主号`），再在用户管理弹窗中绑定 BetterGI 脚本；
6. **运行**：到「调度中心」，选中该脚本，点「执行」——NexusPipeline 会自动启动游戏、运行脚本、监控日志判定结果；
7. **查看结果**：任务结束后在「历史记录」里查看每次尝试的状态与完整日志。

其他专项（MaaEnd 明日方舟：终末地、March7th 崩铁等）流程完全相同：新建脚本 → 填根目录 → 在用户管理中绑定账号 → 运行。

> 专项插件也可用于手动管理：脚本实例页面支持直接新建/编辑通用脚本，主程序、参数、配置、日志路径全部手工指定，适合任何 exe/bat 脚本。

## 三种使用形态

1. **服务模式（默认）**：双击启动 → 托盘常驻 → 网页管理 + 自动调度。适合日常挂机。
2. **轻量命令行模式**：设置中开启（重启生效）——仍启动仅绑定 `127.0.0.1` 的 Control API，不提供静态 Web UI 与浏览器，托盘/命令行交互经控制面完成。适合低配环境。
3. **单次命令**：`nexus-pipeline.exe run script <脚本名>` 等命令直接提交任务并等待结果（常驻服务未运行时自动拉起），可被其他程序脚本化调用。

## CLI 控制接口

正式 CLI 的复杂操作使用 noun/subcommand 形式；脚本、用户和队列的复杂对象通过 JSON 文件或标准输入传入：

```text
nexus-pipeline.exe status --json
nexus-pipeline.exe script list --json
nexus-pipeline.exe script create --file script.json
nexus-pipeline.exe user create --name "我的账号" --remark "主号"
nexus-pipeline.exe user binding add "我的账号" --script "BetterGI" --file binding.json
nexus-pipeline.exe user binding config start "我的账号" "BetterGI"
nexus-pipeline.exe user global-settings get "我的账号" --json
nexus-pipeline.exe plugin store list --json
nexus-pipeline.exe plugin user-settings list "我的账号" --json
nexus-pipeline.exe queue update <队列 ID 或名称> --file queue.json
nexus-pipeline.exe run script <脚本 ID 或名称> --detach --json
nexus-pipeline.exe run cancel <运行 ID> --json
```

带 `--json` 的正式命令在标准输出中返回单个 envelope：成功为 `{ "ok": true, "code": "ok", "data": ... }`，失败为 `{ "ok": false, "code": ..., "message": ... }`；诊断与运行进度写入标准错误。目标参数按“ID 精确匹配 → 名称唯一匹配”解析，同名目标返回 `ambiguous_target` 与候选 ID。未运行的常驻服务会由 CLI 自动拉起，控制 API 的实际监听端口由服务状态发现。

## MCP Agent 接入

在「设置 → MCP Agent」中启用 MCP 服务并重启 NexusPipeline。服务启动后，Agent 使用以下 Streamable HTTP 地址连接：

```text
http://127.0.0.1:58732/mcp
```

MCP 端口只绑定本机 loopback，与「远程访问」设置相互独立；端口被占用时 MCP 保持不可用，Control API 与脚本调度继续运行。NexusPipeline 信任同一台计算机上的本机进程，MCP 的破坏性工具开关属于 Agent 操作护栏；loopback、Host、Origin 和请求体限制提供网络边界。MCP 工具使用稳定的脚本、用户、队列和运行 ID，`run_script` / `run_queue` 会立即返回 `runId`，再用 `get_run` 查询状态；已有队列若配置了完成后的休眠、重启、关机或退出动作，`run_queue` 会返回 `dangerous_completion_action`。

默认工具包含状态、脚本、用户、用户全局设置、绑定、队列、运行、历史、插件、插件商店和插件用户设置的脱敏查询，以及运行/取消、资源 CRUD 和安全设置更新。删除资源、密钥写入、插件启停、插件安装/更新/卸载、服务重启、应用更新和遗留数据清理等高风险工具只有在本机显式开启「允许破坏性工具」并重启后才会出现在工具列表中；密钥写入使用本地 DPAPI 加密，工具响应与审计日志均不回显明文。

MCP 不承担 Web 页面、图标/头像上传、第三方 GUI 配置编辑、任意文件读写或 shell 执行。完整的工具边界、生命周期和安全模型见 [docs/DESIGN.md](docs/DESIGN.md)。

## 常见问题

**为什么要管理员权限？**
脚本程序需要以管理员权限被接管运行（创建进程、捕获输出、监控日志、强制清理进程树）。普通权限启动会拒绝运行并提示。

**端口被占用怎么办？**
默认端口 58731 被占用时自动顺延（+1）。可在「设置」中修改端口。

**如何在外网/其他设备访问？**
在「设置」中开启「允许远程访问」并设置访问令牌，程序自动放行防火墙；其他设备用本机局域网 IP 访问（如 `http://192.168.x.x:58731/`）。请勿在公共网络环境开启。

**脚本不写日志，能判定成功吗？**
可以：未配置任何判定时，脚本进程自行退出即视为成功。但精确判定（成功/失败/重试）依赖日志，建议脚本尽量输出日志并配置日志路径。

**运行期间脚本自己生成的文件会被清理吗？**
会。多账号配置交换机制会在运行结束后还原配置目录现场——脚本写入配置目录内的文件会被删除（运行日志由 NexusPipeline 历史记录保全）。脚本数据请放在配置目录之外。**例外**：脚本自身写入的任务完成记录/运行计数等配置更改，会按「自动更新配置」（默认开，通用脚本可在脚本弹窗的「自定义完成标志」区关闭）在运行结束时回写该账号的用户快照，下次运行自动延续——脚本运行进度不再被还原抹掉。

## 维护者

- 维护者：**FlappiBakuse**
- 仓库：[github.com/FlappiBakuse/NexusPipeline](https://github.com/FlappiBakuse/NexusPipeline)
- 协议：MIT（见 [LICENSE](LICENSE)）

## 文档导航

| 文档 | 读者 | 内容 |
|---|---|---|
| [docs/DESIGN.md](docs/DESIGN.md) | 开发者 | 核心设计理念、运行流程与模块边界（第 10 节为开发者导航） |
| [docs/CONTROL_PLANE.md](docs/CONTROL_PLANE.md) | 开发者 | Web、CLI、MCP 控制面能力现状 |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | 开发者 | 开发环境、调试、协作规范与发布流程 |
| [docs/TESTING.md](docs/TESTING.md) | 开发者 | 测试层级与质量门禁 |
| [docs/STATUS.md](docs/STATUS.md) | 开发者 | 后续开发计划与已知问题台账 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 贡献者 | 贡献要点速览 |
| [SECURITY.md](SECURITY.md) | 所有人 | 安全漏洞报告与敏感信息处理 |
| [CHANGELOG.md](CHANGELOG.md) | 所有人 | 版本历史（升级前必看） |
| [docs/PLUGIN_API.md](docs/PLUGIN_API.md) | 高级用户 | 专项插件数据化形态与自定义插件指南 |

## License

[MIT](LICENSE) © 2026 FlappiBakuse

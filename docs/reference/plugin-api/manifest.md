# 插件清单与包格式

## 目录结构

```
NexusPipeline-Plugins/plugins/
├── general/
│   ├── GameCheckIn/              # managed-code 源码；name = game-checkin
│   │   ├── plugin.json
│   │   ├── src/                  # .csproj 与 C# 源码
│   │   └── web/                  # 可选 Frontend API 模块与静态资源
│   ├── EmulatorSupport/           # managed-code 模拟器 provider；name = emulator-support
│   │   ├── plugin.json
│   │   └── src/                  # Plugin API v1.7 驱动实现
│   └── CustomWallpaper/          # managed-code 源码
└── specialized/
    ├── BetterGI/                 # data-specialized 源码；name = bettergi
    │   ├── plugin.json
    │   ├── store.json
    │   └── data/                 # resolve、judge 与配置脚本
    └── MaaEnd/                   # 同类专项插件
```

- `NexusPipeline-Plugins/plugins/general/` 与 `plugins/specialized/` 下的每个子目录视为一个源码插件；schema 2 的物理目录名必须与 `artifactName` 完全一致，`plugin.json` 无效或 data 引用缺失时仅记警告跳过（不崩溃）。安装后的运行目录仍为扁平 `plugins/<artifactName>/`。
- 官方仓库由每个源码插件目录的 `plugin.json`、`store.json` 和 CI 生成的 `packages/`、根目录 `catalog.json` 组成；客户端只信任固定官方源，下载后再次检查 manifest。`catalog.json` 中的包地址、SHA256、大小和生成时间属于生成事实。
- 数据化插件默认启用，managed-code 插件默认禁用。用户选择会写入 `AppSettings.PluginPreferences`，启停在重启后生效。



## plugin.json（根文件）

运行时 manifest 使用 schema 2，至少声明 `schemaVersion: 2`、小写 kebab-case 的 `name`、严格区分大小写的 `artifactName`、受限 Nexus 版本 `version` 和插件类型。版本格式为 `major.minor.patch`、`major.minor.patch-beta.N` 或 `major.minor.patch-rc.N`，排序遵循 `beta < rc < stable`。需要本地化时，增加 `localization.defaultLocale` 与 `localization.locales`，资源必须随 ZIP 放在 `i18n/` 目录。`artifactName` 必须与源码目录、宿主安装目录、`packages/` 目录及 ZIP 前缀完全一致。

```json
{
  "schemaVersion": 2,
  "name": "bettergi",
  "artifactName": "BetterGI",
  "displayName": "BetterGI",
  "gameName": "原神",
  "description": "BetterGenshinImpact 专项脚本实例配置接管（自动推导主程序、配置、日志路径与自启动参数）",
  "version": "0.1.0",
  "kind": "data-specialized",
  "minHostVersion": "0.12.8",
  "resolve": "data/resolve.json",
  "judgeScript": "data/judge.js",
  "configEditor": "data/config-editor.js"
}
```

| 字段 | 说明 |
|---|---|
| `schemaVersion` | manifest 格式版本；必须为 `2` |
| `name` | 稳定机器标识（脚本实例 `PluginType` 引用）；必须使用小写 kebab-case |
| `artifactName` | 源码、宿主安装、发行目录和 ZIP 的正式物理身份；ASCII 字母/数字，首字符为字母且至少包含一个大写字母，大小写必须与目录和文件名完全一致 |
| `displayName` / `gameName` | 列表显示名 / 中文游戏名（脚本卡片徽章「{gameName}专项」） |
| `description` / `version` | 插件说明 / 受限 Nexus 版本（插件页展示） |
| `minHostVersion` | 可选的最低宿主版本；使用同一受限格式，缺省按 `0.0.0` 处理。宿主版本低于该值时保留插件元数据并标记为不兼容，不解析配置、能力或 managed-code 程序集 |
| `resolve` | 推导配置文件（相对插件目录） |
| `judgeScript` | 判断脚本文件（扩展名决定语言：`.js` → javascript / `.py` → python） |
| `configValidator` | v0.16.9 已退役；声明此字段的旧包会被拒绝并提示升级到 taskProtocol 配置诊断或卸载；不会修改用户配置 |
| `configEditor` | 配置编辑准备阶段运行的可选工作副本调整脚本；仅 `data-specialized` 可声明，必须是插件目录内存在的 `.js` 文件 |
| `capabilities` | 可选能力 key 数组。已接入宿主语义的 key：`emulator`（脚本实例可选「安卓模拟器」启动方式）、`self-managed-pc-launch`（PC 客户端启动由脚本自身含启动器完成；脚本弹窗在选择「PC 客户端」时关闭并禁用「启动游戏」开关、禁用启动参数与等待秒数，游戏路径保留填写用于任务失败时强制关闭游戏；持久化启动开关、参数和等待时间保留，仅在运行时生成宿主启动计划约束）、`execution-preview-client`、`no-fresh-config`（插件不允许使用全新配置文件模式） |

`self-managed-pc-launch` 的持久化启动开关、参数和等待时间保持用户设置；能力只在 PC 模式生成运行时宿主启动计划约束，切换到模拟器模式时可恢复原设置。



### 配置诊断与编辑准备

配置编辑提交和脚本实例保存后的检查统一使用 taskProtocol `discover.configAssessment`。没有声明 taskProtocol 的旧包不运行旧校验；若仍声明 `configValidator`，Host 拒绝加载并保留已有文件与用户快照。

`configEditor` 在配置编辑目标软件启动前运行（trigger=`config-edit-preparation`）。`resolve.json` 可声明 `configEdit.isolateSiblingCandidates`，宿主按当前 profile 的实际候选文件或目录建立 `work/edit-isolation`；`configEdit.freshInput` 可为 fresh 编辑提供稳定的输入名和值。编辑器脚本读取 `nexus.input.mode`、`nexus.input.configInputName`、`nexus.input.configInputValue` 和 `nexus.input.extras`，其中 `@extra<序号>/` 指向附加配置工作副本并允许写入，主配置写入请求会被拒绝。脚本失败、超时或写入失败会阻断目标程序启动并回滚准备现场。

脚本入口可使用稳定输入 DTO `nexus.input`：

```json
{
  "trigger": "config-edit-preparation",
  "script": {
    "id": "...", "name": "...", "pluginType": "...", "rootPath": "...",
    "mainExe": "...", "args": "...", "configPath": "...", "logPath": "...",
    "launchGame": false, "gameMode": "...", "gameExe": "...", "gameArgs": "...",
    "gameWaitSeconds": 0, "forceCloseGame": false, "maxAttempts": 1,
    "logStallTimeoutMinutes": 0, "totalTimeoutMinutes": 0, "autoUpdateConfig": true
  },
  "user": { "userId": "...", "userName": "..." },
  "snapshot": { "files": [{ "path": "config.json", "size": 123 }] },
  "extras": [{ "path": "DATA/CONFIGS/software_config.json", "files": [{ "path": "software_config.json", "size": 45 }] }]
}
```

可用 API 为 `nexus.listFiles()`、`nexus.readFile(path)`、`nexus.writeFile(path, content)`、`nexus.exists(path)`、`nexus.toast(message, kind)` 和 `nexus.notify(title, body, kind)`。文件参数必须是受控根目录内的相对路径；配置校验器访问 `@extra<序号>/` 时保持只读，配置编辑器访问对应工作副本时允许写入。读写单文件上限为 2 MiB，执行时长上限为 5 秒，并限制文件列表和反馈数量。写入采用单文件原子替换；接口不提供删除文件、多文件事务、网络、进程、PowerShell、Node.js、Python、CLR 或环境变量能力。

候选响应同时返回实际使用的 `inputName`，调用方按该名称把候选传入复用编辑会话；用户绑定在编辑成功保存后提交，取消或失败不会改变已有绑定，避免在多输入 profile 中凭字段顺序推测输入。



## 构建与部署

- 插件仓库由 GitHub Actions 在只读构建任务中生成 ZIP 和 catalog，合并后由 Bot commit 写入 `packages/<ArtifactName>/`；宿主从 catalog 的官方 raw 地址下载，主程序更新不会覆盖用户插件目录。
- 修改插件文件后重启服务生效；`/api/status` 的 `plugins` 列表可见，新建脚本选择卡片层出现「新建{displayName}专项脚本实例」。

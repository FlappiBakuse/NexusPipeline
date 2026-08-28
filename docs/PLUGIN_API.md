# NexusPipeline 插件 API 与包规范

数据化专项插件保持纯目录形态，同时支持 `managed-code` C# 插件。插件实现位于独立的 `NexusPipeline-Plugins` 仓库；安装包解压后共用运行目录 `plugins/<名称>/plugin.json` 发现入口。代码插件通过主仓库提供的 `NexusPipeline.Plugin.Abstractions` Plugin API v1 与宿主交互。

插件作者的实践文档位于 [NexusPipeline-Plugins](https://github.com/FlappiBakuse/NexusPipeline-Plugins)：[仓库概览](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/README.md)、[贡献指南](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/CONTRIBUTING.md)、[数据化专项插件开发](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/DATA_SPECIALIZED_PLUGIN.md)、[判断脚本开发](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/JUDGE_SCRIPT.md)、[打包与发布](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/RELEASING.md)。本文件保留宿主实际支持的规范性契约，插件仓库文档负责贡献与发布工作流。

## 目录结构

```
NexusPipeline-Plugins/plugins/
├── bettergi/                     # 插件名 = 目录名
│   ├── plugin.json               # 根文件：元数据 + 引用 data 文件（初始化专项插件）
│   └── data/
│       ├── resolve.json          # 推导配置（require 校验 + paths 模板）
│       ├── judge.js              # 判断脚本（.js = 内置 Jint 引擎 / .py = 系统 python.exe）
│       └── config-template/      # 可选：默认配置模板目录（编辑会话生成用）
│           └── NexusPipeline.json
├── march7th/       （plugin.json + data/{resolve.json, judge.js}）
├── zzzonedragon/   （同构）
└── maaend/         （同构）
```

- `NexusPipeline-Plugins/plugins/` 下的每个子目录视为一个插件；`plugin.json` 无效或 data 引用缺失时仅记警告跳过（不崩溃）。
- 官方仓库通过根目录 `catalog.json` 发布当前版本和包校验信息；客户端只信任固定官方源，下载后再次检查 manifest。
- 数据化插件默认启用，managed-code 插件默认禁用。用户选择会写入 `AppSettings.PluginPreferences`，启停在重启后生效。

## managed-code C# 插件（Plugin API v1）

代码插件必须在独立项目中引用 `src/NexusPipeline.Plugin.Abstractions/`，宿主不会向插件公开 `IServiceProvider`、`AppSettings`、`ScriptInstance` 或 `RunRecord`。插件由 `AssemblyLoadContext` 隔离加载，入口程序集从 manifest 声明，禁用或 API 不兼容时不会加载程序集。

```text
plugins/check-in/
├── plugin.json
└── CheckInPlugin.dll
```

```json
{
  "schemaVersion": 1,
  "name": "check-in",
  "displayName": "自动签到",
  "description": "按计划执行通用签到任务",
  "version": "0.1.0",
  "kind": "managed-code",
  "apiVersion": "1.0",
  "entryAssembly": "CheckInPlugin.dll",
  "entryType": "CheckInPlugin.EntryPoint",
  "capabilities": ["background-jobs"]
}
```

入口类型实现 `INexusPlugin` 的 `InitializeAsync`、`StartAsync`、`StopAsync` 生命周期；`IPluginHostContext` 提供插件日志、JSON 配置、DPAPI 密钥、宿主通知和后台任务调度。后台任务通过 `IPluginJobScheduler.Register` 注册，插件停止时统一取消，单任务异常不会穿透宿主。

`capabilities` 仅作为发现元数据。`script-profile` 等未来能力需要宿主明确接入；`background-jobs` 不会被当作专项脚本选择器。代码插件默认关闭，启用后需重启服务；运行状态可在 `/api/status` 的 `configuredEnabled`、`runtimeEnabled`、`state` 和 `error` 字段中查看。

## plugin.json（根文件）

```json
{
  "name": "bettergi",
  "displayName": "BetterGI",
  "gameName": "原神",
  "description": "BetterGenshinImpact 专项脚本实例配置接管（自动推导主程序、配置、日志路径与自启动参数）",
  "version": "0.1.0",
  "kind": "data-specialized",
  "resolve": "data/resolve.json",
  "judgeScript": "data/judge.js",
  "configTemplate": "data/config-template"
}
```

| 字段 | 说明 |
|---|---|
| `name` | 插件标识（脚本实例 `PluginType` 引用；改名的旧实例需同步修改） |
| `displayName` / `gameName` | 列表显示名 / 中文游戏名（脚本卡片徽章「{gameName}专项」） |
| `description` / `version` | 插件说明 / 版本（插件页展示） |
| `resolve` | 推导配置文件（相对插件目录） |
| `judgeScript` | 判断脚本文件（扩展名决定语言：`.js` → javascript / `.py` → python） |
| `configTemplate` | 可选：默认配置模板目录（编辑用户配置会话中 ConfigPath 不存在时整体复制到配置位置） |

## resolve.json（推导配置）

```json
{
  "require": [
    { "var": "launcher", "file": "March7th Launcher.exe" },
    { "var": "assistant", "file": "March7th Assistant.exe", "searchUpward": true }
  ],
  "paths": {
    "mainExe": "{launcher}",
    "args": "{rel:assistant}",
    "configPath": "config.yaml",
    "logPath": "logs/{YYYY-MM-DD}.log"
  }
}
```

- **require**：全部满足才推导成功（替代 DLL 时代的 `File.Exists` 校验）。`file` 相对脚本根目录；`var` 将匹配到的绝对路径绑定为变量；`searchUpward: true` 时根目录找不到则逐级向上搜索（最多 4 层，March7th 管理端/执行端分离场景）。
- **paths**：`mainExe` / `args` / `configPath` / `logPath` 四项。
  - 占位符 `{var}` = 绑定文件绝对路径；`{rel:var}` = 相对脚本根目录的相对路径（运行时启动目标语义，同目录结果带 `.\` 前缀）。**占位符仅整体替换**：整项命中即替换为该路径，不支持路径文本内嵌入拼接（如 `C:\dir\{var}` 的模板会丢弃前缀只保留 `{var}` 解析值）；需要组合路径时请用无占位符的相对拼接。
  - 无占位符：路径字段按相对脚本根目录拼接；`args` 原样返回（参数文本）。
  - `mainExe` 推导后必须存在（require 覆盖或文件真实存在），否则推导失败（前端保存被拒）。

## 判断脚本

- 契约与通用判断脚本一致：输入 `__NEXUS_INPUT__`（JS）/ 输入 JSON 路径（Python），输出 stdout 尾行 `{"status":"success|failed","reason":"…","notifyText":"…","replaceConfigs":[…]}`；宿主固化 `JudgeScriptEnabled=true` 且用户不可编辑（专项弹窗不渲染自定义完成标志区）。
- 语言按扩展名自动识别：`.js`（内置 Jint 引擎）/ `.py`（系统 python.exe）。

## 配置还原描述（config-restore.json）

**自动更新配置**（专项恒开）下，判断脚本插队文件（`replaceConfigs` 目标）在运行收尾同步快照前，宿主会按还原描述把任务启停字段还原为初始值，再连同运行后计数/其他字段一并写入用户快照 store（保留游戏脚本自身写入的完成记录/计数/新任务）。

- **写入时机**：判断脚本**首次触发**时（任意判定前）用 `nexus.writeFile("config-restore.json", ...)` 写入 script 目录根；跨尝试只写一次（以 `nexus.listFiles()` 检查存在性）。文件随运行结束自动清空。
- **提取内容**：读取 config 中「初始任务启停映射」——array 型取任务数组全部 `keyField → enabled`；map 型取启停对象全部键值。
- **契约格式**：

```json
{
  "files": [
    {
      "file": "mxu-MaaEnd.json",
      "toggles": [
        {
          "type": "array",
          "path": "instances[id=main].tasks",
          "keyField": "id",
          "enabledField": "enabled",
          "initial": { "t1": true, "t2": true, "t3": true }
        }
      ]
    },
    {
      "file": "NexusPipeline.json",
      "toggles": [
        { "type": "map", "path": "TaskEnabledList", "initial": { "<guid>": true } }
      ]
    }
  ]
}
```

- `file`：相对 config 的路径（目录型 ConfigPath 相对路径；文件型 = 文件名，须与 `replaceConfigs` 项一致）。
- array 型：按 `path` 定位 JSON 数组（DSL 支持 `标识符[下标].标识符` 与 `标识符[key=value].标识符` 链），元素取 `keyField` 查 `initial`，命中则设 `enabledField` 为对应布尔；**未覆盖元素保持当前值**（脚本更新新增的任务不被误改）。优先使用稳定 ID 选择实例，避免实例数组重排导致还原错误。
- map 型：`path` 为 JSON 对象键，遍历 `initial` 逐键设布尔；**未覆盖键保持当前值**。
- 仅作用于插队文件（`replaceConfigs` 清单内）；还原描述缺失/解析失败/应用失败时，该文件按「无还原描述」处理（不写入快照）。
- 现有专项实现参考：`maaend/data/judge.js`（array 型，`instances[id=...].tasks`）、`bettergi/data/judge.js`（map 型，`TaskEnabledList`）。

## 默认配置模板（config-template/）

- 编辑用户配置会话 start 时若 `ConfigPath` 不存在且插件提供 `config-template/` 目录 → 目录内容**整体复制**到配置位置（configPath 父目录），cancel 时按复制清单精确清理（清单随 `.session` 标记持久化，重启崩溃恢复同样生效）。
- 建议放入「可直接使用的默认配置」而非空模板；BetterGI 示例为内置标准任务列表的 NexusPipeline.json。

## 构建与部署

- 插件仓库单独构建 ZIP 并发布到 GitHub Release；主程序 `release/plugins/.nxp-root` 仅作为旧版本更新器的兼容根标记，主程序更新不会覆盖用户插件目录。
- 修改插件文件后重启服务生效；`/api/status` 的 `plugins` 列表可见，新建脚本选择卡片层出现「新建{displayName}专项脚本实例」。

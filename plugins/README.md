# NexusPipeline 专项插件（数据化形态）开发指南

v0.6.3 起专项插件为**纯数据目录形态**（不再编译 DLL）：每个插件一个文件夹，放入 `plugin.json`（根文件，初始化插件）与 `data/`（推导配置、判断脚本、可选默认配置模板）。无需编译即可增删改，随 `build.cmd` 整体复制到 `release/plugins/`。

## 目录结构

```
plugins/
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

- `plugins/` 下的每个子目录视为一个插件；`plugin.json` 无效或 data 引用缺失时仅记警告跳过（不崩溃）。
- 随附四个插件即此形态；用户可整体复制目录自定义插件，可删可替换。外部插件默认启用，显式禁用记入设置 `DisabledPlugins`（重启后仍禁用）。

## plugin.json（根文件）

```json
{
  "name": "bettergi",
  "displayName": "BetterGI",
  "gameName": "原神",
  "description": "BetterGenshinImpact 专项脚本实例配置接管（自动推导主程序、配置、日志路径与自启动参数）",
  "version": "0.1.0",
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

## 默认配置模板（config-template/）

- 编辑用户配置会话 start 时若 `ConfigPath` 不存在且插件提供 `config-template/` 目录 → 目录内容**整体复制**到配置位置（configPath 父目录），cancel 时按复制清单精确清理（清单随 `.session` 标记持久化，重启崩溃恢复同样生效）。
- 建议放入「可直接使用的默认配置」而非空模板；BetterGI 示例为内置标准任务列表的 NexusPipeline.json。

## 构建与部署

- `build.cmd` 将 `plugins/` 整体复制到 `release/plugins/`（无编译步骤）；部署即整体拷贝 `release/`。
- 修改插件文件后重启服务生效；`/api/status` 的 `plugins` 列表可见，新建脚本选择卡片层出现「新建{displayName}专项脚本实例」。

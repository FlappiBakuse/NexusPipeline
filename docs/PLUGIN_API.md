# NexusPipeline 插件 API 与包规范

数据化专项插件保持纯目录形态，同时支持 `managed-code` C# 插件。插件实现位于独立的 `NexusPipeline-Plugins` 仓库；安装包解压后共用运行目录 `plugins/<artifactName>/plugin.json` 发现入口。代码插件通过主仓库提供的 `NexusPipeline.Plugin.Abstractions` Plugin API v1.4 与宿主交互。`plugin.json.name` 是稳定的小写 kebab-case 机器 ID，`artifactName` 是严格区分大小写的源码、安装、发行目录与 ZIP 身份；配置、密钥、作用域和偏好仍以机器 ID 隔离。

插件作者的实践文档位于 [NexusPipeline-Plugins](https://github.com/FlappiBakuse/NexusPipeline-Plugins)：[仓库概览](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/README.md)、[贡献指南](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/CONTRIBUTING.md)、[数据化专项插件开发](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/DATA_SPECIALIZED_PLUGIN.md)、[判断脚本开发](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/JUDGE_SCRIPT.md)、[打包与发布](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/RELEASING.md)。本文件保留宿主实际支持的规范性契约，插件仓库文档负责贡献与发布工作流。

## 目录结构

```
NexusPipeline-Plugins/plugins/
├── BetterGI/                     # artifactName；plugin.json.name = bettergi
│   ├── plugin.json               # 根文件：元数据 + 引用 data 文件（初始化专项插件）
│   ├── store.json                # 商店展示元数据与更新记录
│   └── data/
│       ├── resolve.json          # 推导配置（require 校验 + paths 模板）
│       ├── judge.js              # 判断脚本（.js = 内置 Jint 引擎 / .py = 系统 python.exe）
│       └── config-template/      # 可选：默认配置模板目录（编辑会话生成用）
│           └── NexusPipeline.json
├── GameCheckIn/                  # 可选 managed-code 插件；name = game-checkin
│   ├── plugin.json
│   ├── CheckInPlugin.dll
│   └── web/                      # 可选：Frontend API 模块与静态资源
│       ├── main.js
│       └── style.css
├── March7thAssistant/（plugin.json + store.json + data/{resolve.json, judge.js}）
├── ZenlessZoneZeroOneDragon/（同构）
└── MaaEnd/           （同构）
```

- `NexusPipeline-Plugins/plugins/` 下的每个子目录视为一个插件；schema 2 的物理目录名必须与 `artifactName` 完全一致，`plugin.json` 无效或 data 引用缺失时仅记警告跳过（不崩溃）。
- 官方仓库由每个插件目录的 `plugin.json`、`store.json` 和当前 ZIP 生成根目录 `catalog.json`；客户端只信任固定官方源，下载后再次检查 manifest。`catalog.json` 中的包地址、SHA256、大小和生成时间属于生成事实。
- 数据化插件默认启用，managed-code 插件默认禁用。用户选择会写入 `AppSettings.PluginPreferences`，启停在重启后生效。

## managed-code C# 插件（Plugin API v1.4）

代码插件必须在独立项目中引用 `src/NexusPipeline.Plugin.Abstractions/`，宿主不会向插件公开 `IServiceProvider`、`AppSettings`、`ScriptInstance` 或 `RunRecord`。插件由 `AssemblyLoadContext` 隔离加载，入口程序集从 manifest 声明，禁用或 API 不兼容时不会加载程序集。

宿主当前 API 版本为 `1.4`：主版本必须相同，插件 minor 版本必须小于或等于宿主 minor 版本，因此 `1.0` 至 `1.4` 插件可加载，`2.0` 插件会被拒绝。

```text
plugins/GameCheckIn/
├── plugin.json
└── CheckInPlugin.dll
```

```json
{
  "schemaVersion": 2,
  "name": "check-in",
  "artifactName": "GameCheckIn",
  "displayName": "用户脚本扩展",
  "description": "提供通用的用户级扩展设置",
  "version": "0.1.0",
  "kind": "managed-code",
  "apiVersion": "1.4",
  "entryAssembly": "CheckInPlugin.dll",
  "entryType": "CheckInPlugin.EntryPoint",
  "capabilities": ["background-jobs", "ui-contributions", "frontend-module"],
  "frontend": {
    "apiVersion": "1.0",
    "entry": "web/main.js",
    "styles": ["web/style.css"]
  }
}
```

入口类型实现 `INexusPlugin` 的 `InitializeAsync`、`StartAsync`、`StopAsync` 生命周期；`IPluginHostContext` 提供插件日志、JSON 配置、DPAPI 密钥、宿主通知和后台任务调度。后台任务通过 `IPluginJobScheduler.Register` 注册，插件停止时统一取消，单任务异常不会穿透宿主。

实现 v1.1 能力的插件应在初始化时检查 `context is IPluginHostContextV1_1`；需要用户列表徽章的 v1.2 插件应检查 `context is IPluginHostContextV1_2`；需要 v1.3 扩展端口的插件应检查 `context is IPluginHostContextV1_3`，不满足时清晰拒绝初始化。v1.1 附加端口如下：

- `IPluginUserDataStore`：按用户读写 JSON 配置与 DPAPI 密钥。配置路径为 `config/plugins/<机器 ID>/users/<用户 ID>.json`，密钥路径为同目录下的 `<用户 ID>.secrets.json`。删除全局用户时宿主会清理该用户在所有插件中的用户文件；插件禁用或初始化失败不影响清理。物理安装目录使用 artifactName，不参与这些逻辑命名空间。
- `IPluginUserGlobalManagementRegistry`：注册声明式用户全局设置贡献。字段类型仅允许 `text`、`textarea`、`secret`、`switch`、`select`、`multi-select`、`status`；密钥读取只返回 `{configured:true|false}`，保存密钥必须使用 `{action:"keep"}`、`{action:"set",value:"..."}` 或 `{action:"clear"}`。
- `IPluginExecutionEventService`：订阅 `UserRunStarting`。事件只包含用户、脚本实例、队列、运行模式和开始时间等稳定标识；宿主异步调用处理器，处理器异常只记录警告，不能阻塞或改变执行。
- `IPluginHttpClientFactory`：创建遵循宿主代理设置的外网 `HttpClient`，插件无法读取 `AppSettings`。
- `IPluginUserListBadgeRegistry`（v1.2）：注册按用户返回单个聚合徽章的轻量读取处理器。返回 `null` 表示该用户不显示徽章；处理器应只读取本地插件状态，不执行网络请求。

宿主通用设置接口为 `GET /api/plugin-contributions/user-global/{userId}` 与 `PUT /api/plugin-contributions/user-global/{userId}/{pluginName}/{contributionId}`。插件未启用或贡献不存在返回 `404 contribution_not_found`，贡献处理器异常返回 `500 plugin_error`。

用户列表徽章接口为 `GET /api/plugin-contributions/user-list-badges`，一次返回全部用户的徽章快照。每个徽章由宿主投影为 `pluginName`、`pluginDisplayName`、`id`、`label`、`tone`、`title` 和 `order`；`tone` 仅允许 `muted`、`blue`、`ok`、`warn`、`bad`，无效徽章会被记录并丢弃，不影响用户列表。

### v1.3 通用扩展端口

`IPluginHostContextV1_3` 在 v1.2 基础上增加 `Ui`、`ScopedData`、`WebApi` 和 `History`。这些端口只使用稳定的字符串、JSON DTO 和取消令牌，不暴露宿主 DI 容器、领域模型或 `HttpListenerContext`。

#### 声明式 UI

`context.Ui.Register(new PluginUiContribution(...))` 可向以下稳定 slot 注册 Form、Badge 或 Card：

```text
dashboard.cards                 dashboard.after-running
users.list.badges               users.binding.sections
users.global.sections           scripts.list.badges
scripts.editor.sections         queues.list.badges
queues.editor.sections          dispatch.cards
dispatch.running.badges         dispatch.running.sidecar
dispatch.run.sections
history.list.badges             history.detail.sections
settings.sections               shell.nav
```

每个贡献包含稳定 `id`、`slot`、`kind`、标题、说明、排序值和可选字段。字段类型包括 `text`、`textarea`、`secret`、`switch`、`select`、`multi-select`、`status`，以及 v1.3 的 `number`、`color`、`range`、`url`。上下文使用 `PluginUiContext(Slot, Mode, PrimaryId, SecondaryId)`；例如脚本编辑器可用 `PrimaryId` 表示脚本实例，用户绑定设置可同时传入用户和脚本 ID。

宿主提供通用 HTTP 投影：

- `POST /api/plugin-contributions/ui/query`：body 为 `{ "slot": "settings.sections", "contexts": [...] }`，批量读取指定 slot 的贡献；
- `PUT /api/plugin-contributions/ui/{pluginName}/{contributionId}`：body 为 `{ "context": {...}, "values": {...} }`，提交表单；
- `POST /api/plugin-contributions/ui/{pluginName}/{contributionId}/action/{action}`：提交带动作名和字段值的 Card/Form 操作。

读取结果、保存值和动作返回值均经过字段名、数量、类型、长度和只读字段校验。`secret` 读取只返回 `{configured:true|false}`，保存使用 `{action:"keep"}`、`{action:"set",value:"..."}` 或 `{action:"clear"}`；`status` 字段为只读。处理器异常、超时或无效返回会隔离在对应贡献内。

#### 作用域数据

`IPluginScopedDataStore` 以插件为顶级隔离边界，scope 只允许安全的 ASCII 段，数据保存于 `config/plugins/<插件名>/scopes/`。建议使用以下约定：`global`、`user/<userId>`、`script/<scriptId>`、`queue/<queueId>`、`user-script/<userId>/<scriptId>`。读写接口支持 JSON 和泛型对象，宿主拒绝绝对路径、反斜杠、`.`、`..` 及越界段。

删除全局用户、脚本实例、调度队列或用户脚本绑定时，宿主会清理对应作用域文件。历史数据中的插件展示快照与插件作用域数据相互独立；卸载插件不会回写或重写历史记录。

#### 插件自有 Web API

插件可通过 `context.WebApi.Register(new PluginWebApiRoute("GET", "health", handler))` 注册自己的路由。最终地址为 `/api/plugin-api/{pluginName}/health`，支持 `GET`、`POST`、`PUT`、`PATCH`、`DELETE`。handler 收到 `PluginWebApiRequest`（方法、规范化相对路由、查询字典、可选 JSON body），返回 `PluginWebApiResponse.Json(...)` 或 `PluginWebApiResponse.Empty(204)`。

宿主为每次调用设置 30 秒超时，并限制 JSON 响应为 2 MiB；未知路由、无效状态码、超时、异常和无效 JSON 响应均使用 `{ "ok": false, "code": "plugin_error", "error": "..." }` 形式处理。路由只能由注册它的插件访问，路径段拒绝空段、反斜杠及 `.`/`..`。

#### 历史展示

`context.History.Register(new PluginHistoryContribution(...))` 可在运行历史保存前生成纯文本展示快照。快照只允许标题、徽章和字段，单个插件贡献最多 16 KiB，全部插件单次运行最多 64 KiB；处理器最多执行 5 秒。快照写入 `RunRecord.PluginHistory`，不参与状态、尝试次数、结果和通知判定，插件卸载后仍可由历史页面展示。

### 前端插件运行时（Frontend API 1.2）

前端扩展与 C# API 独立版本化。manifest 同时声明 `frontend-module` capability 和 `frontend` 对象：

```json
"capabilities": ["frontend-module"],
"frontend": {
  "apiVersion": "1.2",
  "entry": "web/main.js",
  "styles": ["web/style.css"]
}
```

入口 ES module 必须导出 `activate(host)`。宿主通过 `GET /api/plugin-runtime/frontend` 发布已启用、API 兼容的安全描述，动态加载入口并按需注入样式。插件 host 提供：

- `host.api.get/post/put/patch/delete(route, body, signal)`：访问插件自己的 `/api/plugin-api/` 命名空间；
- `host.actions.register(id, handler)`：注册带 `plugin:<name>:` 前缀的全局 action；
- `host.routes.register(route, handler)`：注册 `#/plugin/<name>/<route>` 页面路由；
- `host.nav.register({ id, title, route, icon, order })`：向 `shell.nav` 增加导航项；
- `host.slots.register(slot, renderer)`：接入稳定 UI slot，自定义 renderer 可返回清理函数；
- `host.ui.query/save/action(...)`：使用声明式 UI 贡献接口；
- `host.lifecycle.onPageEnter/onPageLeave/onPageUpdated/onDispose(...)`：订阅页面生命周期；
- `host.appearance`：注册主题、设置 CSS token、应用主题和访问外观服务。
- `host.appearance.wallpaperStore`：按当前插件身份读取、上传、删除服务端壁纸，保存轮换与效果设置，保存自动配色并订阅跨浏览器变化。
- `host.executionPreview.capture(runId, signal)`：按宿主当前运行目标读取受控的 PC 游戏客户区或模拟器画面；返回 360p JPEG 或等待状态。

前端模块运行在管理页面同源环境，可以使用 DOM、原生 ES module 和 CSS。启用且兼容的插件会直接加载其前端模块；宿主继续校验运行状态、Frontend API 兼容性、公开资源路径、扩展名和文件存在性。同源前端可以访问管理页面可用的 DOM 与请求能力，插件发布前应完成代码审查。

前端资源必须位于插件目录的 `web/` 下；宿主只允许 `GET`/`HEAD` 访问 `/plugin-assets/{plugin}/{relative}`，执行路径包含校验、扩展名白名单和文件存在校验，不提供目录浏览。允许的文件类型为 JS/MJS、CSS、JSON、SVG、PNG、JPG/JPEG、WEBP、GIF、ICO、WOFF/WOFF2。`plugin.json`、配置、密钥、程序集和调试符号不属于公开资源。

外观 API 使用 CSS Variables 作为主题 token；主题名称、token 名和值均经过长度和字符校验。`wallpaperStore` 的壁纸文件由宿主保存到 `user-assets/appearance/wallpapers/`，配置保存到 `config/appearance.json`，轮换游标保存到 `.nxp/state/appearance-runtime.json`。单张壁纸上限 8192 KB，最多 32 张且总容量上限 256 MiB；允许 JPEG、PNG、WebP，上传时校验 MIME、文件头和 SHA256。浏览器只缓存当前显示 Blob，服务端配置由宿主统一同步。

`wallpaperStore` 的 `get()` 返回 `revision`、`provider`、`assets`、`order`、`selectedId`、`currentId`、`rotation`、`effects` 和 `nextSwitchAt`。轮换模式为 `off`、`timer`、`startup`；`timer` 按间隔轮换，`startup` 在每次 Web 初始化时推进一次游标。自定义壁纸启用后仍保留宿主内置主题切换；插件应使用 `derivePalette(blob)` 生成完整实色 CSS token，并通过 `savePalette` 持久化。

`capabilities` 仅作为发现元数据，除已明确接入的 v1.3 扩展端口外不会自动获得业务语义。`script-profile` 等未来能力需要宿主明确接入；`background-jobs` 不会被当作专项脚本选择器。代码插件默认关闭，启用后需重启服务；运行状态可在 `/api/status` 的 `configuredEnabled`、`runtimeEnabled`、`state`、`hasFrontend`、`frontendApiVersion`、`replaces` 和 `error` 字段中查看。

插件管理页使用 `/api/plugins` 与 `/api/plugins/store` 获取列表，使用 `/api/plugins/{name}/detail` 与 `/api/plugins/store/{name}/detail` 获取详情。详情包含统一展示元数据、完整更新记录和受限 README；作者、标签、主页和 README 由插件仓库的 `store.json` 与包内容提供，更新时间取最新更新记录日期。

## plugin.json（根文件）

schema 2 的运行时 manifest 至少声明 `schemaVersion: 2`、小写 kebab-case 的 `name`、严格区分大小写的 `artifactName`、SemVer `version` 和插件类型。`artifactName` 必须与源码目录、宿主安装目录、`packages/` 目录及 ZIP 前缀完全一致；schema 1 仍可被宿主读取，并由启动迁移处理已知或 catalog 可推导的旧物理目录。

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
  "minHostVersion": "0.10.8",
  "resolve": "data/resolve.json",
  "judgeScript": "data/judge.js",
  "configTemplate": "data/config-template"
}
```

| 字段 | 说明 |
|---|---|
| `schemaVersion` | manifest 格式版本；当前为 `2`，宿主兼容旧版 `1` |
| `name` | 稳定机器标识（脚本实例 `PluginType` 引用）；必须使用小写 kebab-case，改名需通过 `replaces` 迁移 |
| `artifactName` | 源码、宿主安装、发行目录和 ZIP 的正式物理身份；ASCII 字母/数字，首字符为字母且至少包含一个大写字母，大小写必须与目录和文件名完全一致 |
| `displayName` / `gameName` | 列表显示名 / 中文游戏名（脚本卡片徽章「{gameName}专项」） |
| `description` / `version` | 插件说明 / SemVer 版本（插件页展示） |
| `minHostVersion` | 可选的最低宿主版本；缺省按 `0.0.0` 处理 |
| `replaces` | 可选的旧插件机器标识数组；商店安装时按跨重启事务迁移旧插件代码目录、配置、密钥、作用域和插件偏好 |
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

## 插件身份替换

插件机器标识参与脚本实例、配置、密钥、作用域和用户偏好隔离；artifactName 参与源码、安装和发行文件系统路径。需要改机器标识时，新插件在 manifest 和 catalog 中声明 `replaces`，宿主会在重启阶段完成一次可恢复的身份迁移：

1. 完成旧 pending journal 后，校验新旧身份与 staging 目录，旧代码目录进入稳定 backup，新的 artifactName 目录完成交换；
2. 将 `config/plugins/<旧名>.json`、`<旧名>.secrets.json` 和 `<旧名>/` 作用域目录移动为新身份；
3. 将 `AppSettings.PluginPreferences` 中的旧键迁移为新键，保留 `Enabled`；
4. 更新商店 `ownership.json` 并清理旧身份归属；
5. 任一阶段失败时保留 `pending.json`，下次启动从已记录阶段继续。

新旧身份同时存在时，宿主报告 `replacement-conflict` 并停止替换；同一 artifact 的多个大小写目录会报告 `layout-conflict`，保留全部现场并暂停相关自动安装/更新。`replaces` 每个条目最多 8 个安全机器标识，同一个旧身份在 catalog 中只能被一个新插件声明替换。

## 构建与部署

- 插件仓库单独构建 ZIP 并提交到 `packages/<ArtifactName>/`；宿主从 catalog 的官方 raw 地址下载，主程序 `release/plugins/.nxp-root` 仅作为旧版本更新器的兼容根标记，主程序更新不会覆盖用户插件目录。
- 修改插件文件后重启服务生效；`/api/status` 的 `plugins` 列表可见，新建脚本选择卡片层出现「新建{displayName}专项脚本实例」。

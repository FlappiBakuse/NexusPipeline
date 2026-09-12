# NexusPipeline 插件 API 与包规范

数据化专项插件保持纯目录形态，同时支持 `managed-code` C# 插件。插件实现位于独立的 `NexusPipeline-Plugins` 仓库；仓库源码按 `plugins/general/<artifactName>/`（managed-code）和 `plugins/specialized/<artifactName>/`（data-specialized）分类，发行目录 `packages/<artifactName>/` 保持扁平，安装包解压后共用运行目录 `plugins/<artifactName>/plugin.json` 发现入口。代码插件通过主仓库提供的 `NexusPipeline.Plugin.Abstractions` Plugin API v1.6 与宿主交互。`plugin.json.name` 是稳定的小写 kebab-case 机器 ID，`artifactName` 是严格区分大小写的源码、安装、发行目录与 ZIP 身份；配置、密钥、作用域和偏好仍以机器 ID 隔离。

插件作者的实践文档位于 [NexusPipeline-Plugins](https://github.com/FlappiBakuse/NexusPipeline-Plugins)：[仓库概览](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/README.md)、[贡献指南](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/CONTRIBUTING.md)、[数据化专项插件开发](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/DATA_SPECIALIZED_PLUGIN.md)、[判断脚本开发](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/JUDGE_SCRIPT.md)、[打包与发布](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/RELEASING.md)。本文件保留宿主实际支持的规范性契约，插件仓库文档负责贡献与发布工作流。

## 目录结构

```
NexusPipeline-Plugins/plugins/
├── general/
│   ├── GameCheckIn/              # managed-code 源码；name = game-checkin
│   │   ├── plugin.json
│   │   ├── src/                  # .csproj 与 C# 源码
│   │   └── web/                  # 可选 Frontend API 模块与静态资源
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

## managed-code C# 插件（Plugin API v1.6）

代码插件必须在独立项目中引用 `src/NexusPipeline.Plugin.Abstractions/`，宿主不会向插件公开 `IServiceProvider`、`AppSettings`、`ScriptInstance` 或 `RunRecord`。插件由 `AssemblyLoadContext` 隔离加载，入口程序集从 manifest 声明，禁用或 API 不兼容时不会加载程序集。

宿主当前 API 版本为 `1.6`：主版本必须相同，插件 minor 版本必须小于或等于宿主 minor 版本，因此 `1.0` 至 `1.6` 插件可加载，`2.0` 插件会被拒绝。

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
  "apiVersion": "1.6",
  "entryAssembly": "CheckInPlugin.dll",
  "entryType": "CheckInPlugin.EntryPoint",
  "capabilities": ["background-jobs", "ui-contributions", "frontend-module"],
  "frontend": {
    "apiVersion": "1.5",
    "entry": "web/main.js",
    "styles": ["web/style.css"]
  }
}
```

入口类型实现 `INexusPlugin` 的 `InitializeAsync`、`StartAsync`、`StopAsync` 生命周期；`IPluginHostContext` 提供插件日志、JSON 配置、DPAPI 密钥、宿主通知和后台任务调度。后台任务通过 `IPluginJobScheduler.Register` 注册，插件停止时统一取消，单任务异常不会穿透宿主。

实现 v1.1 能力的插件应在初始化时检查 `context is IPluginHostContextV1_1`；需要用户列表徽章的 v1.2 插件应检查 `context is IPluginHostContextV1_2`；需要 v1.3 扩展端口的插件应检查 `context is IPluginHostContextV1_3`；需要 v1.5 本地化端口的插件应检查 `context is IPluginHostContextV1_4`；需要 v1.6 资产端口的插件应检查 `context is IPluginHostContextV1_6`，不满足时清晰拒绝初始化。v1.1 附加端口如下：

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

宿主为每次调用设置 30 秒超时，并限制 JSON 响应为 2 MiB；未知路由、无效状态码、超时、异常和无效 JSON 响应均使用 `{ "ok": false, "code": "plugin_error", "args": {} }` 形式处理。错误响应只包含稳定机器码和机器可读参数，不传递本地化句子或异常文本；前端和调用方根据自身语言资源显示文字。路由只能由注册它的插件访问，路径段拒绝空段、反斜杠及 `.`/`..`。

#### 历史展示

`context.History.Register(new PluginHistoryContribution(...))` 可在运行历史保存前生成纯文本展示快照。快照只允许标题、徽章和字段，单个插件贡献最多 16 KiB，全部插件单次运行最多 64 KiB；处理器最多执行 5 秒。快照写入 `RunRecord.PluginHistory`，不参与状态、尝试次数、结果和通知判定，插件卸载后仍可由历史页面展示。

### v1.5 插件本地化

`IPluginHostContextV1_4.I18n` 提供插件自有资源查表、占位符替换和按当前请求语言进行的日期/时间/数字格式化。宿主只传入规范化的 `zh-CN` 或 `en-US` 请求语言，插件资源缺失时回退到 `defaultLocale`，再回退到调用方提供的 `fallback`。插件本地化资源不会复用宿主词典，也不会改变机器 ID、配置键或持久化结构。

managed-code 插件可以在 `plugin.json` 声明：

```json
"localization": {
  "defaultLocale": "zh-CN",
  "locales": {
    "zh-CN": "i18n/zh-CN.json",
    "en-US": "i18n/en-US.json"
  }
}
```

资源文件是有限大小的扁平 JSON 对象，所有 locale 的 key 集合必须一致；资源路径必须位于插件目录的 `i18n/` 下。声明式 UI、用户列表徽章和历史展示可以用 `PluginLocalizedText(Key, Fallback)` 携带语义引用，宿主按请求语言投影展示值，历史落盘保存引用和回退文本。

### v1.6 二进制资产存储

`IPluginHostContextV1_6.Assets` 提供按插件命名空间与逻辑 scope 隔离的二进制资产存储。宿主负责路径逃逸防护、原子写入和宿主级绝对上限；资产的业务配额、去重策略与语义由插件自行决定。

```csharp
ValueTask<PluginAssetInfo> WriteAsync(string scope, string extension, Stream content, CancellationToken cancellationToken = default);
ValueTask<PluginAssetContent?> OpenAsync(string scope, string assetId, CancellationToken cancellationToken = default);
ValueTask<bool> DeleteAsync(string scope, string assetId, CancellationToken cancellationToken = default);
ValueTask<IReadOnlyList<PluginAssetInfo>> ListAsync(string scope, CancellationToken cancellationToken = default);
```

- `PluginAssetInfo(Id, Scope, Extension, SizeBytes, CreatedAt)` 描述单个资产；`ListAsync` 按创建时间与 Id 稳定排序。
- `PluginAssetContent` 暴露 `Info` 与 `Content` 流；调用方负责释放该流。
- 资产 Id 是内容 SHA256 的小写十六进制（64 字符）。相同内容重复写入返回同一 Id，写入是内容寻址且幂等的。
- `extension` 接受 1–12 位 ASCII 字母数字，可带前导点；落盘统一转为小写。扩展名仅用于标识资产格式，宿主不校验实际内容。
- 资产落盘位置为 `config/plugins/{插件名}/assets/{scope}/{sha256}.{extension}`；scope 规则与 `IPluginScopedDataStore` 一致（最多 8 段、每段不超过 128 字符且仅允许 `A-Za-z0-9._-`、总长不超过 512），路径逃逸请求被拒绝。
- 宿主级绝对上限：单资产 16 MiB、单 scope 512 个资产、单 scope 512 MiB。写入使用同目录临时文件加原子替换，失败时不留 `.part` 残留。
- 插件应自行实现业务配额、清理策略与资产语义；卸载插件不自动删除其资产。

### v1.6 二进制 Web API 传输

插件 Web API 的请求与响应在 JSON 之外支持二进制传输。

- `PluginWebApiRequest` 增加 `ContentType`、`ContentLength` 与 `OpenBodyStream`。`ContentType` 为小写且去掉参数；没有请求体时 `OpenBodyStream` 为 `null`，有请求体时宿主在本次调用期间持有该流。JSON 请求体仍投影为 `JsonBody`。
- `PluginWebApiResponse.Binary(Stream content, string contentType, int statusCode = 200, long contentLength = -1)` 构造二进制响应；宿主读取后负责释放流。
- `PluginWebApiContentTypes.AllowedBinary` 允许 `image/png`、`image/jpeg`、`image/webp`、`image/gif`、`image/avif`、`application/octet-stream`。`Normalize` 去掉参数并转小写，`IsAllowedBinary` 判断是否在白名单内；白名单之外的响应类型按插件错误处理。
- 传输上限：请求体 16 MiB、JSON 响应 2 MiB、二进制响应 16 MiB。二进制响应附带 `X-Content-Type-Options: nosniff` 与 `Cache-Control: no-store`。
- 路由仍位于 `/api/plugin-api/{插件名}/{route}`，按 method 与完整 route 精确匹配；查询参数通过 `Query` 传递。

### 旧外观数据搬迁

宿主启动时执行一次性格式搬迁：读取旧外观配置、`user-assets/appearance/wallpapers/` 目录与旧轮换游标，把资产导入旧配置记录的原提供方插件命名空间（资产 scope 为 `wallpapers`），再把搬迁载荷原子写入该插件的 `legacy-appearance-import` 作用域数据。成功后写入标记 `.nxp/state/appearance-migration.json`；任一步失败都不写标记，下一次启动按同一入口重试。旧文件由宿主保留，插件是这些旧数据的唯一消费者。

搬迁载荷结构如下，其中 `id`、`order`、`selectedId` 与 `currentId` 已经是新的资产 Id：

```json
{
  "schemaVersion": 1,
  "migratedAt": "2026-09-12T00:00:00.0000000+00:00",
  "settings": {
    "selectedId": "<资产 Id>",
    "order": ["<资产 Id>"],
    "rotation": { "mode": "off|timer|startup", "intervalMinutes": 30, "epochUnixMs": 0 },
    "effects": { "blurPx": 0, "dimPercent": 20, "surfaceTransparencyPercent": 0, "applyTransparencyToSecondarySurfaces": true },
    "providerEnabled": false,
    "currentId": "<资产 Id，可选>"
  },
  "assets": [
    {
      "id": "<资产 Id>",
      "extension": "png",
      "originalName": "wallpaper.png",
      "mimeType": "image/png",
      "sizeBytes": 102400,
      "createdAt": "2026-09-12T00:00:00.0000000+00:00",
      "palette": { "--accent": "#62a0ff" }
    }
  ]
}
```

搬迁载荷中的 `currentId` 仅在旧轮换游标存在时出现，代表旧实现记录的当前壁纸，是否沿用由插件决定。插件应在初始化时消费该作用域记录，导入完成后删除该记录。

### 前端插件运行时（Frontend API 1.5）

前端扩展与 C# API 独立版本化。manifest 同时声明 `frontend-module` capability 和 `frontend` 对象：

```json
"capabilities": ["frontend-module"],
"frontend": {
  "apiVersion": "1.5",
  "entry": "web/main.js",
  "styles": ["web/style.css"]
}
```

入口 ES module 必须导出 `activate(host)`。宿主通过 `GET /api/plugin-runtime/frontend` 发布已启用、API 兼容的安全描述，动态加载入口并按需注入样式。插件 host 提供：

- `host.plugin`：当前插件的 manifest 描述（含 `name`、`displayName`、`version`、`frontendApiVersion`），冻结只读；
- `host.api.get/post/put/patch/delete(route, body, signal)`：以 JSON 语义访问插件自己的 `/api/plugin-api/` 命名空间；
- `host.api.blob(route, { query?, signal? })`：以 `GET` 读取二进制响应，返回 `Blob`；查询参数经 `query` 传入；
- `host.api.upload(route, body, { method?, contentType?, query?, signal? })`：发送二进制请求体并读取 JSON 响应；`method` 缺省 `POST`，`contentType` 缺省取 `body.type`，再回退到 `application/octet-stream`；
- `host.routes.register(route, handler)`：注册 `#/plugin/<name>/<route>` 页面路由；
- `host.nav.register({ id, title, route, icon, order })`：向 `shell.nav` 增加导航项；
- `host.slots.register(slot, renderer)`：接入稳定 UI slot。renderer 接收 `{ element, context }`，在自己的 surface 中挂载内容并返回清理函数；
- `host.ui.query/save/action(...)`：使用声明式 UI 贡献接口；`host.ui.toast(message, tone)` 显示宿主提示；
- `host.lifecycle.onPageEnter/onPageLeave/onPageUpdated/onDispose(...)`：订阅页面生命周期；
- `host.appearance`：通用外观表面，提供 `registerTheme(name, definition)`、`applyTheme(name)`、`setTokens(tokens)`、`clearTokens()`、`setBackground(surface)` 和 `clearBackground()`。
- `host.executionPreview.capture(runId, signal)`：按宿主当前运行目标读取受控的 PC 游戏客户区或模拟器画面；返回 360p JPEG 或等待状态。该接口用于运行预览，不等同于判断脚本的运行期通知截图。
- `host.i18n`：读取插件 manifest 中的本地化资源，提供 `locale`、`defaultLocale`、`t(key, args, fallback)` 和本地化日期/时间/数字格式化；资源仅属于当前插件。

稳定的外部契约包括 Frontend API `1.5` 精确版本、上述 `host.*` 能力、18 个公开 slot 名称、renderer surface 与 context 的可观察语义、公开 `nxp-*` 元素、主题 token、light DOM 下的可观察视觉与交互行为，以及 route/nav/slot/lifecycle 的挂载与清理语义。宿主侧桥接实现位于 `frontend/src/plugin-bridge/`，其文件划分、内部函数、宿主平台模块路径和宿主私有 class 都是内部实现，不构成插件公共 API。

前端模块运行在管理页面同源环境，可以使用 DOM、构建后的 ES module 和 CSS。Frontend API 采用精确版本匹配：只有 `1.5` 被接受，其他主次版本均拒绝加载，不提供兼容桥。启用且兼容的插件会直接加载其前端模块；宿主继续校验运行状态、Frontend API 版本、公开资源路径、扩展名和文件存在性。插件前端应使用 Vue/TypeScript/Vite 或等效构建链生成 `web/` 静态资源，通过公开 `nxp-*` Native Custom Elements 以及 slot surface 与宿主交互，不依赖宿主 Vue 内部实现。

公开元素注册表位于 `frontend/src/ui/register.ts` 的 `NEXUS_PUBLIC_ELEMENTS`，插件只应使用该注册表登记的元素。当前包含 `nxp-button`、`nxp-icon-button`、`nxp-badge`、`nxp-card`、`nxp-section-card`、`nxp-collapsible-card`、`nxp-field`、`nxp-text-input`、`nxp-text-area`、`nxp-select`、`nxp-number-input`、`nxp-switch`、`nxp-switch-setting`、`nxp-range`、`nxp-path-picker`、`nxp-file-picker`、`nxp-color-picker`、`nxp-time-picker`、`nxp-menu`、`nxp-tooltip`、`nxp-pager`、`nxp-modal`、`nxp-toast`、`nxp-spinner`、`nxp-empty-state`、`nxp-icon` 和 `nxp-loading-state`，共 27 个。

- `nxp-section-card`：props 为 `title`、`description` 和 `variant`（`primary` 或 `secondary`，默认 `primary`）；默认插槽为 body，具名插槽为 `header`、`description` 和 `actions`。
- `nxp-collapsible-card`：props 为 `title`、`description`、`expanded`（布尔，默认 `false`）和 `panel-id`；展开状态由调用方受控，展开变化时 emit `toggle`，事件负载在 `CustomEvent.detail[0]`；`panel-id` 同时用于 `aria-controls` 与 body 的 id；body 为默认插槽，header 右侧为 `actions` 具名插槽。

设置页的折叠卡片由宿主统一协调，插件可以接入同一协议：插件展开自己的卡片时向 window 派发 `nxp-settings-panel-toggle`（`detail` 为 `{ panelId }`，收起时 `panelId` 为 `null`），并监听 `nxp-settings-panel-state`（`detail` 为 `{ panelId }`）以收起其它卡片。字段帮助文案使用宿主工具提示约定：在控件容器上设置 `data-help="说明文字"`。

前端资源必须位于插件目录的 `web/` 下；宿主只允许 `GET`/`HEAD` 访问 `/plugin-assets/{plugin}/{relative}`，执行路径包含校验、扩展名白名单和文件存在校验，不提供目录浏览。允许的文件类型为 JS/MJS、CSS、JSON、SVG、PNG、JPG/JPEG、WEBP、GIF、ICO、WOFF/WOFF2。`plugin.json`、配置、密钥、程序集和调试符号不属于公开资源。

外观表面使用 CSS Variables 作为主题 token；主题名称、token 名和值均经过长度和字符校验。`setTokens` 的 token 名必须匹配 `--[A-Za-z0-9_-]{1,96}`，值不超过 4096 字符且不含控制字符；token 应用在 `body` 上，因此优先于主题 token 并跨主题切换保持有效，直到 `clearTokens()` 或替换新集合。`setBackground(surface)` 接受 `url`（仅 `http`、`https`、`blob` 和 `data` 协议，其余拒绝）、`blurPx`（0–40）、`dimPercent`（0–80）、`surfaceTransparencyPercent`（0–50）与 `secondarySurfaceTransparency`（布尔，默认 `true`）。外观变化继续广播 `nexus:appearance-changed`；`createAppearanceHost()` 不接收插件名参数。

壁纸配置、配额、文件校验、去重、轮换、配色与持久化属于插件业务，由插件通过自己的资产 scope、插件 Web API 与前端模块实现；宿主只提供通用资产存储、二进制 Web API 与通用外观表面。旧宿主壁纸数据的搬迁见上文「旧外观数据搬迁」。

`capabilities` 仅作为发现元数据，除已明确接入的 v1.3 扩展端口外不会自动获得业务语义。`script-profile` 等未来能力需要宿主明确接入；`background-jobs` 不会被当作专项脚本选择器。代码插件默认关闭，启用后需重启服务；运行状态可在 `/api/status` 的 `configuredEnabled`、`runtimeEnabled`、`state`、`hasFrontend` 和 `frontendApiVersion` 字段中查看。前端描述中的 `defaultLocale` 与 `localization` 只包含该插件已声明并通过校验的资源。

插件管理页使用 `/api/plugins` 与 `/api/plugins/store` 获取列表，使用 `/api/plugins/{name}/detail` 与 `/api/plugins/store/{name}/detail` 获取详情。详情包含统一展示元数据、完整更新记录和受限 README；作者、标签、主页和 README 由插件仓库的 `store.json` 与包内容提供，创建时间取 `store.json.createdAt`（插件第一次正式公开发布日期），更新时间取最新更新记录日期。旧 catalog 缺少 `createdAt` 时按空值展示并保持可读取。

## plugin.json（根文件）

运行时 manifest 使用 schema 2，至少声明 `schemaVersion: 2`、小写 kebab-case 的 `name`、严格区分大小写的 `artifactName`、SemVer `version` 和插件类型。需要本地化时，增加 `localization.defaultLocale` 与 `localization.locales`，资源必须随 ZIP 放在 `i18n/` 目录。`artifactName` 必须与源码目录、宿主安装目录、`packages/` 目录及 ZIP 前缀完全一致。

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
  "configValidator": "data/config-validator.js",
  "configEditor": "data/config-editor.js"
}
```

| 字段 | 说明 |
|---|---|
| `schemaVersion` | manifest 格式版本；必须为 `2` |
| `name` | 稳定机器标识（脚本实例 `PluginType` 引用）；必须使用小写 kebab-case |
| `artifactName` | 源码、宿主安装、发行目录和 ZIP 的正式物理身份；ASCII 字母/数字，首字符为字母且至少包含一个大写字母，大小写必须与目录和文件名完全一致 |
| `displayName` / `gameName` | 列表显示名 / 中文游戏名（脚本卡片徽章「{gameName}专项」） |
| `description` / `version` | 插件说明 / SemVer 版本（插件页展示） |
| `minHostVersion` | 可选的最低宿主版本；缺省按 `0.0.0` 处理 |
| `resolve` | 推导配置文件（相对插件目录） |
| `judgeScript` | 判断脚本文件（扩展名决定语言：`.js` → javascript / `.py` → python） |
| `configValidator` | 配置编辑完成后运行的可选配置校验/自修复脚本；仅 `data-specialized` 可声明，必须是插件目录内存在的 `.js` 文件 |
| `configEditor` | 配置编辑准备阶段运行的可选工作副本调整脚本；仅 `data-specialized` 可声明，必须是插件目录内存在的 `.js` 文件 |
| `capabilities` | 可选能力 key 数组。已接入宿主语义的 key：`emulator`（脚本实例可选「安卓模拟器」启动方式）、`self-managed-pc-launch`（PC 客户端启动由脚本自身含启动器完成；脚本弹窗在选择「PC 客户端」时关闭并禁用「启动游戏」开关、禁用启动参数与等待秒数，游戏路径保留填写用于任务失败时强制关闭游戏；持久化启动开关、参数和等待时间保留，仅在运行时生成宿主启动计划约束）、`execution-preview-client` |

`self-managed-pc-launch` 的持久化启动开关、参数和等待时间保持用户设置；能力只在 PC 模式生成运行时宿主启动计划约束，切换到模拟器模式时可恢复原设置。

### 配置校验脚本

`configValidator` 在两个时机执行：用户完成配置编辑并保存（trigger=`config-edit`），以及保存专项脚本实例（新建/编辑）后按绑定用户逐个执行（trigger=`script-save`）。宿主先提交编辑/保存结果，再以用户配置 store 作为主工作根目录运行脚本；取消编辑不会触发执行。脚本错误或超时会记录并通过结果反馈，已提交的配置及脚本此前写入的文件保持不变。校验结论通过 `nexus.notify` / `nexus.toast` 传回前端角落通知，当前版本的校验器只做比较与提醒，不修改配置。

`configEditor` 在配置编辑目标软件启动前运行（trigger=`config-edit-preparation`）。`resolve.json` 可声明 `configEdit.isolateSiblingCandidates`，宿主按当前 profile 的实际候选文件或目录建立 `work/edit-isolation`；`configEdit.freshInput` 可为 fresh 编辑提供稳定的输入名和值。编辑器脚本读取 `nexus.input.mode`、`nexus.input.configInputName`、`nexus.input.configInputValue` 和 `nexus.input.extras`，其中 `@extra<序号>/` 指向附加配置工作副本并允许写入，主配置写入请求会被拒绝。脚本失败、超时或写入失败会阻断目标程序启动并回滚准备现场。

脚本入口可使用稳定输入 DTO `nexus.input`：

```json
{
  "trigger": "config-edit",
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

## resolve.json（推导配置）

```json
{
  "inputs": [
    { "name": "config", "label": "BAAH 配置文件名", "labelKey": "input.config.label", "description": "BAAH_CONFIGS 下的配置文件名（含 .json）", "descriptionKey": "input.config.description",
      "default": "config.json", "required": true, "pattern": "^[A-Za-z0-9_\\-]+\\.json$" }
  ],
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

- **inputs（可选）**：用户输入变量声明，供 paths/require 模板内联引用。`name` 必须是字母开头的字母/数字/下划线且不重复；`label`/`description` 为回退文本，`labelKey`/`descriptionKey` 可引用插件 `i18n/` 资源中的展示文字；`default` 为缺省值；`required` 表示缺失（且无 default 可回退）时推导失败；`pattern` 为可选的整串正则校验。仅声明未被模板引用的输入不参与推导。宿主对所有输入值做基线净化（禁止路径分隔符、冒号、相对路径段、通配符、花括号与控制字符），防止路径拼接越界。`configPath` 模板恰好引用一个输入且输入未提供/指向的目标不存在时，宿主枚举静态目录中匹配「静态前缀 + * + 静态后缀」的**文件与子目录**作为候选（目录候选服务于实例目录型配置），目录内唯一候选时自动绑定并跟随改名，多候选不猜测；`pattern` 同时用于枚举过滤（如实例目录名 `^\d{2}$` 可排除共享数据目录）。宿主 v0.15.3 起按请求语言解析 `label`/`description` 并返回输入 DTO，输入 key 必须存在于所有 locale 资源中。宿主 v0.14.2 起输入值按用户保存在绑定（`configInputs`）上，运行/编辑/校验按用户绑定解析（接管哪个配置文件属于用户选择，多用户可各自接管不同配置）；脚本实例的 `pluginInputs` 仅作为未设置绑定输入时的回退，专项实例编辑弹窗不再渲染输入表单。
- **require**：全部满足才推导成功（替代 DLL 时代的 `File.Exists` 校验）。`file` 相对脚本根目录；`var` 将匹配到的绝对路径绑定为变量；`searchUpward: true` 时根目录找不到则逐级向上搜索（最多 4 层，March7th 管理端/执行端分离场景）。
- **paths**：`mainExe` / `args` / `configPath` / `logPath` 四项，另有可选 `extraConfigPaths` 数组。
  - 占位符 `{var}` = 绑定文件绝对路径；`{rel:var}` = 相对脚本根目录的相对路径（运行时启动目标语义，同目录结果带 `.\` 前缀）。**占位符仅整体替换**：整项命中即替换为该路径，不支持路径文本内嵌入拼接（如 `C:\dir\{var}` 的模板会丢弃前缀只保留 `{var}` 解析值）；需要组合路径时请用无占位符的相对拼接。绑定占位符每项最多 1 个，且不可与 `{input:名称}` 混用。
  - 占位符 `{input:名称}` = 用户输入值**内联替换**，可与相对路径文本自由组合（如 `BAAH_CONFIGS/{input:config}`、`--config {input:config}`）；引用未声明的输入、必填输入缺失且无 default、或值未通过 pattern 校验时整体推导失败。
  - 无占位符：路径字段按相对脚本根目录拼接；`args` 原样返回（参数文本）。`logPath` 允许为空：为空表示专项脚本无专用日志文件，判定日志改由进程标准输出提供。
  - `extraConfigPaths`（可选，宿主 v0.14.1+）：附加配置文件/文件夹路径数组（相对脚本根目录，支持 `{input:名称}`）。附加路径与主配置路径一样按用户快照隔离交换（运行前快照覆盖现场、运行后与编辑提交差异入库），但**判定脚本始终不可见**——`input.files`、`replaceConfigs` 与 config-restore 只作用于主 `configPath`。适用对象是软件级配置（如 BAAH 的 `DATA/CONFIGS/software_config.json`、BetterGI 的 `User/config.json`）。快照缺失宽容：现场也不存在时保持为空，等现场生成后自动采用。
  - `mainExe` 推导后必须存在（require 覆盖或文件真实存在），否则推导失败（前端保存被拒）。

附加配置路径的运行准备与快照同步采用带 manifest 的 stage/backup/commit 事务；准备失败会回滚已处理路径并阻断本次运行，启动恢复和运行收尾会处理未提交现场，无法确认的现场保留并告警。

## 判断脚本

- 契约与通用判断脚本一致：输入 `__NEXUS_INPUT__`（JS）/ 输入 JSON 路径（Python），输出 stdout 尾行 `{"status":"success|partial|failed","reason":"…","notifyText":"…","notifyScreenshotId":"…","replaceConfigs":[…]}`；`partial` 只能由判断脚本主动返回，属于终局结果且不触发重试、不计入每日成功次数；`replaceConfigs` 仅在 `failed` 结果下为下一次重试应用。宿主在当前 profile 解析成功后将 `judgeScript` 作为本次操作的有效判断脚本，用户不可编辑（专项弹窗不渲染自定义完成标志区）。
- 语言按扩展名自动识别：`.js`（内置 Jint 引擎）/ `.py`（系统 python.exe）。

### 判断脚本截图

历史详情中的受保护图片由宿主前端通过带 Bearer 认证的 Blob 请求加载，再以弹窗生命周期管理 Object URL；插件无需获得历史文件鉴权令牌。

一次「脚本实例 × 用户」运行按 Attempt 分别维护内存截图池；每个 Attempt 最多保存 8 张，第 9 张加入时移除该 Attempt 最早的一张。运行收尾时，当前保留截图会写入本轮运行的 history 目录。

- 截图来源为游戏窗口客户区或模拟器画面，保留采集到的原始像素宽高，编码为高质量 JPEG。
- 关键字模式在首次接受成功/失败关键字判定时自动截图；判断脚本模式在首次接受 `status: "success"` / `"partial"` / `"failed"` 时自动截图。关键字模式不能产生 `partial`。
- JavaScript 判断脚本可随时调用 `nexus.captureScreenshot()`，返回截图 ID；Python 判断脚本可使用输入中的 `screenshotApi.endpoint` 和 `screenshotApi.token`，向 endpoint 发送带 `X-Nexus-Screenshot-Token` 请求头的 `POST` 请求来截图。该地址仅绑定本机回环，并随当前判断脚本调用结束失效。
- PC 游戏运行期间由宿主约每秒维护一张最近有效帧（最多保留 2 秒，按 Attempt 隔离）。截图请求先采集当前窗口；如果判断脚本或关键字生效后窗口已经消失，宿主直接回退到有效缓存帧，不增加关闭游戏前的等待阶段。插件无需自行检测窗口、维护截图缓存或实现重试。
- 输入中的 `screenshots` 仅包含 ID、序号、时间、尝试次数、尺寸、来源和触发类型等元数据，不包含图片字节。
- 输出的 `notifyScreenshotId` 指定最终 Attempt 的脚本通知附带截图。留空时选择最终 Attempt 当前仍保留的最新截图；填写已被淘汰、属于其他 Attempt 或不存在的 ID 时不附图，并记录警告。脚本通知发送后截图池释放；队列汇总通知不附图。

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
      "file": "默认配置.json",
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
- boolArray 型：`path` 定位布尔数组（如 BAAH 平行数组 `TASK_ORDER_GROUP.ALL_PIPELINES[0].TASK_ONOFF`），`initial` 为布尔数组，按下标逐位还原；**超出 initial 的尾部元素保持当前值**；目标数组短于 `initial` 时视为应用失败（该文件不入快照）。
- 仅作用于插队文件（`replaceConfigs` 清单内）；还原描述缺失/解析失败/应用失败时，该文件按「无还原描述」处理（不写入快照）。
- 现有专项实现参考：`maaend/data/judge.js`（array 型，`instances[id=...].tasks`）、`bettergi/data/judge.js`（map 型，`TaskEnabledList`）、`baah/data/judge.js`（boolArray 型，平行数组 `TASK_ONOFF`）。

插件机器标识参与脚本实例、配置、密钥、作用域和用户偏好隔离；artifactName 参与源码、安装和发行文件系统路径。发布后应保持两者稳定；变更身份时按新插件重新配置用户绑定和插件设置。

## 构建与部署

- 插件仓库由 GitHub Actions 在只读构建任务中生成 ZIP 和 catalog，合并后由 Bot commit 写入 `packages/<ArtifactName>/`；宿主从 catalog 的官方 raw 地址下载，主程序更新不会覆盖用户插件目录。
- 修改插件文件后重启服务生效；`/api/status` 的 `plugins` 列表可见，新建脚本选择卡片层出现「新建{displayName}专项脚本实例」。

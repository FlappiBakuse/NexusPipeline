# Managed Plugin API

## managed-code C# 插件（Plugin API v1.9）

代码插件必须在独立项目中引用 `src/NexusPipeline.Plugin.Abstractions/`，宿主不会向插件公开 `IServiceProvider`、`AppSettings`、`ScriptInstance` 或 `RunRecord`。插件由 `AssemblyLoadContext` 隔离加载，入口程序集从 manifest 声明，禁用或 API 不兼容时不会加载程序集。

宿主当前 API 版本为 `1.9`：主版本必须相同，插件 minor 版本必须小于或等于宿主 minor 版本，因此 `1.0` 至 `1.9` 插件可加载，`2.0` 插件会被拒绝。既有插件仍按自身声明的 API minor 加载；使用模拟器 provider 的插件至少需要 `1.7`，使用通知收件人覆盖的插件需要 `1.8`，使用独立执行 provider 的插件需要 `1.9`。

`IPluginHostContextV1_9.ExecutionProviders` 的冻结计划、配置门禁、worker 与事件协议见 [执行 provider](execution-provider.md)。新插件须声明相应 API 与最低 Host，旧插件不需要修改 minor。

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
  "apiVersion": "1.8",
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

实现 v1.1 能力的插件应在初始化时检查 `context is IPluginHostContextV1_1`；需要用户列表徽章的 v1.2 插件应检查 `context is IPluginHostContextV1_2`；需要 v1.3 扩展端口的插件应检查 `context is IPluginHostContextV1_3`；需要本地化端口的插件应检查 `context is IPluginHostContextV1_4`；需要 v1.6 资产端口的插件应检查 `context is IPluginHostContextV1_6`；需要 v1.7 模拟器支持端口的插件应检查 `context is IPluginHostContextV1_7`；需要 v1.8 通知收件人覆盖的插件应检查 `context is IPluginHostContextV1_8`，不满足时清晰拒绝初始化。v1.1 附加端口如下：

- `IPluginUserDataStore`：按用户读写 JSON 配置与 DPAPI 密钥。配置路径为 `config/plugins/<机器 ID>/users/<用户 ID>.json`，密钥路径为同目录下的 `<用户 ID>.secrets.json`。删除全局用户时宿主会清理该用户在所有插件中的用户文件；插件禁用或初始化失败不影响清理。物理安装目录使用 artifactName，不参与这些逻辑命名空间。
- `IPluginUserGlobalManagementRegistry`：注册声明式用户全局设置贡献。字段类型仅允许 `text`、`textarea`、`secret`、`switch`、`select`、`multi-select`、`status`；密钥读取只返回 `{configured:true|false}`，保存密钥必须使用 `{action:"keep"}`、`{action:"set",value:"..."}` 或 `{action:"clear"}`。
- `IPluginExecutionEventService`：订阅 `UserRunStarting`。事件只包含用户、脚本实例、队列、运行模式和开始时间等稳定标识；宿主异步调用处理器，处理器异常只记录警告，不能阻塞或改变执行。
- `IPluginHttpClientFactory`：创建遵循宿主代理设置的外网 `HttpClient`，插件无法读取 `AppSettings`。
- `IPluginUserListBadgeRegistry`（v1.2）：注册按用户返回单个聚合徽章的轻量读取处理器。返回 `null` 表示该用户不显示徽章；处理器应只读取本地插件状态，不执行网络请求。

宿主通用设置接口为 `GET /api/plugin-contributions/user-global/{userId}` 与 `PUT /api/plugin-contributions/user-global/{userId}/{pluginName}/{contributionId}`。插件未启用或贡献不存在返回 `404 contribution_not_found`，贡献处理器异常返回 `500 plugin_error`。

GameCheckIn v0.3.1 使用插件自有的独立任务、任务级平台凭据和本机时区计划，不再注册用户全局签到设置或依赖用户运行事件。通知由宿主全局渠道发送，任务可以覆盖 SMTP 收件人，留空时继承宿主全局收件人。v0.3.1 使用全新的任务存储格式，不导入 v0.3.0 创建的任务数据；用户需要在“签到”页面重新创建任务并填写凭据。

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
settings.sections               settings.cards
shell.nav
```

每个贡献包含稳定 `id`、`slot`、`kind`、标题、说明、排序值和可选字段。字段类型包括 `text`、`textarea`、`secret`、`switch`、`select`、`multi-select`、`status`，以及 v1.3 的 `number`、`color`、`range`、`url`。上下文使用 `PluginUiContext(Slot, Mode, PrimaryId, SecondaryId)`；例如脚本编辑器可用 `PrimaryId` 表示脚本实例，用户绑定设置可同时传入用户和脚本 ID。

宿主按公开元素渲染声明式字段，插件无需自带控件：`text`/`url`/`secret`/`status` → `nxp-text-input`，`textarea` → `nxp-text-area`，`number` → `nxp-number-input`，`range` → `nxp-range`，`color` → `nxp-color-picker`，`switch` → `nxp-switch`，`select` 与 `multi-select` → `nxp-select`。字段的 `min`、`max`、`step`、`options`、`placeholder`、`maxLength`、`readOnly` 与 `required` 映射到对应公开属性；保存载荷沿用字段类型语义：`switch` 为布尔、`multi-select` 为字符串数组、`number` 与 `range` 为数值、`secret` 为 `{action:"keep"|"set"|"clear"}`。

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



### v1.7 模拟器支持扩展

`IPluginHostContextV1_7.EmulatorSupport` 允许 managed-code 插件注册模拟器识别 provider。注册只在该插件运行期间有效，插件停止或初始化失败时会撤销；provider 的 `Id` 使用小写 kebab-case，`Priority` 决定稳定探测顺序。

provider 的 `ProbeAsync(adbEndpoint, cancellationToken, timeoutSeconds)` 返回 `NotApplicable`、`Matched(driver)` 或 `Error(message)`。无匹配时宿主继续现有 Generic ADB 路径；任何已加载 provider 明确报告错误、多个 provider 同时匹配或探测超时都会使目标识别失败，不能把已证明的错误降级为 Generic。目标选定后，宿主在本次运行中冻结并复用同一驱动完成 `EnsureReadyAsync`、`StartAppAsync`、`GetForegroundPackageAsync`、`CaptureScreenAsync`、`StopAppAsync` 和 `ShutdownAsync`；每次调用都受宿主超时与取消边界约束。截图驱动返回不超过 16 MiB 的 PNG 字节。

Generic ADB 与 MuMuManager 保留在宿主。雷电、夜神和 BlueStacks 的专属识别、驱动命令与实例关闭由官方可选插件 `EmulatorSupport` 提供；使用这三家模拟器的厂商专属行为前，需要安装并启用该扩展。数据化专项插件的 `emulator` capability 仍只声明脚本实例支持「安卓模拟器」启动方式，不负责注册 provider。



### v1.8 通知收件人覆盖

`IPluginHostContextV1_8` 在 v1.7 基础上标记通知收件人覆盖能力。插件调用 `IPluginNotificationService.SendAsync` 时，可以在 `PluginNotification.SmtpTo` 提供可选 SMTP 收件人；空值或空白值继承宿主全局 SMTP 收件人。SMTP 服务器、发件人、凭据与渠道开关仍由宿主管理，Webhook 继续使用宿主全局配置。



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
      "palette": { "--accent": "#62a0ff" }
    }
  ]
}
```

搬迁载荷中的 `currentId` 仅在旧轮换游标存在时出现，代表旧实现记录的当前壁纸，是否沿用由插件决定。插件应在初始化时消费该作用域记录，导入完成后删除该记录。

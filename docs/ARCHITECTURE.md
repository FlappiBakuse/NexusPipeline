# NexusPipeline 架构说明

本文件是开发者与大模型的导航地图：模块边界、依赖方向、如何定位功能、如何扩展插件。v0.2.0 起生效。

## 总体结构

```
NexusPipeline/
├── src/                C# 后端（.NET 8，WinForms 托盘 + HttpListener）
│   ├── *.cs            核心域（NexusPipeline）
│   ├── Web/            HTTP 层（NexusPipeline.Web）
│   ├── Cli/            命令行层（NexusPipeline.Cli）
│   └── Plugins/        插件契约与内置插件（NexusPipeline.Plugins）
├── wwwroot/            前端（零构建 ES modules，浏览器直接加载）
│   ├── app.js          路由 + 事件委托（唯一入口）
│   ├── core/           平台层（与业务无关的通用能力）
│   ├── views/          业务视图（一域一文件）
│   └── effects/        独立视觉效果
└── uitest/             Playwright 端到端测试（黑盒，292 项断言）
```

## 后端分层（src/）

### 依赖方向（只允许向下依赖）

```
NexusPipeline（核心域：模型/服务/数据）
        ↑           ↑            ↑
NexusPipeline.Web（HTTP 适配层）
NexusPipeline.Cli（命令行适配层）
NexusPipeline.Plugins（插件契约 + 内置插件）
```

- **核心域不得引用 Web/Cli/Plugins**（例外：`RuntimeContext` 组合根持有 `PluginManager` 实例——组合根允许）。
- **Web/Cli 只调用核心域服务，不做业务逻辑**，只做参数解析与响应组装。
- **Plugins 通过契约接口（IPlugin / ISpecializedScriptPlugin / INotifyChannel / PluginContext）与宿主交互**，不得反向引用宿主实现细节。

### 关键类职责

| 类 | 位置 | 职责 |
|---|---|---|
| `Program` | src/Program.cs | CLI 命令分发（service/manage/status/web/run-script/run-queue/cancel/register/unregister） |
| `Bootstrap` | src/Bootstrap.cs | 服务启动/停止编排、Web 端口重试 |
| `RuntimeContext` | src/RuntimeContext.cs | 组合根：持有 Settings/Scripts/Queues/Center/History/Plugins/Scheduler 单例 |
| `DataStore` | src/DataStore.cs | 持久化仓储（scripts/queues JSON 读写） |
| `DispatchCenter` | src/DispatchCenter.cs | 运行编排：脚本/队列执行、取消、通知分发 |
| `RunSession` | src/RunSession.cs | 单次脚本运行会话（重试、日志监控、用户配置交换） |
| `LogPattern` | src/LogPattern.cs | 日志路径格式解析（日期占位符/通配符严格匹配，无格式外猜测） |
| `Scheduler` | src/Scheduler.cs | 定时/启动时触发队列 |
| `HistoryService` | src/HistoryService.cs | 历史记录读写与清理 |
| `WebServer` | src/Web/WebServer.cs | HTTP 骨架：监听、静态文件、路由表（约 150 行） |
| `HttpHelper` | src/Web/HttpHelper.cs | 通用 HTTP 辅助（写 JSON/404/405/解析请求体） |
| `ApiXxxHandler` | src/Web/ | 每资源一个 handler，路由表在此分发 |
| `MainMenu` + 菜单类 | src/Cli/ | 命令行交互（主菜单/脚本/队列/调度/历史/插件/设置/通知渠道） |
| `PluginManager` | src/Plugins/PluginManager.cs | 插件发现/加载/开关/能力查询 |
| `Logger` | src/Logger.cs | 分级日志（DEBUG/INFO/WARN/ERROR/FATAL），阈值过滤，控制台着色 |

### public / internal 约定

- 仅以下为 **public**（对外契约）：`Program`（入口）、`IPlugin`/`ISpecializedScriptPlugin`/`ScriptProfile`/`PluginContext`/`INotifyChannel`（插件契约）、领域模型 `AppSettings`/`ScriptInstance`/`ScriptUser`/`DispatchQueue`/`QueueTask`/`QueueTimeSet`/`RunRecord`/`RunAttempt`（插件接口签名需要）。
- 其余全部 `internal`：新增类型默认 internal，除非它属于契约清单。

### 新增 API 的落点

- HTTP 路由：在 `src/Web/` 新增或扩展 `ApiXxxHandler`，并在 `WebServer.RouteApiAsync` 注册一行。
- 命令行菜单：在 `src/Cli/` 对应菜单类加 case。
- 业务服务：核心域新增服务类，由 `RuntimeContext` 持有。

## 前端分层（wwwroot/）

### 依赖方向

```
app.js → views/* → core/*（api/state/ui/modal/forms/dom/format）
views/* 互不引用（跨域数据只经 core/state.js 缓存共享）
```

### 模块职责

| 模块 | 职责 |
|---|---|
| `app.js` | 路由表 + 各视图 `actions` 注册表合并分发 + 全局 input 委托。**不加业务逻辑** |
| `views/scripts.js` | 脚本实例页（卡片列表 + 新建卡片组 + 通用/专用弹窗，草稿为模块变量） |
| `views/users.js` | 用户管理二级页（`#/scripts/{id}/users`） |
| `views/queues.js` | 调度队列页 + 定时/任务弹窗 |
| `views/dispatch.js` | 调度中心（2 秒轮询，只更新运行面板 DOM） |
| `views/history.js` | 历史列表 + 详情弹窗 |
| `views/plugins.js` | 插件列表 + `#/plugins/{name}` 配置二级页 |
| `views/settings.js` | 系统设置页 |
| `views/dashboard.js` | 仪表盘（3 秒轮询） |
| `core/api.js` | 请求封装（JSON/错误/AbortController 生命周期联动） |
| `core/dom.js` | `$` / `$$` 查询 |
| `core/format.js` | 格式化/转义/徽章模板 |
| `core/forms.js` | 共享表单模板（pageHeader/valueField/selectField） |
| `core/modal.js` | 单模态弹窗（焦点陷阱/Esc/焦点恢复） |
| `core/ui.js` | 页面渲染/导航/Toast/主题/倒计时 |
| `core/state.js` | 路由生命周期（enterPage/isCurrent/schedule/trackController）+ 跨域缓存（scripts/queues/settings） |

### 新增交互的落点

1. 在对应域视图新增导出函数 + 加入该视图的 `actions` 对象（`data-action` 名与处理器映射）。
2. 视图模板使用 `data-action` + 稳定的 `data-testid`（e2e 契约）。
3. 需要路由的新页面：视图导出 `pageXxx(token)`，在 `app.js` 的 `routes` 表注册一行；二级路由在 `route()` 特判分支转发。

## 插件扩展指南

外部插件 = `plugins/*.dll` 中实现契约接口（public、无参构造）的类型，启动时自动加载。

### 插件分类

| 类别 | 接口 | 职责 | 启用语义 |
|---|---|---|---|
| 通用插件 | `IPlugin`（+ 能力接口如 `INotifyChannel`） | 为程序添加能力 | 内置插件白名单 `EnabledPlugins`（默认 notify）；外部插件默认启用 |
| 专用插件 | `ISpecializedScriptPlugin : IPlugin` | 接管专项脚本实例配置：`Resolve(rootPath)` 推导主程序/参数/配置/日志 | 外部插件默认启用，显式禁用记入 `DisabledPlugins`（重启后仍禁用） |

### 编写专用插件（示例：`extensions/BetterGIAdapter/`）

```csharp
public sealed class BetterGenshinImpactAdapter : ISpecializedScriptPlugin
{
    public string Name => "bettergi";            // 脚本实例 PluginType 引用此名
    public string DisplayName => "BetterGI";
    public string Description => "...";
    public string Version => "1.0.0";
    public bool IsBuiltIn => false;
    public void Initialize(PluginContext context) { }
    public void Shutdown() { }

    public ScriptProfile? Resolve(string rootPath)   // 无法推导返回 null（前端保存将被拒）
    {
        string exe = Path.Combine(rootPath, "BetterGI.exe");
        if (!File.Exists(exe)) return null;
        return new ScriptProfile
        {
            MainExe = exe,
            Args = "--startOneDragon",
            ConfigPath = Path.Combine(rootPath, "User", "OneDragon", "默认配置.json"),
            LogPath = Path.Combine(rootPath, "log", "better-genshin-impact{YYYYMMDD}.log"),
        };
    }
}
```

- 专用插件工程通过 `ProjectReference` 引用 `src/NexusPipeline.csproj`（契约类为 public），构建产物 DLL 放入 `release/plugins/`（见 `build.cmd`）。
- 宿主在保存专用脚本实例时调用 `Resolve` 固化快照（POST/PUT 时覆盖 MainExe/Args/ConfigPath/LogPath）；前端简化弹窗通过 `POST /api/scripts/probe` 预校验。
- 元数据 + 生命周期：实现 `IPlugin`；通知能力：实现 `INotifyChannel`（NotifyScriptAsync / NotifyQueueAsync），宿主在运行结束时自动调用。
- 宿主交互：只使用 `PluginContext`（Log / Settings / ReloadSettings），**不要**引用 `RuntimeContext`。
- 内置插件 `NotifyPlugin` 在 `PluginManager.DiscoverBuiltIn` 注册；外部插件与内置插件同契约。

> v0.2.0 起命名空间为 `NexusPipeline.Plugins`，v0.1.x 编译的外部插件需重新编译；v0.3.0 起新增 `ISpecializedScriptPlugin` / `ScriptProfile` 契约。

## 功能定位指南（找代码）

| 想找什么 | 去哪里 |
|---|---|
| 某 API 路由的实现 | `src/Web/ApiXxxHandler.cs`（路由表见 `WebServer.RouteApiAsync`） |
| 命令行某菜单 | `src/Cli/` 对应菜单类 |
| 脚本运行流程/重试/日志监控 | `src/RunSession.cs`、`src/LogPattern.cs`（日志路径格式解析） |
| 队列调度触发 | `src/Scheduler.cs` |
| 通知发送（Webhook/SMTP） | `src/WebhookSender.cs`、`src/SmtpSender.cs`、`src/Plugins/NotifyPlugin.cs` |
| 页面渲染/表单 | `wwwroot/views/` 对应域文件 |
| 前端交互绑定 | 视图 `actions` 对象 → `app.js` 合并分发 |
| 配置读写/加密 | `src/ConfigStore.cs`、`src/SecretStore.cs` |
| 历史记录格式 | `src/HistoryService.cs`、`src/RunRecord.cs` |

## 数据流速览

```
Web 请求 → WebServer → ApiXxxHandler → 核心服务（DispatchCenter/Scheduler/HistoryService/...）→ DataStore/Logger
CLI 菜单 → Cli 菜单类 → 核心服务
运行结束 → DispatchCenter → RuntimeContext.Plugins.NotifyScriptAsync → INotifyChannel 实现 → Webhook/SMTP
```

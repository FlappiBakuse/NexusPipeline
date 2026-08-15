# NexusPipeline 架构说明

本文件是开发者与大模型的导航地图：模块边界、依赖方向、如何定位功能、如何扩展插件。v0.2.0 起生效。
核心设计理念与运行流程见 [DESIGN.md](DESIGN.md)；版本历史见 [CHANGELOG.md](../CHANGELOG.md)。

## 总体结构

```
NexusPipeline/
├── src/                C# 后端（.NET 8，WinForms 托盘 + HttpListener）
│   ├── *.cs            入口与组合根（NexusPipeline）：Program/Bootstrap/RuntimeContext/TrayApp
│   ├── Models/         领域模型（NexusPipeline.Models）
│   ├── Services/       服务层（NexusPipeline.Services）
│   ├── Persistence/    持久化层（NexusPipeline.Persistence）
│   ├── Utilities/      工具层（NexusPipeline.Utilities）
│   ├── Web/            HTTP 层（NexusPipeline.Web）
│   ├── Cli/            命令行层（NexusPipeline.Cli）
│   └── Plugins/        插件契约与内置插件（NexusPipeline.Plugins）
├── wwwroot/            前端（零构建 ES modules，浏览器直接加载）
│   ├── app.js          路由 + 事件委托（唯一入口）
│   ├── core/           平台层（与业务无关的通用能力）
│   ├── views/          业务视图（一域一文件）
│   └── effects/        独立视觉效果
└── uitest/             Playwright 端到端测试（黑盒，@playwright/test 框架；tests/ 按域 7 文件共 64 用例 / NEXUS_CI 核心集 63；单元测试见 src/NexusPipeline.Tests/）
```

## 后端分层（src/）

### 依赖方向（只允许向下依赖）

```
NexusPipeline（根：Program/Bootstrap/RuntimeContext 组合根）
   └── Models（领域模型）← Services（服务）← Persistence（持久化）← Utilities（工具，被一切依赖）
        ↑           ↑            ↑
NexusPipeline.Web（HTTP 适配层）
NexusPipeline.Cli（命令行适配层）
NexusPipeline.Plugins（插件契约 + 内置插件）
```

- **核心域不得引用 Web/Cli**（例外：`RuntimeContext` 组合根持有 `PluginManager` 实例——组合根允许）。
- **Web/Cli 只调用核心域服务，不做业务逻辑**，只做参数解析与响应组装。
- **Plugins 通过宿主内置契约接口（IPlugin / INotifyChannel / PluginContext）交互**；数据化专项插件（`DataSpecializedPlugin`）为纯数据驱动，宿主只读其目录文件。
- **依赖方向顺沿命名空间**：Models 无依赖；Services 依赖 Models/Persistence/Utilities；Persistence 依赖 Utilities。
- **已知偏差（如实记录，见 KNOWN-ISSUES.md KN-49）**：v0.6.3 插件契约内置后，`Services` 与 `Plugins` 存在双向依赖（`UserConfigManager`/`DispatchCenter` 引用 Plugins 调用 `ResolveProfile`/通知分发；`PluginManager`/`NotifyPlugin` 引用 Services 使用 Audit/发送器）；`Utilities/Logger` 读取 `RuntimeContext.Instance.Settings`（Utilities → 根命名空间）。均经 `RuntimeContext` 组合根协调，无跨适配层引用；后续重构目标为收敛为单向依赖。

### 关键类职责

| 类 | 位置 | 职责 |
|---|---|---|
| `Program` | src/Program.cs | CLI 命令分发（service/manage/status/web/run-script/run-queue/cancel/register/unregister） |
| `Bootstrap` | src/Bootstrap.cs | 服务启动/停止编排、Web 端口重试 |
| `RuntimeContext` | src/RuntimeContext.cs | 组合根（壳式 DI，v0.5.0+）：内部 ServiceProvider 注册 Center/History/Plugins/Scheduler，外部访问方式不变；`Resolve<T>()` 服务解析出口 |
| `DataStore` | src/Persistence/DataStore.cs | 持久化仓储（scripts/queues JSON 读写） |
| `DispatchCenter` | src/Services/DispatchCenter.cs | 运行编排：脚本/队列执行、取消、通知分发 |
| `RunSession` | src/Services/RunSession.cs | 单次脚本运行会话（重试、日志监控、用户配置交换）；判断脚本输入按尝试切片（v0.5.2+） |
| `SessionJudge` | src/Services/SessionJudge.cs | 完成判定策略状态机（v0.5.0 拆分）：判断脚本/关键字两模式，判定状态与输入 |
| `JudgeScriptRunner` | src/Services/JudgeScriptRunner.cs | 判断脚本执行器：输入 JSON 生成（脚本字段+用户+config（只读）与 script（可读写）目录全递归文件清单+**本次尝试日志段**（v0.5.2+，超过 4MB 截断尾部并置 logTruncated））、JS 内置 Jint 引擎（注入 `__NEXUS_INPUT__`/`nexus.readFile`（限 config/script 范围 2MB）/`nexus.writeFile`（限 script 目录防逃逸）/`nexus.listFiles()`/`console.log`）、Python 系统解释器进程、30 秒超时、stdout 尾行 JSON 解析（含 `replaceConfigs`） |
| `LogMonitor` | src/Services/LogMonitor.cs | 日志增量读取器：追加/截断（v0.6.9+：部分截断从新尾续读、归零从头读）/替换（FileId 对比 `GetFileInformationByHandle` 卷序列号+文件索引，v0.5.2+ 根治句柄残留）三形态；忽略运行前已有内容（末尾读） |
| `UserConfigManager` | src/Services/UserConfigManager.cs | 配置储存对外门面（v0.5.0 拆分），实现分层见 `ConfigSwapPrimitives`/`ConfigSwapSession`/`ConfigSwapPaths` |
| `ConfigSwapPrimitives` | src/Services/ConfigSwapPrimitives.cs | 配置交换文件原语层：安全移动/原子替换/重试/跨进程互斥/形态判断 |
| `ConfigSwapSession` | src/Services/ConfigSwapSession.cs | 配置交换会话/恢复层：replaceConfigs 替换、.session 标记、自愈 + 启动扫描恢复 + 后台延迟重试 |
| `ConfigSwapPaths` | src/Services/ConfigSwapPaths.cs | 配置数据目录管理：data/{脚本Id}/{用户名} 子目录定位与清理 |
| `LogPattern` | src/Persistence/LogPattern.cs | 日志路径格式解析（日期占位符/通配符严格匹配，无格式外猜测） |
| `Scheduler` | src/Services/Scheduler.cs | 定时/启动时触发队列 |
| `HistoryService` | src/Services/HistoryService.cs | 历史记录读写与清理 |
| `WebServer` | src/Web/WebServer.cs | HTTP 骨架：监听、静态文件（v0.6.9+：nosniff/Referrer-Policy/CSP 安全头）、特性路由表（[ApiRoute] 反射扫描注册，v0.5.0+）；远程令牌校验 v0.6.9+ 改常量时间比较 |
| `HttpHelper` | src/Web/HttpHelper.cs | 通用 HTTP 辅助（写 JSON/404/405/解析请求体）；v0.6.9+ 移除 `ReadLogTail`（`/api/logs` 孤儿 API 删除） |
| `ApiXxxHandler` | src/Web/ | 每资源一个 handler，`[ApiRoute("资源名")]` 标注，路由表自动注册（v0.5.0+） |
| `MainMenu` + 菜单类 | src/Cli/ | 命令行交互（主菜单/脚本/队列/调度/历史/插件/设置/通知渠道） |
| `PluginManager` | src/Plugins/PluginManager.cs | 插件发现/加载/开关/能力查询（内置 NotifyPlugin + 数据化专项插件 DataSpecializedPlugin 扫描注册） |
| `Logger` | src/Utilities/Logger.cs | 分级日志（DEBUG/INFO/WARN/ERROR/FATAL），阈值过滤，控制台着色 |

### public / internal 约定

- 仅以下为 **public**（对外契约）：`Program`（入口）与领域模型 `AppSettings`/`ScriptInstance`/`ScriptUser`/`DispatchQueue`/`QueueTask`/`QueueTimeSet`/`RunRecord`/`RunAttempt`。v0.6.3 起插件契约均为宿主内置（`IPlugin`/`INotifyChannel`/`PluginContext`/`ScriptProfile` 一律 internal），不再对外提供 DLL 插件契约。
- 其余全部 `internal`：新增类型默认 internal，除非它属于契约清单。

### 新增 API 的落点

- HTTP 路由：在 `src/Web/` 新增或扩展 `ApiXxxHandler`，类上标注 `[ApiRoute("资源名")]`（子路由标注在方法上，如 `cancel`）；`WebServer` 启动时反射扫描自动注册，**无需改路由表**（v0.5.0+）。
- 命令行菜单：在 `src/Cli/` 对应菜单类加 case。
- 业务服务：核心域 `Services/` 新增服务类，注册到 `RuntimeContext`（组合根）后经 `Resolve<T>()` 或属性访问。

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
| `views/scripts.js` | 脚本实例页（紧凑列表行 + 新建卡片组 + 通用/专用弹窗，草稿为模块变量） |
| `views/users.js` | 用户管理二级页（`#/scripts/{id}/users`） |
| `views/queues.js` | 调度队列页 + 定时/任务弹窗 |
| `views/dispatch.js` | 调度中心（2 秒轮询，只更新运行面板 DOM） |
| `views/history.js` | 历史列表 + 详情弹窗 |
| `views/plugins.js` | 插件列表 + `#/plugins/{name}` 配置二级页（密钥字段并入「保存设置」提交，仅非空提交） |
| `views/settings.js` | 系统设置页 |
| `views/dashboard.js` | 仪表盘（3 秒轮询） |
| `core/api.js` | 请求封装（JSON/错误/AbortController 生命周期联动） |
| `core/dom.js` | `$` / `$$` 查询 |
| `core/format.js` | 格式化/转义/徽章模板 |
| `core/forms.js` | 共享表单模板（pageHeader/valueField/selectField） |
| `core/modal.js` | 单模态弹窗（焦点陷阱/Esc/焦点恢复） |
| `core/ui.js` | 页面渲染/导航/Toast/主题/倒计时 |
| `core/state.js` | 路由生命周期（enterPage/isCurrent/schedule/trackController）+ 跨域缓存（scripts/queues/settings） |
| `core/dnd.js` | 通用拖拽排序组件（v0.6.8+，无业务依赖）：`initDndList(container, { onDrop(ids) })`——容器内 `[data-dnd-id]` 项 + `.drag-handle` 把手，Pointer Events 统一鼠标/触屏；拖拽结束 DOM 重排后回调视图提交全量顺序；插入位置判定不得跳过带 `.dnd-drop-before` 标记的项（否则落位震荡） |

### 新增交互的落点

1. 在对应域视图新增导出函数 + 加入该视图的 `actions` 对象（`data-action` 名与处理器映射）。
2. 视图模板使用 `data-action` + 稳定的 `data-testid`（e2e 契约）。
3. 需要路由的新页面：视图导出 `pageXxx(token)`，在 `app.js` 的 `routes` 表注册一行；二级路由在 `route()` 特判分支转发。
4. 列表拖拽排序（v0.6.8+，弹窗内 v0.6.10+）：渲染容器 + `[data-dnd-id]` 项 + `.drag-handle` 把手 → `initDndList(container, { onDrop })` → 视图把可见项重排进全量列表后提交 `PUT /api/{scripts|queues}/order`（body `{ ids: [...] }`，全量名单一致校验）或用户沿用 `PUT /api/scripts/{id}/users/order`（`{ names }`）；**弹窗内（队列编辑弹窗的定时列表/任务列表）**：onDrop 按 `data-dnd-id`（渲染下标）重排 `queueDraft` 数组即可（时间卡无 Index 字段、任务卡重排时同步重设 `index`），sync 按元素携带下标（`data-ts-idx`/`data-task-idx`）写回原数组项，DOM 顺序与数组顺序脱钩后仍正确。

## 插件扩展指南

v0.6.3 起专项插件为**数据化目录形态**（`plugins/<名称>/plugin.json + data/`），无需编译；内置 C# 插件仅剩 NotifyPlugin（通知推送）。

### 插件分类

| 类别 | 形态 | 职责 | 启用语义 |
|---|---|---|---|
| 通用插件 | 内置 C#（`IPlugin`/`INotifyChannel`/`PluginContext`） | 为程序添加能力（内置「通知推送」，`PluginManager.DiscoverBuiltIn` 注册） | 内置白名单 `EnabledPlugins`（默认 notify），只可禁用不可删除 |
| 数据化专项插件 | `plugins/<名称>/plugin.json + data/`（`DataSpecializedPlugin` 扫描注册） | 接管专项脚本实例配置：`Resolve(rootPath)` 按 `data/resolve.json` 推导主程序/参数/配置/日志/判断脚本 | 外部默认启用，显式禁用记入 `DisabledPlugins`（重启后仍禁用） |

> **通知通道（v0.4.4+）**：`INotifyChannel` 为**多通道并存**语义——`PluginManager.NotifyScriptAsync/NotifyQueueAsync` 分发至全部已启用通道（NotifyPlugin 内部按 Webhook/SMTP 独立开关并行双发）。单个通道异常仅记警告，不影响其余通道。

### 编写数据化专项插件（示例：`plugins/bettergi/`）

```
plugins/bettergi/
├── plugin.json               # 根文件：元数据 + 引用 data 文件（初始化专项插件）
└── data/
    ├── resolve.json          # 推导配置：require 校验 + paths 模板
    ├── judge.js              # 判断脚本（.js = Jint / .py = 系统 python.exe）
    └── config-template/      # 可选：默认配置模板目录（编辑会话生成用）
        └── NexusPipeline.json
```

```json
// plugin.json
{
  "name": "bettergi",
  "displayName": "BetterGI",
  "gameName": "原神",
  "description": "BetterGenshinImpact 专项脚本实例配置接管",
  "version": "0.1.0",
  "resolve": "data/resolve.json",
  "judgeScript": "data/judge.js",
  "configTemplate": "data/config-template"
}
```

```json
// data/resolve.json（March7th 示例：管理端 Launcher + 执行端 Assistant 上级目录搜索）
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

- `require` 全部满足才推导成功；`searchUpward: true` 时逐级向上搜索（最多 4 层）；`{var}` = 绑定文件绝对路径、`{rel:var}` = 相对脚本根目录的相对路径；无占位符的路径字段按相对脚本根目录拼接、`args` 原样返回。
- 宿主在保存专用脚本实例时调用 `Resolve` 固化快照（POST/PUT 时覆盖 MainExe/Args/ConfigPath/LogPath/JudgeScript 与语言，`ConfigTemplateDir` 仅编辑会话现取不落盘）；前端简化弹窗通过 `POST /api/scripts/probe` 预校验。
- `config-template/` 目录在编辑用户配置会话中 ConfigPath 不存在时整体复制到配置位置，复制清单随 `.session` 标记持久化（cancel/重启恢复按清单清理）。
- 完整 schema 见 `plugins/README.md`。

> **MaaEnd 专项要点（v0.6.1，`plugins/maaend/`）**：主程序 `MaaEnd.exe`（MXU 客户端改名）以 `--autostart --quit-after-run` 启动（任务运行完成时进程自动退出）；配置目录 `config/`（`mxu-MaaEnd.json` 为实例/任务核心配置），v0.6.4 起**提供默认配置模板**（`data/config-template/` 含 `mxu-MaaEnd.json` 与 `maa_option.json`，编辑用户配置会话时 config 目录不存在则整体复制生成——目录型 ConfigPath 模板复制到 ConfigPath 本身，恢复按相对父目录清单精确清理）；日志 `debug/{YYYY-MM-DD}-*.log`（前端写入，文件名带 `-n` 自增序号、启动时自动清理旧文件，通配取最新修改 = 当前会话）。判断脚本按「最后一个启用任务的任务完成/任务失败判定行」收尾（MXU 无运行记录机制、无天然选择性补做），失败任务改写 `mxu-MaaEnd.json`（全部 `enabled=false`、失败任务 `enabled=true`）经 `replaceConfigs` 触发选择性重试；启用任务判定**只按 `enabled===true`**（与 MXU 运行分发一致，`enabledByController` 仅 UI 缓存不参与分发）。

> v0.6.3 起插件契约为宿主内置（`IPlugin`/`INotifyChannel`/`PluginContext`/`ScriptProfile` 均 internal），不再对外提供 DLL 插件契约与 `ISpecializedScriptPlugin`。

## 功能定位指南（找代码）

| 想找什么 | 去哪里 |
|---|---|
| 某 API 路由的实现 | `src/Web/ApiXxxHandler.cs`（`[ApiRoute]` 特性注册，见 `WebServer.Routes`） |
| 命令行某菜单 | `src/Cli/` 对应菜单类 |
| 脚本运行流程/重试/日志监控 | `src/Services/RunSession.cs`、`src/Services/LogMonitor.cs`（日志增量读取/替换检测）、`src/Persistence/LogPattern.cs`（日志路径格式解析） |
| 自定义完成标志（关键字/判断脚本） | `src/Services/SessionJudge.cs`（判定状态机）、`src/Services/RunSession.cs`（监控循环/触发时机）、`src/Services/JudgeScriptRunner.cs`（脚本执行器）、`src/Utilities/TextRules.cs`（`KeywordRule`） |
| 判断脚本边界与配置替换 | `src/Services/UserConfigManager.cs`（门面）、`src/Services/ConfigSwapSession.cs`（替换/恢复）、`src/Services/JudgeScriptRunner.cs`（`ResolveWithin` 防逃逸） |
| 队列调度触发 | `src/Services/Scheduler.cs` |
| 通知发送（Webhook/SMTP） | `src/Services/WebhookSender.cs`、`src/Services/SmtpSender.cs`、`src/Plugins/NotifyPlugin.cs` |
| 页面渲染/表单 | `wwwroot/views/` 对应域文件 |
| 前端交互绑定 | 视图 `actions` 对象 → `app.js` 合并分发 |
| 配置读写/加密 | `src/Persistence/ConfigStore.cs`、`src/Persistence/SecretStore.cs` |
| 历史记录格式 | `src/Services/HistoryService.cs`、`src/Models/RunRecord.cs` |

> **v0.5.0 分层变更**：核心域按子域重组（`Models/`、`Services/`、`Persistence/`、`Utilities/` 对应命名空间）；Web 路由改特性路由；`RuntimeContext` 引入壳式 DI（`ServiceProvider` + `Resolve<T>()`，外部访问方式不变）；`RunSession` 判定策略拆出 `SessionJudge`；`UserConfigManager` 拆为门面 + 原语/会话恢复/数据目录三层。public 契约清单不变，extensions 三插件工程对齐后仍可编译。v0.6.3 起专项插件数据化（extensions/ 工程移除，见「插件扩展指南」）。
>
> **v0.5.1 变更**：插件级配置（`PluginContext.GetConfig/SetConfig/GetSecret/SetSecret`，落盘 `config/plugins/<插件名>.json`，DPAPI `enc:` 前缀）；e2e 迁移 @playwright/test（tests/ 按域 7 文件 46 用例，旧 test.mjs 移除）；`core/limits.js` 归位 `views/limits.js`。
>
> **v0.5.2 变更**：日志监控文件替换检测改 FileId（`LogMonitor.FileReplaced`，根治 move+重建场景句柄残留）；初始监控严格 fresh（`LastWriteTime ≥ attemptStart`）；判断脚本输入按尝试切片（本次尝试日志段，跨尝试不污染判定）；RunAsync finally 还原顺序调整（先配置替换还原、后配置交换还原）。

## 数据流速览

```
Web 请求 → WebServer → ApiXxxHandler → 核心服务（DispatchCenter/Scheduler/HistoryService/...）→ DataStore/Logger
CLI 菜单 → Cli 菜单类 → 核心服务
运行结束 → DispatchCenter → RuntimeContext.Plugins.NotifyScriptAsync → INotifyChannel 实现 → Webhook/SMTP
```

# AGENTS.md — NexusPipeline

继承 Workspace/AGENTS.md；冲突时 Workspace 约束优先。NexusPipeline 是 .NET 8 Windows WinForms 托盘程序、HttpListener 服务与 Vue/TypeScript 静态前端。相邻 NexusPipeline-Plugins 维护官方插件。两个仓库独立版本、协同契约。

## 定位与所有权

先检查 Git/用户改动，读取 docs/architecture/README.md、docs/TESTING.md 及所属专题。存在 docs/backend-map.json 时先定位 owner；迁移期间用本次教程逐文件卡对照旧路径与新路径。完整测试命令的长期权威入口为 docs/testing/commands.md，经 docs/TESTING.md 导航。

目标顶层为 Host、ControlPlane、Modules、Platform、Shared；独立 NexusPipeline.Plugin.Abstractions 保留。Modules 依次包含 Settings、Plugins、Scripts、Users、Queues、Configuration、History、Notifications、Execution、Scheduling、Updates、Diagnostics。Settings 表示宿主设置；Configuration 表示被自动化目标的配置。

Host/Composition 创建和释放唯一容器。长期实例不捕获 IServiceProvider。AutomationDefinitionState 在 Host/State 保留 scripts/queues/users 的唯一同步边界；模块经明确 typed port 操作，禁止复制第二套实体集合或将三类状态改成互不协调的锁。Settings 保持 clone→验证→落盘成功→发布新引用。

ControlPlane 负责路由、参数、授权、调用用例和响应；图标读取、avatar、文件浏览、历史文件读取经明确服务调用。反射路由可保留，绑定实例由组合根提供；不因重构切换 ASP.NET Core。CLI 继续走常驻服务 Control API。MCP 注入具体用例，不持有整个 RuntimeContext。

RunRecord 与完成态 RunScreenshot 归 History；运行中的 Execution 状态归 Execution。NotificationDispatcher 的队列通知允许依赖 Queues。Plugins 获取 Host 版本使用注入的版本值；禁止为读版本反向依赖 Updates。Platform 网络服务只接受平台代理选项，不读取 AppSettings。

## Windows 与验证

生产程序与正式运行资源验证使用 Administrator / High Integrity 或 System Integrity；权限检查不足 exit 2，禁止自动 UAC、降级或 skip。受限反馈保留 codex 模式与 NexusTestHost=true/asInvoker，结果不替代 production admin 资格。

新 release 入口完成前使用现存 node tests/run.mjs unit、web、contract、docs、tooling、syntax、build，以及显式 codex/admin system。新入口完成后 Host 最终为：

```text
node tests/run.mjs release core
node tests/run.mjs release frontend-contract
node tests/run.mjs release ui-runtime
node tests/run.mjs release execution-emulator
node tests/run.mjs release update-acceptance
```

H1–H5 不共享测试通过缓存。H3/H4 为 production admin、timeScale=10；H5 的 Test Host update 与 production admin execution 为 timeScale=1，分别记录。所有 System 组必须覆盖 runtime/control/config/execution/judge/emulator/plugins/update。组选择不得导致实际 suite 为空仍报通过。

production 和 Test Host 构建必须使用隔离输出/中间目录，测试进程仅绑定隔离数据与端口。重构建前核对本次进程是否锁定 release/nexus-pipeline.exe；不得结束用户现有实例。命令保持非交互、可见日志和退出码，不添加 pause。

src/Shared/Localization/Resources 移动时显式保持嵌入资源 LogicalName；.csproj 的程序集名、版本、入口 manifest/icon 及 InternalsVisibleTo 不随 namespace 改变。用户文件路径和 JSON 字段名不按目录结构替换。

## 插件与全局设置

专项只声明 Host 能力，前端行为由宿主实现。managed Frontend API 保持1.5；Plugin API 及 Abstractions 项目已有版本元数据不在本轮自行递增或互相强制等同。Config Editor/Validator 的后端 Jint 脚本继续保留。

pluginRepository.channel 文件配置属于 Settings；消费与更新规则属于 Plugins。通过重启应用配置；普通设置 API 显式拒绝修改该字段。安装来源、pending、缓存、同版本更新和通道切换均按 Workspace 规则。新增持久字段同步严格白名单、clone、save/load、ApplyOne、ownership 构造和恢复测试。

## 前端约束

frontend/ 为唯一宿主前端源码；Vite dist 由 build.cmd 同步到 wwwroot，最终用户无需 Node/npm。保持 platform 服务、app/bootstrap.ts 和 features/<domain> 的职责；业务请求不放入无生命周期的全局单例。

可复用控件使用 frontend/src/ui/primitives；插件稳定控件使用 nxp-* Native Custom Elements。插件桥接仅通过 plugin-bridge/host-adapter.ts 使用宿主平台；app/features/ui 通过 @bridge/index facade 使用桥接，不直接引用内部模块。插件不得依赖 Vue 内部组件或私有 class。

专项 capability→Host 组件的选择由已验证能力声明驱动，禁止按插件名/游戏名硬编码分支，禁止运行专项包浏览器代码。既有 resolve.inputs 保持宿主声明式表单。

页面保留360/768/1280视口适配、至少40px触控目标、主题/焦点/ARIA与紧凑列表规范。轮询经 platform/page-state 管理并在离开时释放。验证产品行为，遵守 Workspace UI Smoke 数量与视觉测试约束。

## 长期文档路由

用户功能看 README；结构看 docs/architecture/README.md；构建发布看 docs/DEVELOPMENT.md；验证看 docs/TESTING.md；Web/CLI/MCP 看 docs/CONTROL_PLANE.md；插件契约看 docs/reference/plugin-api/README.md；未完成看 docs/STATUS.md；历史看 CHANGELOG.md。

任何公开能力变更同步控制面能力表和两个仓库插件文档。实际 commit/push/PR/Release 与规则启用始终受 Workspace 授权要求约束。

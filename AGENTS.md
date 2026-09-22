# AGENTS.md — NexusPipeline

本文件是本仓库的项目级工程约束。所有路径相对本仓库根目录，另有标注除外。维护者及自动化代理从本文件开始；实施计划、Issue、任务卡可以细化工作，不能自行扩大操作授权、降低测试要求或放宽用户数据保护。

## 1. 项目与开工入口

NexusPipeline（枢链）是 Windows 本地自动化脚本管家：.NET 8/C# WinForms 托盘、HttpListener 服务、CLI/MCP 控制面，以及构建为静态文件的 Vue 3/TypeScript 前端。最终用户不需要 Node/npm。官方插件仓库为 `FlappiBakuse/NexusPipeline-Plugins`；两个仓库版本独立。单仓库开发不要求特定父目录名称。

开始修改前，在本仓库运行 `git status --short --branch`、`git rev-parse HEAD`，确认用户未提交内容。读取下面与任务有关的最小文档集合；根据 `docs/architecture/README.md` 与 `docs/map.json` 定位专题和责任，再读对应代码和测试。需要逐文件 owner、声明和依赖图时，按架构索引中的正式命令生成 `.generated/architecture/backend-map.json`；该生成物不入仓，也不能回退为依赖外部迁移清单。

| 事项 | 仓库内权威入口 |
|---|---|
| 用户行为、安装、管理员运行原因 | `README.md` |
| 模块、约束与代码定位 | `docs/architecture/README.md`、`docs/map.json`；详细生成地图见 `.generated/architecture/backend-map.json` |
| 环境、构建、发布 | `docs/DEVELOPMENT.md` |
| 测试政策与完整命令 | `docs/TESTING.md` → `docs/testing/commands.md` |
| Web/CLI/MCP 的能力与状态 | `docs/CONTROL_PLANE.md` |
| Plugin SDK、manifest、Frontend API | `docs/reference/plugin-api/README.md` |
| 当前未完成问题 | `docs/STATUS.md` |
| 已发布历史 | `CHANGELOG.md` |

涉及 Plugin API、manifest、专项能力、前端桥接或官方插件装配时，同时检查官方插件仓库相应文件。联调检出路径通过测试入口参数/环境变量显式提供；缺少联调依赖时记录具体缺失，继续不依赖它的工作，不伪造契约通过。

## 2. 操作授权、版本和数据

- 代码实施授权与 commit、push、tag、PR、合并、规则修改、实际发布授权分别判断。用户未授权的远端或版本操作不得执行。可以完成不依赖远端写入的实现和本地验证。
- 日常开发目标为 `develop`；`main` 源码必须经 PR、当前候选的完整 `Release Qualification` 和 squash 合并。Host 没有生成物直推 `main` 例外。禁止普通直推/force push `main`。
- 首次安装仓库资格控制面时，单独的控制面初始化 PR 按当时实际生效的门禁与维护者明确审核合入；不携带产品功能或生成物、不伪造新资格、不自动发布。此初始化操作需要独立授权，不能成为已启用门禁后的绕过入口。
- 正式版本号仅按用户指示修改；未指定版本不阻止普通修复、架构或工具工作。指定版本后同步相关元数据。`major=0` 或含 `-beta.N`/`-rc.N` 的发行标记为 Pre-release。预览插件通道与 Host 产品 Pre-release 是不同概念。
- 修改前保存当前 HEAD、diff 和将修改文件的外部备份。备份必须包含未提交字节；只记录 tag 不足以保全工作树。未经针对性授权不使用 `reset --hard`、`clean -fd`、自动 stash 或覆盖恢复。已授权的版本化本地备份 tag 不推送远端。
- `config/`、`data/`、`history/`、`logs/`、`.nxp/`、插件用户数据、更新/配置恢复现场属于用户或运行态。测试使用新建隔离目录；不得对用户现有实例、进程、端口或文件做“测试清理”。读日志先脱敏。
- 不提交凭据、Cookie、Token、密钥、账号、个人日志、运行目录、依赖缓存或本地验证包。只清理本次创建且身份已核验的明确路径；失败证据保留到诊断结束。不得仅凭 PID 文件杀进程，须核验本次运行身份。
- 获授权提交后按可独立审查/验证/恢复的功能边界提交，Conventional Commits 类型英文、说明中文。不要混入无关修改；不得以固定文件数预算替代合理变更边界。

## 3. 后端结构与依赖

顶层为 `src/Host`、`src/ControlPlane`、`src/Modules`、`src/Platform`、`src/Shared`；独立 `src/NexusPipeline.Plugin.Abstractions` 保持 SDK 边界。模块包含 Settings、Plugins、Scripts、Users、Queues、Configuration、History、Notifications、Execution、Scheduling、Updates、Diagnostics。

`Settings` 管宿主自身设置；`Configuration` 管被自动化目标的配置、快照、编辑会话、交换与恢复。一个业务概念一个 owner。目录和 namespace 对齐，测试按模块镜像组织。新增模块/入口同步 backend map 和架构门禁。

只有 `Host/Composition` 创建/解析/释放 DI 容器；创建完毕向生命周期和控制面传递具体依赖。业务代码禁止 `IServiceProvider`、全局组合根、泛型服务定位器和捕获容器的延迟 delegate。不要通过把 RuntimeContext 改名成其他 Singleton 绕过边界。

Modules 不依赖 Host/ControlPlane；ControlPlane 不直接读写具体业务存储；Platform 不依赖业务模块或硬编码产品仓库/发行策略；Shared 不承载 feature 规则。跨模块依赖必须显式、无环；Contracts 目录不免除循环检查。Host 组合适配器负责需要跨 owner 协调的事务接线。

`AutomationDefinitionState` 保持 scripts/queues/users 的唯一内存与同步边界；typed ports 暴露所需操作，禁止创建第二份实体集合或互不协调的三把锁。Settings 保持 clone → 校验 → 原子保存成功 → 发布新引用。保存失败不得先改变内存或副作用。

HTTP 只处理路由、认证、参数、用例调用和响应映射；图标、avatar、文件浏览和历史读取进入对应服务。MCP 注入明确用例；CLI 经常驻服务 Control API，不另写一套数据修改路径。反射路由可保留，但绑定必须可验证、重复路由报错。

保留配置交换、journal、崩溃恢复、取消、超时、租约与清理的可靠性语义。机械搬迁与行为修改分开验证。内部类型改名不得改变 JSON 字段、用户磁盘路径、资源名、程序集名和公开协议。Plugin API/Frontend API 版本与程序集包版本分别解释，不自动相互改成一样。

## 4. 测试与 Windows 运行

正式发行程序保持 `app.manifest` 的 `requireAdministrator`；自动化功能测试统一使用 `NexusTestHost=true`、`asInvoker` 的隔离测试构建。测试继承启动终端的权限：普通终端直接运行，GitHub 托管 Windows runner 使用其默认管理员环境。测试入口不要求提权，不触发 UAC，不降权，不按权限跳过用例；实际权限写入日志。

主要入口如下，完整参数和工具链版本以 `docs/testing/commands.md` 为准：

```text
node tests/run.mjs dev
node tests/run.mjs dev ui
node tests/run.mjs dev system --group plugins
node tests/run.mjs release core
node tests/run.mjs release frontend-contract
node tests/run.mjs release ui-runtime
node tests/run.mjs release execution-emulator
node tests/run.mjs release update-acceptance
node tests/run.mjs release all
```

H1–H5 的测试内容在本地和 CI 一致。H3/H4 使用 scale10；H5 包含 update scale10、update scale1 故障注入及 execution scale1。未知组、空选择、零用例、意外 skip、缺报告、子进程失败和超时不得报告通过。环境限制与断言失败分开记录；未运行就是 NOT_RUN。

生产/Test Host 构建的输出和中间目录分离；两者共享同一业务源实现，禁止将测试 EXE 发布给用户。测试使用独立端口和受控进程/模拟器 fixture，操作系统授权边界通过平台适配器契约验证，不冒称普通权限已执行了系统级操作。

先搜索现役测试，在最低有效层增加覆盖。普通业务测试验证 API、结果、状态、文件效果和生命周期，不读取源码函数体匹配实现。UI Smoke 只保留必须跨浏览器证明的核心流程，总量不超过 12；不新增持久视觉截图/像素/布局基线，不断言私有 class、DOM 层级或装饰文案。临时人工浏览器验证材料放系统临时目录并按本次所有权清理。

测试失败保留原始报告并修根因。禁止自动重试掩盖不稳定、跳过失败、catch 后成功或降低通过条件。命令非交互、UTF-8、实时显示阶段/用例/退出码，不加无条件 pause。Python 用于适合的文件/数据与辅助脚本；既有 dotnet/node/npm/.cmd 入口按当前文档执行。

## 5. 插件与前端

宿主默认 stable 商店；`pluginRepository.channel` 只接受 stable/develop，通过配置文件及重启生效，普通设置 API 不开放此字段。develop 指向官方 `plugins-develop` Release 资产。通道缓存、ETag、pending、安装归属和冻结包身份一致；禁止跨通道缓存回退、候选按名称重新取包或无可信归属接管目录。

Preview 同版本 hash 变化可更新；hash 相同不因 sourceCommit 变化重装；可信 preview 可被同版本 stable 替换；高版本安装不自动降级。稳定包版本对应字节不可变。新增 journal 字段同步白名单、克隆、读写、恢复和 ownership 提交，保持当前格式数据安全。

专项插件只声明宿主支持的能力，前端代码写在 Host；专项不包含 frontend 对象、frontend-module、web/frontend 浏览器代码或 .NET 程序集。后端 judge/configEditor/configValidator 脚本保持有效。managed 插件可通过 Frontend API 1.5、公开 slot/route 和 `nxp-*` Native Custom Elements 扩展。未声明能力不得静默当作支持。

`frontend/` 是唯一宿主前端源码；Vite 输出同步到发行 `wwwroot/`。`frontend/src/platform` 管平台服务，`app/bootstrap.ts` 管启动，`features/<domain>` 管业务。桥接仅通过 `plugin-bridge/host-adapter.ts` 使用宿主平台；app/features/ui 经 `@bridge/index` facade，不依赖 bridge 私有实现。插件不引用 Vue 私有组件/class。

复用 `ui/primitives` 与既有 CSS 变量、紧凑列表。页面适配 360/768/1280 视口，触控目标至少 40px；保持主题、焦点、ARIA 与可访问性。轮询/订阅经生命周期管理，离开页面释放。运行目标 Args 的路径与参数使用现役解析协议，不私自把命令行引号规则搬进持久化字段。

## 6. 交付与长期维护

README 描述当前产品，架构文档描述现役结构，TESTING 描述实际入口和测试政策，DEVELOPMENT 描述构建发布，CONTROL_PLANE 列公开能力，STATUS 只保留未完成问题，CHANGELOG 记录已发布历史。完成的迁移教程、版本专项清单、外部会话依赖、旧路径和零调用兼容层不进入长期维护入口。

架构 `check` 与生成地图的 schema、owner、路径和确定性校验必须进入默认门禁；报告输出不能代替失败退出。地图只写入本地 `.generated/architecture/backend-map.json` 或当前 CI run 的 artifact，不放入产品 release。保留低层工具和测试，不长期保留迁移债务豁免。修改公开能力同步控制面表、相应测试与官方插件作者文档。

交付时逐项列明改动、已运行命令/退出码、未运行范围及原因；本地测试通过不等于远端发布已启用，上传候选包不等于发布成功。正式完成前，在无父目录文档、无实施资料包的新 checkout 中验证导航、构建、测试和文档。

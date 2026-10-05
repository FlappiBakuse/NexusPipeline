# AGENTS.md — NexusPipeline

本文件是本仓库的项目级工程约束。所有路径相对本仓库根目录，另有标注除外。维护者及自动化代理从本文件开始；实施计划、Issue、任务卡可以细化工作，不能自行扩大操作授权、降低测试要求或放宽用户数据保护。

## 1. 项目与开工入口

NexusPipeline（枢链）是 Windows 本地自动化脚本管家：.NET 10/C# WinForms 托盘、HttpListener 服务、CLI/MCP 控制面，以及构建为静态文件的 Vue 3/TypeScript 前端。最终用户不需要 Node/npm。官方插件仓库为 `FlappiBakuse/NexusPipeline-Plugins`；两个仓库版本独立。单仓库开发不要求特定父目录名称。

开始修改前，在本仓库运行 `git status --short --branch`、`git rev-parse HEAD`，确认用户未提交内容。读取下面与任务有关的最小文档集合；根据 `docs/architecture/README.md` 与 `docs/map.json` 定位专题和责任，再读对应代码和测试。

| 事项 | 仓库内权威入口 |
|---|---|
| 用户行为、安装、管理员运行原因 | `README.md` |
| 模块、约束与代码定位 | `docs/architecture/README.md`、`docs/map.json` |
| 环境、构建、发布 | `docs/development/README.md` |
| 测试政策与完整命令 | `docs/testing/README.md` → `docs/testing/commands.md` |
| Web/CLI/MCP 的能力与状态 | `docs/CONTROL_PLANE.md` |
| Plugin SDK、manifest、Frontend API | `docs/reference/plugin-api/README.md` |
| 当前未完成问题 | `docs/STATUS.md` |
| 已发布历史 | `CHANGELOG.md` |

涉及 Plugin API、manifest、专项能力、前端桥接或官方插件装配时，同时检查官方插件仓库相应文件。联调检出路径通过测试入口参数/环境变量显式提供；缺少联调依赖时记录具体缺失，继续不依赖它的工作，不伪造契约通过。

## 2. 操作授权、版本和数据

- 代码实施授权与 commit、push、tag、PR、合并、规则修改、实际发布授权分别判断。用户未授权的远端或版本操作不得执行。可以完成不依赖远端写入的实现和本地验证。
- 日常开发目标为 `develop`；`main` 源码必须经 PR、`Host / 必需汇总` 与 `Host / 完整预算` 成功和 squash 合并。Host 没有生成物直推 `main` 例外。禁止普通直推/force push `main`。
- 正式版本号仅按用户指示修改；未指定版本不阻止普通修复、架构或工具工作。指定版本后同步相关元数据。`major=0` 或含 `-beta.N`/`-rc.N` 的发行标记为 Pre-release。预览插件通道与 Host 产品 Pre-release 是不同概念。
- 修改前保存当前 HEAD、diff 和将修改文件的外部备份。备份必须包含未提交字节；只记录 tag 不足以保全工作树。未经针对性授权不使用 `reset --hard`、`clean -fd`、自动 stash 或覆盖恢复。已授权的版本化本地备份 tag 不推送远端。
- `config/`、`data/`、`history/`、`logs/`、`.nxp/`、插件用户数据、更新/配置恢复现场属于用户或运行态。测试使用新建隔离目录；不得对用户现有实例、进程、端口或文件做“测试清理”。读日志先脱敏。
- 不提交凭据、Cookie、Token、密钥、账号、个人日志、运行目录、依赖缓存或本地验证包。只清理本次创建且身份已核验的明确路径；失败证据保留到诊断结束。不得仅凭 PID 文件杀进程，须核验本次运行身份。
- 获授权提交后按可独立审查/验证/恢复的功能边界提交，Conventional Commits 类型英文、说明中文。不要混入无关修改；不得以固定文件数预算替代合理变更边界。

## 3. 后端结构与依赖

顶层为 `src/Host`、`src/ControlPlane`、`src/Modules`、`src/Platform`、`src/Shared`；独立 `src/NexusPipeline.Plugin.Abstractions` 保持 SDK 边界。模块包含 Settings、Plugins、Scripts、Users、Queues、Configuration、History、Notifications、Execution、Scheduling、Updates、Diagnostics。

`Settings` 管宿主自身设置；`Configuration` 管被自动化目标的配置、快照、编辑会话、交换与恢复。一个业务概念一个 owner。目录和 namespace 对齐，测试按模块镜像组织。新增模块/入口同步 `docs/map.json` 与 `docs/architecture/README.md`。

只有 `Host/Composition` 创建/解析/释放 DI 容器；创建完毕向生命周期和控制面传递具体依赖。业务代码禁止 `IServiceProvider`、全局组合根、泛型服务定位器和捕获容器的延迟 delegate。不要通过把 RuntimeContext 改名成其他 Singleton 绕过边界。

Modules 不依赖 Host/ControlPlane；ControlPlane 不直接读写具体业务存储；Platform 不依赖业务模块或硬编码产品仓库/发行策略；Shared 不承载 feature 规则。跨模块依赖必须显式、无环；Contracts 目录不免除循环检查。Host 组合适配器负责需要跨 owner 协调的事务接线。

`AutomationDefinitionState` 保持 scripts/queues/users 的唯一内存与同步边界；typed ports 暴露所需操作，禁止创建第二份实体集合或互不协调的三把锁。Settings 保持 clone → 校验 → 原子保存成功 → 发布新引用。保存失败不得先改变内存或副作用。

HTTP 只处理路由、认证、参数、用例调用和响应映射；图标、avatar、文件浏览和历史读取进入对应服务。MCP 注入明确用例；CLI 经常驻服务 Control API，不另写一套数据修改路径。反射路由可保留，但绑定必须可验证、重复路由报错。

保留配置交换、journal、崩溃恢复、取消、超时、租约与清理的可靠性语义。机械搬迁与行为修改分开验证。内部类型改名不得改变 JSON 字段、用户磁盘路径、资源名、程序集名和公开协议。Plugin API/Frontend API 版本与程序集包版本分别解释，不自动相互改成一样。

## 4. 测试与 Windows 运行

正式发行程序保持 `app.manifest` 的 `requireAdministrator`；自动化功能测试统一使用 `NexusTestHost=true`、`asInvoker` 的隔离测试构建。测试继承启动终端的权限：普通终端直接运行，GitHub 托管 Windows runner 使用其默认管理员环境。测试入口不要求提权，不触发 UAC，不降权，不按权限跳过用例；实际权限写入日志。

核心验证入口为 `node tests/run.mjs ci --group backend`、`ci --group frontend`；`smoke` 并行运行这两组，共享从命令开始的 180 秒预算。测试只在原字节隔离副本中构建，输出及依赖缓存使用外部测试目录。`tests/policy.json` 固定选择与预期用例；原生 TRX／Vitest JSON、场景和用例集合、计数、源码指纹、预算和清理都必须匹配，零用例、意外 skip、缺报告、取消和超时不得成功。普通测试不自动运行全仓语法扫描、文档链接或全部工具自测；这些按改动显式运行。

`daily` 在两个独立 Host 槽位中运行执行、配置恢复、控制面和真实分钟调度四组 E2E，共享 180 秒父预算；预期场景不得缺失。`integration` 保留 UI/System Smoke 诊断，其中受控 API 响应不得冒称真实业务。`release` 单独执行生产 requireAdministrator 构建与清单校验。CI 以 scope、可选 control、最多五个 Windows batch 和 必需汇总 执行逻辑义务；加上可信 main 的 begin/finalize 控制器，每 attempt 最多十个物理 job，每个完整 job 不超过 150 秒。必需汇总 汇总核验报告和 Actions 完整前序 job 时长；完整预算 继续审计 必需汇总 和控制器，最终收尾须用服务端记录复核；本地通过不代表远端验收或发布完成。准确命令见 `docs/testing/commands.md`。

生产/Test Host 构建的输出和中间目录分离；两者共享同一业务源实现，禁止将测试 EXE 发布给用户。功能测试统一使用 `NexusTestHost=true`、asInvoker 的隔离 Test Host，每次运行使用独立端口与独立 runtime 目录，运行数据、PID 与退出标记只写入已登记外部测试根的 `runs/<runId>/`，不读写用户实例的进程、端口或数据。托管层使用受控进程/模拟器 fixture，操作系统授权边界通过平台适配器契约验证，不冒称普通权限已执行了系统级操作。

先搜索现役测试，在最低有效层增加覆盖。普通业务测试验证 API、结果、状态、文件效果和生命周期，不读取源码函数体匹配实现。UI Smoke 只保留必须跨浏览器证明的核心流程，总量不超过 12；不新增持久视觉截图/像素/布局基线，不断言私有 class、DOM 层级或装饰文案。临时人工浏览器验证材料放系统临时目录并按本次所有权清理。

正式核心门禁中的新增测试必须直接验证项目核心功能的运行结果、状态、持久化或恢复。只检查卡片排列、分隔线、标题文案、CSS 类等展示细节，不能充当核心功能测试纳入现役门禁；此类改动使用类型检查、生产构建和人工验收核查。违反本条属于严重违规。

测试失败保留原始报告并修根因。禁止自动重试掩盖不稳定、跳过失败、catch 后成功或降低通过条件。命令非交互、UTF-8、实时显示阶段/用例/退出码，不加无条件 pause。Python 用于适合的文件/数据与辅助脚本；既有 dotnet/node/npm/.cmd 入口按当前文档执行。

## 5. 插件与前端

宿主默认 stable 商店；`pluginRepository.channel` 只接受 stable/develop，通过配置文件及重启生效，普通设置 API 不开放此字段。develop 指向官方 `plugins-develop` Release 资产。通道缓存、ETag、pending、安装归属和冻结包身份一致；禁止跨通道缓存回退、候选按名称重新取包或无可信归属接管目录。

Preview 同版本 hash 变化可更新；hash 相同不因 sourceCommit 变化重装；可信 preview 可被同版本 stable 替换；高版本安装不自动降级。稳定包版本对应字节不可变。新增 journal 字段同步白名单、克隆、读写、恢复和 ownership 提交，保持当前格式数据安全。

专项插件只声明宿主支持的能力，前端代码写在 Host；专项不包含 frontend 对象、frontend-module、web/frontend 浏览器代码或 .NET 程序集。后端 judge/configEditor 脚本保持有效；configValidator 已退役，声明旧字段的包显式拒绝，配置诊断通过 taskProtocol discover.configAssessment，不改用户快照。managed 插件可通过 Frontend API 1.5、公开 slot/route 和 `nxp-*` Native Custom Elements 扩展。未声明能力不得静默当作支持。

`frontend/` 是唯一宿主前端源码；Vite 输出同步到发行 `wwwroot/`。`frontend/src/platform` 管平台服务，`app/bootstrap.ts` 管启动，`features/<domain>` 管业务。桥接仅通过 `plugin-bridge/host-adapter.ts` 使用宿主平台；app/features/ui 经 `@bridge/index` facade，不依赖 bridge 私有实现。插件不引用 Vue 私有组件/class。

复用 `ui/primitives` 与既有 CSS 变量、紧凑列表。页面适配 360/768/1280 视口，触控目标至少 40px；保持主题、焦点、ARIA 与可访问性。轮询/订阅经生命周期管理，离开页面释放。运行目标 Args 的路径与参数使用现役解析协议，不私自把命令行引号规则搬进持久化字段。

## 6. 交付与长期维护

README 描述当前产品，架构文档描述现役结构，TESTING 描述实际入口和测试政策，DEVELOPMENT 描述构建发布，CONTROL_PLANE 列公开能力，STATUS 只保留未完成问题，CHANGELOG 记录已发布历史。完成的迁移教程、版本专项清单、外部会话依赖、旧路径和零调用兼容层不进入长期维护入口。

保留低层工具和测试，不长期保留迁移债务豁免。修改公开能力同步控制面表、相应测试与官方插件作者文档。

交付时逐项列明改动、已运行命令/退出码、未运行范围及原因；本地测试通过不等于远端发布已启用，上传候选包不等于发布成功。正式完成前，在无父目录文档、无实施资料包的新 checkout 中验证导航、构建、测试和文档。

正式支持与验收范围为 Windows 11 x64，现役 Plugin API 精确为 2.0，Frontend API 为 1.5，自有 .NET 工程使用 .NET 10。1.0.0 前仍持续清理无调用的旧 Nexus 协议、别名、工具入口和自动迁移，不保留兼容 wrapper；外部上游最新稳定及前两版适配独立维护。旧配置、外观、历史及未知恢复现场原字节保留；破坏性版本在新目录安装、人工重新配置，独立升级屏障须先于资产公开生效。

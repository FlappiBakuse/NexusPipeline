# NexusPipeline 测试规范

本文件是测试层级、完整命令、CI 顺序和发布门禁的唯一详细来源。测试应在能够稳定证明契约的最低成本层完成，跨层测试保留真正依赖下一层运行时的行为。

## 测试层级

| 层级 | 目录或工程 | 运行产品进程 | 浏览器 | 主要职责 |
|---|---|---:|---:|---|
| L1 Unit | `tests/NexusPipeline.Tests/` | 否 | 否 | 模型规则、状态机、解析、规划、重试和边界校验 |
| L2 Component | `tests/NexusPipeline.Tests/` | 否 | 否 | 临时目录、仓储、配置事务、应用命令和外部端口替身 |
| L3 Web Logic | `tests/web/` | 否 | 否 | 原生 ES module 中可独立调用的格式化、分页和表单纯函数 |
| L4 System Smoke | `tests/system/` | 是 | 否 | Windows 进程、HTTP、解释器、端口回退、模拟器和更新事务边界 |
| L5 UI Smoke | `tests/e2e/tests/*.smoke.spec.mjs` | 是 | 是 | 页面加载、导航和少量关键用户工作流 |

`tests/stress/` 与 `tests/legacy/` 属于按需运行的诊断、压力和历史考据资产，不参与默认 CI，也不作为每个版本的固定发布门禁。

文档一致性检查是独立的工程治理检查，不归入 L1–L5 或 Web Logic：`tests/documentation/documentation-consistency.mjs`，覆盖 Markdown 本地链接、CHANGELOG 标题唯一性、README 导航存在性与双模式行为防线。

## 控制面覆盖规则

涉及可管理业务能力的改动，验收链路按以下顺序核对：

`Domain capability → Application command/service → HTTP/Web → CLI → MCP → docs/tests`

三端能力入口现状维护在 [CONTROL_PLANE.md](CONTROL_PLANE.md)；新能力落地后同步更新该表。

每项能力都必须记录一种明确状态：`supported`、`intentionally-ui-only`、`security-restricted` 或 `not-applicable`。纯视觉表现、布局、壁纸展示和前端路由可以保持 `intentionally-ui-only`；插件安装、更新、卸载、用户全局设置、插件用户设置和运行控制属于控制面能力，需具备正式 API、CLI/MCP 暴露或明确的 `security-restricted` 策略。CLI 通过 Control API 调用宿主服务，MCP 通过应用服务和同一套投影读取状态，适配器不得各自拼出独立领域规则。

## 测试归属规则

以下行为默认进入 Unit 或 Component：

- Normalize、参数范围、数量上限、UTF-8 字节长度和 ID 规则；
- CRUD mutation、排序、repository、DataStore 和状态恢复；
- 调度计算、执行准入、资源冲突、重试和完成状态机；
- Judge 关键字、判断脚本输出、历史计算和通知选择；
- 配置交换、快照同步、插件 capability 和模拟器路由；
- 插件 catalog schema 2、artifact/版本/官方 raw URL/changelog 校验、插件包安全边界、三档代理映射、缓存状态和跨重启安装恢复；
- 插件管理控制面投影、插件商店安装/更新/卸载事务、用户全局绑定覆盖和插件用户设置的脱敏/secret 风险策略；
- API payload 转换中可以独立出的业务规则。
- CLI/Control API 契约中的参数解析、目标解析、JSON envelope、退出码和轻量模式监听选项。
- MCP 工具 DTO、`OperationResult` 映射、脱敏设置、目标解析、队列执行完成操作策略和重启维护租约。
- Control API 身份握手、CLI 分层 HTTP 超时、通知测试失败错误码和维护期间宿主配置写入。

以下行为进入 UI Smoke：

- 页面可加载并显示状态；
- 主导航和关键二级页面可打开；
- 一个典型创建、编辑、删除流程可完成；
- 关键表单字段显隐、确认动作和粗粒度手机宽度检查。
- 插件本地/仓库入口可加载，并完成一个代表性插件贡献设置流程。

以下行为固定在低层测试或 System Smoke：

- DOM 层级、CSS/class/style、精确像素、SVG 数量、装饰性文案和每个选项的重复校对；
- 更新下载、应用、进程重启、代理持久化、模拟器字段契约、插件序列化和文件系统内容；
- 同一业务规则在多个页面的重复断言。

以下行为进入 System Smoke：

- release binary 启动、状态 API、端口回退和重启；
- release binary 的正式 CLI：`--json` 单 envelope、stderr 诊断、stdin JSON 校验和稳定退出码；
- release binary 的 CLI/Control API 长请求、通知失败协议、服务身份校验和重启接受后运行/配置冻结；
- fatal startup、进程树清理、detached child 和 Job Object；
- 真实 JavaScript/Python interpreter 边界；
- Generic ADB 与 MuMuManager stub command sequence；
- managed plugin assembly 加载与更新事务恢复。
- 插件 catalog 本地 fixture 的读取、插件目录归属和宿主更新不接管 `plugins/`。
- MCP Streamable HTTP 的真实握手、工具发现、结构化结果、loopback Host/Origin 安全边界、运行轮询/取消和端口冲突降级。

新增测试时先写低层 replacement，再决定是否需要保留一个高层 smoke。测试不得通过 retries、sleep、自动重启服务或跳过失败来掩盖不稳定性；`waitForTimeout` 不得承担业务同步职责。

## 默认命令

统一 Node 调度器是活动测试的规范入口。以下命令均在项目根目录执行：

```text
node tests\run.mjs unit
node tests\run.mjs web
node tests\run.mjs docs
node tests\run.mjs syntax
node tests\run.mjs build
```

`unit`、`web`、`docs`、`syntax` 和 `build` 可在受限 Codex 终端中运行。`default`、`ui`、`system`、`all` 属于组合入口，必须显式选择执行模式：

```text
node tests\run.mjs codex default
node tests\run.mjs codex ui
node tests\run.mjs codex system
node tests\run.mjs codex all

node tests\run.mjs admin default
node tests\run.mjs admin ui
node tests\run.mjs admin system
node tests\run.mjs admin all
```

省略 `codex` 或 `admin` 的组合入口会打印用法并返回 exit code `2`，避免测试结果失去运行语义。

## 权限边界与双模式门禁

正式产品构建使用 `requireAdministrator` 清单，满足脚本进程控制、系统操作和托盘运行边界。测试分成两个明确模式：Codex 本地反馈与 Administrator 正式门禁。

| Mode | 权限 | Runtime | LLM 可本地执行 | 正式管理员门禁 |
|---|---|---|---:|---:|
| Unit/Web/Docs/Syntax | 普通即可 | 不启动正式 App | 是 | 部分 |
| `codex` | 普通/受限即可 | `NexusTestHost=true` 的 Test Host | 是 | 否 |
| `admin` | High/System | `release/nexus-pipeline.exe` | 由 CI 执行 | 是 |
| GitHub CI | Administrator，UAC disabled | Production Release | 自动远程执行 | 是 |

执行管理员模式前可用以下命令核对完整性 SID：

```powershell
whoami /groups | Select-String 'S-1-16-(12288|16384)'
```

`S-1-16-12288` 表示 High，`S-1-16-16384` 表示 System。`admin default|ui|system|all` 与管理员 UI/System 直接入口在权限不足时打印诊断并返回 exit code `2`；不会触发 UAC、降级令牌或把阻断转成 skip。

### Codex Feedback Mode

`codex` 模式用于受限 Codex 的自主开发闭环：

```text
node tests\run.mjs codex all
```

UI/System 执行前会构建 `-p:NexusTestHost=true` 的临时宿主，使用 `src/app.test.manifest` 的 `asInvoker` 清单，运行目录为 `tests/.artifacts/test-host`。E2E/System 通过 `NEXUS_TEST_MODE=codex` 选择 Test Host，并使用隔离 runtime、stub、dry-run 和退出信号文件完成收尾。

启动输出会标明：

```text
NexusPipeline CODEX FEEDBACK TEST
Runtime: Test Host
Administrator validation: deferred to GitHub CI
```

Codex Feedback PASS 表示本地功能反馈通过，仍需等待 GitHub Administrator Gate。

### Administrator Gate Mode

`admin` 模式是正式质量门禁：

```text
node tests\run.mjs admin default
node tests\run.mjs admin ui
node tests\run.mjs admin system
```

入口要求 High/System 完整性，固定使用 `release/nexus-pipeline.exe` 的生产构建，并清除 `NEXUS_TEST_HOST`、`NEXUS_TEST_HOST_DIR` 与 `NEXUS_TEST_HOST_EXIT_FILE`。权限不足返回 `2`，不会切换到 Test Host。

直接执行 `npx playwright test` 或直接运行 System suite 时必须先设置明确的 `NEXUS_TEST_MODE`，推荐始终通过 `tests/run.mjs` 进入；缺少模式会直接报错。

## UI Smoke

Codex 本地反馈：

```text
node tests\run.mjs codex ui
```

GitHub Administrator Gate：

```text
node tests\run.mjs admin ui
```

两种入口运行相同的 Playwright assertions。Codex 入口构建并启动 Test Host，管理员入口构建并启动 `release/nexus-pipeline.exe`；global setup 会再次核对当前模式，管理员模式还会检查 Administrator / High Integrity 或 System Integrity。`tests/e2e/run-e2e.cmd` 保留为 Codex 反馈模式兼容转发入口。

当前 UI Smoke 使用四个 spec 文件：

```text
tests/e2e/tests/
├── app.smoke.spec.mjs
├── scripts-users.smoke.spec.mjs
├── queues.smoke.spec.mjs
└── settings-platform.smoke.spec.mjs
```

全套 UI Smoke 目标为 9～12 个，硬上限为 12 个。建议预算如下：

| 范围 | 建议数量 |
|---|---:|
| 应用入口与主导航 | 2 |
| 典型脚本流程 | 1 |
| 用户与绑定 | 2 |
| 队列与调度 | 2 |
| 设置与安全入口 | 1 |
| 插件商店/贡献入口 | 1 |
| 版本专项保留位 | 0～3 |

新增或保留 UI Smoke 前，测试说明应能回答“业务不变量、失败模式、最低充分层级”三项问题；能够在 Unit、Component、Web Logic 或 System Smoke 证明的行为不占用 UI 配额。断言优先定位稳定的 `data-testid`、`data-action` 和业务 ID；禁止依赖按钮顺序、装饰性 class、随机 CSS 层级、精确像素坐标、SVG 数量和完整磁盘文件内容。

UI Smoke 使用一次 global setup 启动当前模式的隔离副本、一次 global teardown 关闭服务。服务意外退出应直接暴露为测试失败；服务重启、端口占用、进程树和 interpreter 场景由 System Smoke 独立负责。

## System Smoke

Codex 本地反馈与管理员门禁均使用同一组 System Smoke assertions，模式由命令显式选择：

```text
node tests\run.mjs codex system
node tests\run.mjs admin system
```

统一入口当前按顺序覆盖 MCP、runtime（含 CLI/Control API）、judge、execution-resilience、emulator 和 update 六类跨层场景。Codex 模式使用 Test Host、test-host exit signaling、isolated runtime、stub 和 dry-run；admin 模式使用 production release 并要求 High/System。MCP suite 还覆盖默认关闭、启用后的官方协议握手、结构化工具调用、已有危险完成操作拦截、运行轮询/取消、轻量模式和固定端口占用降级；runtime suite 覆盖通知测试失败、长 Webhook 请求、身份握手和重启维护窗口。测试辅助进程必须使用 `tests/system/runtime*/` 等隔离目录，并在结束时关闭服务和清理现场。`tests/system/run-system.cmd` 保留为 Codex 反馈模式兼容转发入口。

## 按需诊断与历史资产

历史 Chaos 场景需要在完成 `build.cmd` 后运行，并确认使用历史工具自己的隔离 runtime：

```cmd
node tests\legacy\chaos\chaos-queue.mjs
```

持续 flake 诊断工具：

```cmd
node tests\stress\diagnostics\flake-monitor.mjs
```

`tests/legacy/README.md` 说明历史资产的边界。新 regression 应迁入 active tier；历史记录保留在 `tests/legacy/history/FLAKE-LEDGER.md`。

## CI 与发布门禁

CI 的主 job 和独立的 `system-tests` job 都先用完整性 SID 确认 Windows runner 为 High/System，再分别执行 `node tests\run.mjs admin default`、`node tests\run.mjs admin ui` 和 `node tests\run.mjs admin system`。GitHub Actions 不创建临时测试账户、不写入测试账户密码、不递归修改 workspace ACL，也不使用令牌降级启动器。发布前必须在 GitHub Administrator Gate 中完成这些测试并确认每项 exit code 为 `0`；任一项因权限检查返回 `2` 或因测试失败退出时，发布门禁未完成。发布验证记录实际通过数与耗时；Stress/Chaos 根据修改范围和专项风险决定。

CI 中 Playwright 使用 `trace: retain-on-failure`，本地保持 `trace: off`。UI Smoke 失败时由 `actions/upload-artifact@v4` 上传 `tests/e2e/test-results`，用于补充同一 step 中已经实时显示的 suite、case、错误和 stack 信息。

`NEXUS_CI` 不划分隐式 Playwright 测试集合。时间缩放仅适用于明确依赖宿主等待的专项脚本；判断脚本的 30 秒单次执行上限保持真实墙钟语义。测试中的日期断言遵循本地时区规则。

## Flake 处理

flake 视为测试或产品同步问题。处理顺序为：降低测试层级、去除共享状态、注入时钟或外部端口、缩小跨层场景。修复前保留可复现证据，不能通过自动重试或跳过断言消除红灯；新 flake 写入当前版本验证记录或 Issue。

## 测试隔离与清理

运行时数据必须位于 `tests/e2e/runtime/`、`tests/system/runtime*/`、`tests/stress/runtime/` 或历史工具专用的 `tests/legacy/runtime/`，禁止写入项目根目录的 `config/`、`data/`、`history/` 和 `logs/`。测试结束后停止产品进程，并清理 PID、runtime、test-results 和专项日志。

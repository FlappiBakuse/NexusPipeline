# NexusPipeline 测试规范

本文件是测试层级、默认命令、质量门禁和清理要求的唯一详细来源。测试以当前生产契约为准，当前格式以外的迁移、旧字段、旧目录和兼容别名不属于活动回归范围。

## 测试层级

| 层级 | 目录或工程 | 运行产品进程 | 浏览器 | 主要职责 |
|---|---|---:|---:|---|
| L1 Unit | `tests/NexusPipeline.Tests/` | 否 | 否 | 模型规则、状态机、解析、规划、重试和边界校验 |
| L2 Component | `tests/NexusPipeline.Tests/` | 否 | 否 | 临时目录、仓储、配置事务、应用命令和外部端口替身 |
| L3 Web Logic | `frontend/src/**/*.test.ts` | 否 | 否 | 可独立导入的 ES module 纯函数、Vue 组件契约和协议转换 |

L3 用例统一由 frontend Vitest 承载；`tests/web/` 已不再保留独立 Node 用例，`node tests\run.mjs web` 会提示该情况并以 `0` 结束。
| L4 Visual Contract | `tests/e2e/tests/*.smoke.spec.mjs` 的 screenshot contract | 是 | 是 | 固定视口下的关键 shell/page 视觉基线 |
| L5 System Smoke | `tests/system/` | 是 | 否 | Windows 进程、HTTP/CLI/MCP、诊断与运行解释、解释器、端口、模拟器和更新事务 |
| L6 UI Smoke | `tests/e2e/tests/*.smoke.spec.mjs` | 是 | 是 | 页面加载、导航和少量关键用户工作流 |

`tests/stress/` 是按需运行的压力与诊断资产，不参与默认发布门禁。历史测试容器已删除；需要追溯行为时使用 CHANGELOG 和 Git 历史。

文档一致性检查独立于 L1–L5：`tests/documentation/documentation-consistency.mjs` 检查 Markdown 本地链接、CHANGELOG 标题唯一性、README 导航、当前版本和已删除路径引用。

## 测试归属与写法

### 最低充分层级

- 参数范围、数量上限、UTF-8 字节数、ID 规则、状态机、更新自动化、重试、调度计算、配置交换、快照同步、插件 capability、历史计算和通知选择进入 L1/L2。
- API payload、DTO、脱敏、secret 策略、候选输入名、CLI envelope 和退出码在 Web Logic 或组件测试验证。
- 真实解释器、进程树、Job Object、端口回退、插件安装事务、模拟器命令序列、定期更新检查和更新恢复进入 System Smoke。
- UI Smoke 保留页面加载、主导航、典型 CRUD、用户绑定设置、队列调度、设置安全入口和插件浏览等用户工作流。

### UI Smoke 配额

当前浏览器验收保留 11 个用例，硬上限 12 个；其中 10 个是用户工作流，1 个是视觉契约：

```text
tests/e2e/tests/
├── app.smoke.spec.mjs                 2
├── scripts-users.smoke.spec.mjs       2
├── queues.smoke.spec.mjs              2
├── settings-platform.smoke.spec.mjs   4
└── visual-contract.smoke.spec.mjs     1
```

UI Smoke 断言用户可观察的结果和稳定业务状态，优先使用稳定的 `data-testid`、ARIA 状态和业务 ID；少量现有迁移用例仍可读取 `data-action` 作为定位属性，但它不再是运行时行为契约。Visual Contract 使用固定视口的 `toHaveScreenshot` 锁定 shell/page 视觉基线，并遮罩地址、版本等运行时动态值。业务行为不使用 CSS/class/style、精确像素、SVG 数量、装饰性文案、源码字符串、随机 DOM 层级或完整磁盘文件内容作为质量判断。低层已能稳定证明的每个字段、密钥、选项和 payload 不重复占用浏览器配额。

### Web Logic 与测试文件组织

- Web Logic 只能导入生产 ES module 的纯函数；禁止读取生产源文本、按函数名切片、正则解析函数边界或把实现字符串当作行为证据。
- `frontend/` 的 `npm run typecheck`、`npm run test` 和 `npm run build` 验证 Vue/TypeScript 组件、公共 `nxp-*` 元素和静态构建产物。
- Frontend API 1.5 的宿主外部契约由 `frontend/src/plugin-bridge/contract.test.ts` 覆盖：精确版本匹配、`host.*` 能力面（含二进制 `api.blob` / `api.upload`）、18 个公开 slot 白名单、renderer surface context 与清理、生命周期订阅与释放。
- 声明式插件表单控件由 `frontend/src/plugin-bridge/controls.test.ts` 表驱动覆盖：字段类型到公开 `nxp-*` 元素的映射、初始值与约束传递、单选与多选交互、开关载荷、必填错误投影与清理；实现边界由 `frontend/src/plugin-bridge/component-reuse.test.ts` 静态校验（桥接目录不得拼装控件 DOM、平台依赖只经 host adapter、字段类型必须映射到公开元素）。
- 服务重启恢复协议由 `frontend/src/platform/service-restart.test.ts` 覆盖：旧实例与无关 HTTP 服务被拒绝、同端口等待新实例、端口漂移与配置端口被占用时跳转到实际监听端口、超时与重试复用交接信息；背景表面的 Blob URL 归属由 `frontend/src/platform/appearance.test.ts` 覆盖。
- 宿主路由表结构由 `frontend/src/router.test.ts` 覆盖：宿主页面路由、插件 catch-all 路由、空 fallback 与按需加载方式。
- 页面 route token、定时器与 `AbortController` 生命周期由 `frontend/src/platform/page-state.test.ts` 覆盖。
- 插件 route 的真实装配与生命周期由 `frontend/src/router.integration.test.ts` 覆盖：经真实 `vue-router` 实例、`router.push()` 与 `RouterView` 驱动 `/plugin/:pathMatch(.*)*`，断言 `PluginRouteHost` 挂载、`resolvePluginRoute` 收到的 route segment、route handler 的 token 与 segments、`onPageEnter`/`onPageUpdated`、插件 route → 宿主 route 与插件 route → 插件 route 的 leave/dispose 次数、无效 route 回退 Dashboard，以及 query 变化时的页面代际语义。该文件只替换 `@bridge/index` facade 与 `plugin-bridge/host-adapter` 两个宿主边界，路由表、`RouterView`、`PluginRouteHost`、页面状态与启动编排使用生产实现。
- 前端候选配置请求由 `frontend/src/features/users/utils/configEditRequest.ts` 的 `buildConfigEditRequest` 负责构造，Vitest 直接验证输入与输出。
- 宿主语言资源以 `frontend/public/i18n/` 为唯一源；文档一致性检查按该资源校验资源键、调用点语境和宿主注册表，扫描范围为 `frontend/src` 的生产源码。
- xUnit 文件按子系统命名，例如 `ExecutionStateStoreTests.cs`、`PluginManagerTests.cs`、`ConfigSwapPrimitivesTests.cs` 和 `LogMonitorTests.cs`。禁止重新建立跨域的 `GovernanceUnitTests`、`BaselineReproductionTests` 或 `ExtensibilityCharacterizationTests` 容器。
- 测试替身显式实现当前接口；接口移除默认实现后同步所有 fakes。测试不为旧接口、旧 overload、旧数据形态或历史恢复路径保留兼容断言。
- 新增回归先放在最低有效层，再评估是否保留一个高层 smoke。测试应暴露根因，不通过 retries、无条件 sleep、自动重启或跳过断言掩盖不稳定性。

## 当前格式与恢复契约

- `.session`、`.session.bak`、`store-meta.json`、`edit-isolation`、`original-extra`、`swap-backup`、`store-txn` 和附加配置 manifest 只按当前 schema 读取；字段缺失、未知字段、未知目录或身份不明的现场保留并告警。
- 配置定位变化时，新位置缺失会阻断运行并保留旧快照；新位置存在时按当前配置重新建立 `store`，更新当前元数据，不跨定位复用旧快照内容。
- 配置恢复、快照事务、fail-closed、Windows 真实进程边界和第三方插件契约需要持续覆盖；删除的迁移链路不通过测试保留。
- 测试 runtime、日志、PID、服务停止信号和 Playwright 结果必须使用隔离目录。测试结束后确认进程退出，再清理本次产生的精确临时路径。

## 默认命令

以下命令均在 `NexusPipeline/` 根目录执行：

```text
dotnet test tests\NexusPipeline.Tests\NexusPipeline.Tests.csproj --nologo -m:1
npm run typecheck --prefix frontend
npm test --prefix frontend
npm run build --prefix frontend
node tests\run.mjs unit
node tests\run.mjs web
node tests\run.mjs docs
node tests\run.mjs tooling
node tests\run.mjs syntax
node tests\run.mjs build
```

`tooling` 运行 `tests/tools/ci-domains.test.mjs`，校验 CI 影响域清单与四个 System 分组的映射关系；`changes` 作业在判定影响域之前执行同一用例。

`codex all` 不执行 frontend Vitest；每次修改前端源码都必须显式运行 `npm test --prefix frontend`。

统一组合入口必须显式选择运行模式：

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

System Smoke 支持按影响域分组运行，便于 CI 与本地只跑受影响的 suite：

```text
node tests\run.mjs codex system [runtime|execution|emulator|update] [--realtime] [--dry]
```

可以列出多个分组，也可以用 `--group <名称>` 重复指定；省略分组等于全部 suite。未知分组会打印可用分组并以 exit code 2 退出。`--dry` 只列出将要执行的 suite，不构建也不启动运行时。

视觉契约需要在明确确认基线变化后刷新：

```powershell
$env:NEXUS_UPDATE_SNAPSHOTS = "1"
node tests\run.mjs codex ui
Remove-Item Env:NEXUS_UPDATE_SNAPSHOTS
```

`codex` 使用 `NexusTestHost=true` 的 `asInvoker` Test Host；`admin` 使用生产 release，并要求 Administrator / High Integrity 或 System Integrity。权限不足返回 exit code `2`，不降级运行。

## 质量门禁顺序

1. 修改宿主代码、测试或前端纯函数后运行 Unit/Component、Web Logic、Docs、Syntax、Visual Contract 和 `build.cmd` 的适用组合。
2. 涉及配置交换、Windows 进程、端口、解释器、插件、模拟器或更新事务时，追加 `node tests\run.mjs codex system`。
3. 发布前由 CI 在管理员上下文执行 `node tests\run.mjs admin default`、`admin ui` 和适用的 `admin system`，并核对每项 exit code 为 `0`。
4. 两仓库的宿主—插件契约发生变化时，在插件仓库执行 `python tools/repository.py validate-source`、`node tools/Test-FrontendPlugins.mjs` 和 `python -m unittest discover -s tools/tests -v`，并核对两仓库文档、manifest 和测试。
5. 修改 `frontend/src/plugin-bridge/**`、`frontend/src/platform/appearance.ts`、公开 `nxp-*` 元素或 Frontend API 契约时，必须执行 `NexusPipeline-Plugins/tools/Test-FrontendPlugins.mjs`。该脚本使用 mock host 运行插件入口，不读取宿主检出；插件仓库 CI 的 plugin-frontend Gate 不检出宿主，元素白名单在无宿主检出时使用脚本内维护的清单。宿主 CI 的插件契约 Gate 检出官方插件仓库并在宿主工作区内运行同一脚本，元素白名单来自同级宿主检出的 `NEXUS_PUBLIC_ELEMENTS`。managed-code 构建与打包使用 `host.lock.json` 锁定的宿主 commit。

   脚本当前校验：`frontend.apiVersion` 必须为 1.5、宿主私有结构 class 拒绝、`nxp-*` 元素必须来自公开元素集合、插件不得复制 Nexus UI 组件、渲染必须挂载 `nxp-collapsible-card` 与既有必需控件、卸载后不得残留定时器与 window 监听。CustomWallpaper 额外覆盖插件级壁纸运行时：激活即应用背景与配色、离开设置页面保留全局外观、页面访问不触发随机轮换、按时间轮换在无设置页面时继续生效、只有插件停用才清理外观与计时器。

构建、测试和发布命令保持实时输出；失败时保留失败项、原因摘要和必要的 runtime 证据。长任务不使用无反馈的超长等待。

## CI 与清理

CI 不创建临时测试账户、不写入测试账户密码、不使用令牌降级启动器，也不把日志、配置、密钥或运行产物加入版本库。Playwright 失败结果保留在 `tests/e2e/test-results/` 供同一 step 上传，测试结束后按项目 AGENTS.md 的精确清单清理。

`.github/workflows/ci.yml` 按影响域拆成六个 Gate：前端 Unit（ubuntu：`npm ci`、typecheck、Vitest、构建）、宿主 Core（windows：编译加 `node tests\run.mjs unit`）、文档与 i18n（ubuntu：`node tests\run.mjs docs`）、插件契约（windows：Plugin API 编译、`unit`、plugin-bridge 契约用例，并检出官方插件仓库运行 `node tools\Test-FrontendPlugins.mjs`）、管理员 UI Smoke（windows：`node tests\run.mjs admin ui`）、System 四域（windows：`node tests\run.mjs admin system runtime|execution|emulator|update`）。

影响域路径清单维护在 `tools/ci-domains.mjs`，判定脚本 `tools/ci-changes.mjs` 输出九个域布尔值（`frontend`、`host`、`docs`、`plugin`、`ui` 与 `system_runtime`、`system_execution`、`system_emulator`、`system_update`）。四个 System 域各自只覆盖对应的运行时路径，横切文件（宿主入口与启动、持久化、Web 控制面、插件加载、构建与测试入口）显式列入多个域。改动列表不可用、未命中任何影响域或命中共享路径时按全量门禁执行。映射关系由 `tests/tools/ci-domains.test.mjs` 固定，`changes` 作业在判定前运行该用例。每周 `schedule`（`17 3 * * 1`）与手动 `workflow_dispatch` 运行 `node tests\run.mjs admin all` 全量回归。

`tests/stress/diagnostics/flake-monitor.mjs` 仅在专项诊断需要时运行；新的 regression 直接进入当前 L1–L5 层级并补充对应文档事实。

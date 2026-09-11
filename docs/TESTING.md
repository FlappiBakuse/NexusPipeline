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
- Frontend API 1.4 的宿主外部契约由 `frontend/src/plugin-bridge/contract.test.ts` 覆盖：精确版本匹配、`host.*` 能力面、18 个公开 slot 白名单、renderer surface context 与清理、生命周期订阅与释放。
- 宿主路由与插件 route 生命周期契约由 `frontend/src/router.test.ts` 和 `frontend/src/platform/page-state.test.ts` 覆盖；页面/插件行为沿用 `codex ui` 的浏览器验收。
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
node tests\run.mjs syntax
node tests\run.mjs build
```

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
4. 两仓库的宿主—插件契约发生变化时，同时执行 `NexusPipeline-Plugins/tools/Test-Repository.ps1`，并核对两仓库文档、manifest 和测试。

构建、测试和发布命令保持实时输出；失败时保留失败项、原因摘要和必要的 runtime 证据。长任务不使用无反馈的超长等待。

## CI 与清理

CI 不创建临时测试账户、不写入测试账户密码、不使用令牌降级启动器，也不把日志、配置、密钥或运行产物加入版本库。Playwright 失败结果保留在 `tests/e2e/test-results/` 供同一 step 上传，测试结束后按项目 AGENTS.md 的精确清单清理。

`tests/stress/diagnostics/flake-monitor.mjs` 仅在专项诊断需要时运行；新的 regression 直接进入当前 L1–L5 层级并补充对应文档事实。

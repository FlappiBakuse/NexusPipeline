# NexusPipeline 测试规范

本文定义 v0.9.8 起的测试分层、默认命令和发布门禁。测试应在能够稳定证明契约的最低成本层完成，跨层测试只保留真正依赖下一层运行时的行为。

## 测试层级

| 层级 | 目录或工程 | 运行产品进程 | 浏览器 | 主要职责 |
|---|---|---:|---:|---|
| L1 Unit | `tests/NexusPipeline.Tests/` | 否 | 否 | 模型规则、状态机、解析、规划、重试和边界校验 |
| L2 Component | `tests/NexusPipeline.Tests/` | 否 | 否 | 临时目录、仓储、配置事务、应用命令和外部端口替身 |
| L3 Web Logic | `tests/web/` | 否 | 否 | 原生 ES module 中可独立调用的格式化、分页和表单纯函数 |
| L4 System Smoke | `tests/system/` | 是 | 否 | Windows 进程、HTTP、解释器、端口回退、模拟器 driver |
| L5 UI Smoke | `tests/e2e/tests/*.smoke.spec.mjs` | 是 | 是 | 页面加载、导航和少量关键用户工作流 |

`tests/stress/` 属于按需运行的压力与 soak 工具。Stress 不参与默认 CI，也不作为每个版本的发布硬门禁。

## 测试归属规则

以下行为默认进入 Unit 或 Component：

- Normalize、参数范围、数量上限、UTF-8 字节长度和 ID 规则；
- CRUD mutation、排序、repository、DataStore 和 migration；
- 调度计算、执行准入、资源冲突、重试和完成状态机；
- Judge 关键字、判断脚本输出、历史计算和通知选择；
- 配置交换、快照同步、插件 capability 和模拟器路由；
- API payload 转换中可以独立出的业务规则。

以下行为可以进入 UI Smoke：

- 页面可加载并显示状态；
- 主导航和关键二级页面可打开；
- 一个典型创建、编辑、删除流程可完成；
- 关键表单字段显隐、确认动作和粗粒度手机宽度检查。

以下行为进入 System Smoke：

- release binary 启动、状态 API、端口回退和重启；
- fatal startup、进程树清理、detached child 和 Job Object；
- 真实 JavaScript/Python interpreter 边界；
- Generic ADB 与 MuMuManager stub command sequence；
- managed plugin assembly 加载。

新增测试时应先写低层 replacement，再决定是否需要保留一个高层 smoke。测试不得通过 retries、sleep、自动重启服务或跳过失败来掩盖不稳定性；`waitForTimeout` 不得承担业务同步职责。

## 默认命令

在项目根目录执行：

```powershell
# L1/L2
dotnet test tests\NexusPipeline.Tests\NexusPipeline.Tests.csproj --nologo

# L3
$webTests = @(Get-ChildItem tests/web -Filter *.test.mjs -File | ForEach-Object { $_.FullName })
if ($webTests.Count -eq 0) { throw "未找到 Web Logic 测试文件" }
node --test $webTests

# 静态语法检查
Get-ChildItem tests\e2e\tests -Filter *.smoke.spec.mjs | ForEach-Object { node --check $_.FullName }

# 构建 release runtime
build.cmd
```

在 `tests/e2e/` 目录执行 UI Smoke：

```powershell
$env:PLAYWRIGHT_BROWSERS_PATH = "browsers"
npx playwright test
```

System Smoke 需要管理员终端和已完成的 `build.cmd`：

```powershell
tests\system\run-system.cmd
```

确定性的执行状态机套件由 System Smoke 统一入口运行；随机压力工具按需运行：

```powershell
node tests\legacy\chaos\chaos-queue.mjs
```

Judge 的业务规则由 xUnit replacement 覆盖；真实解释器边界由 `tests/system/judge-smoke.mjs` 覆盖；执行状态机的确定性真实进程场景由 `tests/system/execution-resilience.mjs` 覆盖。完整历史 E2E、Judge、Chaos 和 flake 资料统一保留在 `tests/legacy/`，供迁移核对和专项诊断使用。

## Playwright Smoke 约束

UI Smoke 使用四个 spec 文件：

```text
tests/e2e/tests/
├── app.smoke.spec.mjs
├── scripts-users.smoke.spec.mjs
├── queues.smoke.spec.mjs
└── settings-platform.smoke.spec.mjs
```

全套 UI Smoke testcase 总数控制在 12～18 个，版本完成后不得超过 20 个。断言优先定位稳定的 `data-testid`、`data-action` 和业务 ID；禁止依赖按钮顺序、装饰性 class、随机 CSS 层级、精确像素坐标、SVG 数量和完整磁盘文件内容。

普通 UI Smoke 使用一次 global setup 启动服务、一次 global teardown 关闭服务。服务意外退出应直接暴露为测试失败；服务重启、端口占用、UAC、进程树和 interpreter 场景由 System Smoke 独立负责。

## CI 与发布门禁

CI 顺序：

1. `dotnet test`；
2. PowerShell 枚举 `tests/web/*.test.mjs` 后运行 `node --test`，并执行 `node --check`；
3. `build.cmd`；
4. Playwright UI Smoke。
5. 独立 `system-tests` job 执行 `build.cmd` 与 `tests/system/run-system.cmd`，不加载 legacy。

发布前增加 System Smoke，并记录 Unit/Component、Web Logic、UI Smoke、System Smoke 的实际通过数与耗时。System Smoke 必须包含 runtime、judge、execution-resilience、emulator、update 五个阶段；Stress/Chaos 按修改范围和专项风险手动运行，结果写入验证记录。

`NEXUS_CI` 不再划分两套隐式 Playwright 集合。时间缩放仅适用于明确依赖宿主等待的专项脚本；判断脚本的 30 秒单次执行上限保持真实墙钟语义。

## Flake 处理

flake 视为测试或产品同步问题。处理顺序为：降低测试层级、去除共享状态、注入时钟或外部端口、缩小跨层场景。历史记录位于 `tests/legacy/history/FLAKE-LEDGER.md`，`tests/stress/diagnostics/flake-monitor.mjs` 只在诊断或专项调查时启动；新 flake 写入当版本 verification section 或 issue。

## 测试隔离与清理

运行时数据必须位于 `tests/e2e/runtime/`、`tests/system/runtime*/`、`tests/stress/runtime/` 或历史工具专用的 `tests/legacy/runtime/`，禁止写入项目根目录的 `config/`、`data/`、`history/` 和 `logs/`。测试结束后停止产品进程，删除 PID、停止信号、临时 runtime、test-results 和专项日志。

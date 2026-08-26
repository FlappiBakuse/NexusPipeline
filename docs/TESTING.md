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

文档一致性检查是独立的工程治理检查，不归入 L1–L5 或 Web Logic：`tests/documentation/documentation-consistency.mjs`。

## 测试归属规则

以下行为默认进入 Unit 或 Component：

- Normalize、参数范围、数量上限、UTF-8 字节长度和 ID 规则；
- CRUD mutation、排序、repository、DataStore 和 migration；
- 调度计算、执行准入、资源冲突、重试和完成状态机；
- Judge 关键字、判断脚本输出、历史计算和通知选择；
- 配置交换、快照同步、插件 capability 和模拟器路由；
- API payload 转换中可以独立出的业务规则。

以下行为进入 UI Smoke：

- 页面可加载并显示状态；
- 主导航和关键二级页面可打开；
- 一个典型创建、编辑、删除流程可完成；
- 关键表单字段显隐、确认动作和粗粒度手机宽度检查。

以下行为进入 System Smoke：

- release binary 启动、状态 API、端口回退和重启；
- fatal startup、进程树清理、detached child 和 Job Object；
- 真实 JavaScript/Python interpreter 边界；
- Generic ADB 与 MuMuManager stub command sequence；
- managed plugin assembly 加载与更新事务恢复。

新增测试时先写低层 replacement，再决定是否需要保留一个高层 smoke。测试不得通过 retries、sleep、自动重启服务或跳过失败来掩盖不稳定性；`waitForTimeout` 不得承担业务同步职责。

## 默认命令

统一 Node 调度器是活动测试的规范入口。以下命令均在项目根目录执行：

```text
node tests\run.mjs default
node tests\run.mjs unit
node tests\run.mjs web
node tests\run.mjs docs
node tests\run.mjs syntax
node tests\run.mjs build
```

`default` 按 Unit + Component → Web Logic → 文档一致性 → UI Smoke 语法 → 构建顺序执行，并实时转发每个子进程的输出。

## UI Smoke

在完成构建后，从管理员终端执行：

```text
node tests\run.mjs ui
```

入口会使用 `whoami /groups` 的完整性 SID 检查 High/System integrity。非管理员环境立即以 exit code `2` 退出，不在测试内部触发 UAC；`tests/e2e/run-e2e.cmd` 仅保留为兼容转发入口。

当前 UI Smoke 使用四个 spec 文件：

```text
tests/e2e/tests/
├── app.smoke.spec.mjs
├── scripts-users.smoke.spec.mjs
├── queues.smoke.spec.mjs
└── settings-platform.smoke.spec.mjs
```

全套 UI Smoke testcase 控制在 12～18 个，版本完成后不得超过 20 个。断言优先定位稳定的 `data-testid`、`data-action` 和业务 ID；禁止依赖按钮顺序、装饰性 class、随机 CSS 层级、精确像素坐标、SVG 数量和完整磁盘文件内容。

普通 UI Smoke 使用一次 global setup 启动服务、一次 global teardown 关闭服务。服务意外退出应直接暴露为测试失败；服务重启、端口占用、UAC、进程树和 interpreter 场景由 System Smoke 独立负责。

## System Smoke

System Smoke 需要管理员终端和已完成的构建，统一入口为：

```text
node tests\run.mjs system
```

统一入口当前按顺序覆盖 runtime、judge、execution-resilience、emulator 和 update 五类跨层场景。测试辅助进程必须使用 `tests/system/runtime*/` 等隔离目录，并在结束时关闭服务和清理现场。`tests/system/run-system.cmd` 仅保留为兼容转发入口。

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

CI 的主 job 通过 `node tests\run.mjs default` 执行 Unit + Component、Web Logic、文档一致性、UI Smoke 语法和构建，再通过 `node tests\run.mjs ui` 执行 Playwright UI Smoke；独立的 `system-tests` job 通过 `node tests\run.mjs build` 与 `node tests\run.mjs system` 执行构建和五阶段 System Smoke，不加载 legacy。发布前运行 UI Smoke 和适用的 System Smoke，并记录实际通过数与耗时；Stress/Chaos 根据修改范围和专项风险决定。

`NEXUS_CI` 不划分隐式 Playwright 测试集合。时间缩放仅适用于明确依赖宿主等待的专项脚本；判断脚本的 30 秒单次执行上限保持真实墙钟语义。测试中的日期断言遵循本地时区规则。

## Flake 处理

flake 视为测试或产品同步问题。处理顺序为：降低测试层级、去除共享状态、注入时钟或外部端口、缩小跨层场景。修复前保留可复现证据，不能通过自动重试或跳过断言消除红灯；新 flake 写入当前版本验证记录或 Issue。

## 测试隔离与清理

运行时数据必须位于 `tests/e2e/runtime/`、`tests/system/runtime*/`、`tests/stress/runtime/` 或历史工具专用的 `tests/legacy/runtime/`，禁止写入项目根目录的 `config/`、`data/`、`history/` 和 `logs/`。测试结束后停止产品进程，并清理 PID、停止信号、临时 runtime、test-results 和专项日志。

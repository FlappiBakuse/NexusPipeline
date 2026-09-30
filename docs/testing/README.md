# 测试层级与入口

## 测试层级

| 层级 | 目录或工程 | 运行产品进程 | 浏览器 | 主要职责 |
|---|---|---|---|---|
| xUnit | `tests/NexusPipeline.Tests/` | 否 | 否 | 模型规则、状态机、解析、规划、重试、临时目录、仓储、配置事务、应用命令和外部端口替身 |
| Frontend Vitest | `frontend/src/**/*.test.ts` | 否 | 否 | 可独立导入的 ES module 纯函数、Vue 组件契约和协议转换 |
| 核心 E2E | `tests/system/finite-*.mjs` | 是 | execution 使用 | 实际执行、配置交换/恢复、CLI/MCP/商店、分钟调度，两个 Host 槽位 |
| 显式诊断 | System Smoke、UI Smoke | 是 | UI Smoke 使用 | 历史边界诊断；受控 UI API 响应不能冒称实际业务 |

xUnit 通过 `InternalsVisibleTo` 访问宿主 internal 契约；文件与预期用例以现役策略和测试源码为准。托管层由 `node tests\run.mjs integration` 在同一次 asInvoker Test Host 发布后运行：UI Smoke 2 个用例（`app.smoke.spec.mjs`），System Smoke 11 个用例（`runtime-smoke.mjs` 7、`judge-smoke.mjs` 4）。两个 spec/system 入口之外的 `tests/system/runtime-helper.mjs` 与 `tests/system/task-protocol-fixture.mjs` 是共享夹具，不是独立套件。

核心入口为 `ci --group backend|frontend`，`smoke` 并行运行两组；具体选择和预期用例以 `tests/policy.json` 为准，数量由策略登记导出。`daily` 运行四条真实有限 E2E，具体规模见[命令与证明边界](commands.md)。未选历史文件保留作明确的按需诊断，不隐式追加运行。

Markdown 内链与片段由 `node tools/check-doc-links.mjs` 按文档改动显式检查，由选中的 control 文档义务执行，也可单独诊断，不属于普通核心命令；运行产物目录不参与扫描。

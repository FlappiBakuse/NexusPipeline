# 测试层级与入口

## 测试层级

| 层级 | 目录或工程 | 运行产品进程 | 浏览器 | 主要职责 |
|---|---|---|---:|---:|---|
| xUnit | `tests/NexusPipeline.Tests/` | 否 | 否 | 模型规则、状态机、解析、规划、重试、临时目录、仓储、配置事务、应用命令和外部端口替身 |
| Frontend Vitest | `frontend/src/**/*.test.ts` | 否 | 否 | 可独立导入的 ES module 纯函数、Vue 组件契约和协议转换 |
| 托管层 | `tests/system/`（System Smoke）、`tests/e2e/tests/*.smoke.spec.mjs`（UI Smoke） | 是 | UI Smoke 使用 | Windows 进程、HTTP/CLI/MCP、判断脚本、配置交换、端口和运行解释；页面加载、导航与关键用户工作流 |

xUnit 现役 15 个测试文件，通过 `InternalsVisibleTo` 访问宿主 internal 契约；前端 Vitest 现役 12 个测试文件。托管层由 `node tests\run.mjs integration` 在同一次 asInvoker Test Host 发布后运行：UI Smoke 3 个用例（`app.smoke.spec.mjs` 2、`scripts-users.smoke.spec.mjs` 1），System Smoke 17 个用例（`runtime-smoke.mjs` 7、`config-smoke.mjs` 3、`judge-smoke.mjs` 4、`mcp-smoke.mjs` 3）。

Markdown 内链与片段由 `tools/check-doc-links.mjs` 校验，作为 `node tests\run.mjs smoke` 的一个步骤运行。

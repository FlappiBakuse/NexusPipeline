# 测试层级与入口

## 测试层级

| 层级 | 目录或工程 | 运行产品进程 | 浏览器 | 主要职责 |
|---|---|---:|---:|---|
| L1 Unit | `tests/NexusPipeline.Tests/` | 否 | 否 | 模型规则、状态机、解析、规划、重试和边界校验 |
| L2 Component | `tests/NexusPipeline.Tests/` | 否 | 否 | 临时目录、仓储、配置事务、应用命令和外部端口替身 |
| L3 Web Logic | `frontend/src/**/*.test.ts` | 否 | 否 | 可独立导入的 ES module 纯函数、Vue 组件契约和协议转换 |

L3 用例统一由 frontend Vitest 承载；`tests/web/` 已不再保留独立 Node 用例，`node tests\run.mjs web` 会提示该情况并以 `0` 结束。

| L4 System Smoke | `tests/system/` | 是 | 否 | Windows 进程、HTTP/CLI/MCP、诊断与运行解释、解释器、端口、模拟器和更新事务 |
| L5 UI Smoke | `tests/e2e/tests/*.smoke.spec.mjs` | 是 | 是 | 页面加载、导航和少量关键用户工作流 |

`tests/stress/` 是按需运行的压力与诊断资产，不参与默认发布门禁。历史测试容器已删除；需要追溯行为时使用 CHANGELOG 和 Git 历史。

运行时效率诊断先构建 `tests/stress/RuntimeEfficiencyDiagnostic/RuntimeEfficiencyDiagnostic.csproj`，再执行 `node tests\stress\runtime-efficiency.mjs`；默认测量 600 个调度 tick、100 MiB 日志 checkpoint 和一次追加读取，使用隔离 runtime 输出机器可读 JSON，结束后清理临时目录。

文档一致性检查独立于 L1–L5：`tests/documentation/documentation-consistency.mjs`、`i18n-consistency.mjs`、`i18n-semantic-consistency.mjs`、`i18n-audit-consistency.mjs`、`backend-i18n-audit.mjs`、`native-scrollbar-audit.mjs` 和 `test-policy-consistency.mjs` 共七个脚本共同构成 Docs 门禁。它们分别检查 Markdown/版本/导航、语言资源、语义键、审计清单、后端本地化、原生滚动条约束以及持久化测试政策。

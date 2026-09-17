# 测试域与 CI

## CI 与清理

CI 不创建临时测试账户、不写入测试账户密码、不使用令牌降级启动器，也不把日志、配置、密钥或运行产物加入版本库。Playwright 失败结果保留在 `tests/e2e/test-results/` 供同一 step 上传，测试结束后按项目 AGENTS.md 的精确清单清理。

`.github/workflows/ci.yml` 按影响域拆分基础 Gate 与独立的 System Smoke 子作业：前端 Unit（ubuntu：`npm ci`、typecheck、Vitest、构建）、宿主 Core（windows：编译加 `node tests\run.mjs unit`）、文档与 i18n（ubuntu：`node tests\run.mjs docs`）、插件契约（windows：Plugin API 编译、`unit`、plugin-bridge 契约用例，并检出官方插件仓库运行 `node tools\Test-FrontendPlugins.mjs`）、管理员 UI Smoke（windows：`node tests\run.mjs admin ui`），以及 System 的 runtime、execution、emulator、update 四个独立作业。计划任务与手动触发的 full-regression 会执行完整组合。

影响域路径清单维护在 `tools/ci-domains.mjs`，判定脚本 `tools/ci-changes.mjs` 输出九个域布尔值（`frontend`、`host`、`docs`、`plugin`、`ui` 与 `system_runtime`、`system_execution`、`system_emulator`、`system_update`）。四个 System 域各自只覆盖对应的运行时路径，横切文件（宿主入口与启动、持久化、Web 控制面、插件加载、构建与测试入口）显式列入多个域。改动列表不可用、未命中任何影响域或命中共享路径时按全量门禁执行。映射关系由 `tests/tools/ci-domains.test.mjs` 固定，`changes` 作业在判定前运行该用例。每周 `schedule`（`17 3 * * 1`）与手动 `workflow_dispatch` 运行 `node tests\run.mjs admin all` 全量回归。

`tests/stress/diagnostics/flake-monitor.mjs` 仅在专项诊断需要时运行；新的 regression 直接进入当前 L1–L5 层级并补充对应文档事实。

# Legacy 测试资产

此目录保存已经被当前测试层替代的历史专项、完整 E2E、历史 Chaos 和 flake 资料。它们用于行为考据、专项诊断和迁移核对；默认测试入口、持续集成和发布门禁均不加载此目录。

> Legacy tests are historical characterization/diagnostic assets.
> They are excluded from CI and release gates.
> Do not add new regression coverage here.
> Any bug reproduced from a legacy test must receive a minimized replacement in an active tier.

当前替代关系：

- `tests/system/execution-resilience.mjs` 承担确定性的执行状态机保护。
- `tests/system/run-system.cmd` 承担每次 PR 与 main push 的系统门禁入口。
- `tests/stress/diagnostics/flake-monitor.mjs` 是按需运行的进程/端口诊断工具。

历史脚本如需专项运行，应先完成 `build.cmd`，并确认其运行时目录位于 `tests/legacy/runtime/`，避免将测试数据写入项目根目录。

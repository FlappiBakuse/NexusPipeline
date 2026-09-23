# 测试域与 CI

## CI 与清理

CI 不创建临时测试账户、不写入测试账户密码、不使用令牌降级启动器，也不把日志、配置、密钥或运行产物加入版本库。Playwright 失败结果保留在 `tests/e2e/test-results/` 供同一 step 上传，测试结束后按项目 AGENTS.md 的精确清单清理。

`CI` 的 `Host / Required` 聚合检查按完整 PR diff 选择文档或代码范围。代码范围运行 `fast`，使用固定的官方插件 checkout；docs-only 不安装 .NET 或构建宿主。合并后的 `Host Release` 候选任务独立运行 `integration`、生产构建和包验收，成功后才上传候选 artifact。

本地测试归属由 `tests/registry.mjs` 唯一维护，包含 Host、Frontend、System 和治理测试文件；它不记录 CI job、权限、diff 计划或结果证明字段。PR 范围由 `tools/ci-scope.mjs` 完成；未知范围保守扩大，Required 不把空选择或意外跳过视为成功。`node tests\run.mjs all` 在本地依次执行快速与集成检查。

`tests/stress/diagnostics/flake-monitor.mjs` 仅在专项诊断需要时运行；新的 regression 直接进入当前 L1–L5 层级并补充对应文档事实。

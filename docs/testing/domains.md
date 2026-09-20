# 测试域与 CI

## CI 与清理

CI 不创建临时测试账户、不写入测试账户密码、不使用令牌降级启动器，也不把日志、配置、密钥或运行产物加入版本库。Playwright 失败结果保留在 `tests/e2e/test-results/` 供同一 step 上传，测试结束后按项目 AGENTS.md 的精确清单清理。

最终 Qualification workflow 按五个固定 Gate 拆分独立作业：H1 core、H2 frontend-contract、H3 ui-runtime、H4 execution-emulator、H5 update-acceptance。每个作业在 Host checkout 下准备自身的 Node 24、Python 3.13、.NET 8.0.424 和所需 npm 依赖；H2 使用显式的官方插件 checkout，H3–H5 使用隔离 Test Host。PR Feedback 只提供四个粗粒度范围，不写最终 App check。

本地测试归属由 `tests/registry.mjs` 唯一维护，包含 Host、Frontend、System 和治理测试文件；它不记录 CI job、权限、diff 计划或结果证明字段。PR Feedback 的四范围选择由 `tools/ci-scope.mjs` 完成；最终 Qualification 不做路径跳过，也不读取 diff。`node tests\run.mjs release all` 按 H1→H5 顺序执行完整资格门禁。

`tests/stress/diagnostics/flake-monitor.mjs` 仅在专项诊断需要时运行；新的 regression 直接进入当前 L1–L5 层级并补充对应文档事实。

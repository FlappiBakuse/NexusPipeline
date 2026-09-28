# 测试域与 CI

## CI 与清理

CI 不创建临时测试账户、不写入测试账户密码、不使用令牌降级启动器，也不把日志、配置、密钥或运行产物加入版本库。

`.github/workflows/ci.yml` 只有一个作业 `Host / Required`：在 `windows-latest` 上检出源码、安装 .NET 8 与 Node 24，执行 `node tests/run.mjs smoke`，超时 20 分钟。它不按 diff 选择范围、不条件准备工具链、不上传 artifact。

push 到 `main` 后，`.github/workflows/release.yml` 的 candidate 作业执行 `node tests/run.mjs integration`，再由 `tools/host_release.py candidate` 构建并校验生产候选；成功后才上传候选 artifact，发布恢复只复用原候选字节。

托管层按文件区分测试域：System Smoke 为 `tests/system/runtime-smoke.mjs`、`config-smoke.mjs`、`judge-smoke.mjs`、`mcp-smoke.mjs`，UI Smoke 为 `tests/e2e/tests/app.smoke.spec.mjs` 和 `scripts-users.smoke.spec.mjs`。每个 suite 使用独立端口和独立 runtime 目录，运行数据、PID 与退出标记都落在本次运行目录内；测试结束按本次身份清理进程与目录，不接触用户实例。Playwright 失败结果保留在 `tests/e2e/test-results/` 供本地诊断，清理按项目 AGENTS.md 的精确清单执行。

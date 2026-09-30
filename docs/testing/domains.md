# 测试域与 CI

## CI 与清理

CI 不创建临时测试账户、不写入测试账户密码、不使用令牌降级启动器，也不把日志、配置、密钥或运行产物加入版本库。

`.github/workflows/ci.yml` 使用范围判定、可选轻量 control、最多五个 Windows batch 与 Required；每个物理 job 三分钟硬停止。逻辑义务和原生用例由固定策略登记，同程序集选中用例并集执行一次，准备按同输入 key 复用。`Host / Required` 保留为兼容现役保护规则的轻量汇总：核验输入、计数、场景、清理以及 Actions API 返回的前置 job 实际总时长。失败、取消、意外跳过、缺报告或 API 不可达都不放行。

合入 `main` 后，`.github/workflows/release.yml` 按 source plan 分阶段构建前端、bundled 插件、正式 Host、ZIP 与 Setup，并验收候选清单、来源与包完整性；builder 与 writer 权限分离。发布恢复只复用原候选字节。发布权限和远端保护规则不由测试命令修改。

托管层按文件区分测试域：System Smoke 为 `tests/system/runtime-smoke.mjs` 与 `tests/system/judge-smoke.mjs`，UI Smoke 为 `tests/e2e/tests/app.smoke.spec.mjs`。每个 suite 使用独立端口和独立 runtime 目录，运行数据、PID 与退出标记都落在本次运行目录内；测试结束按本次身份清理进程与目录，不接触用户实例。Playwright 失败结果保留在外部测试根的 `runs/<runId>/ui/playwright/` 供本地诊断，清理按项目 AGENTS.md 的精确清单执行。

`daily` 四组各有独立 runtime、端口和报告，父预算合计 180 秒。核心命令时长不能冒充 GitHub job 含工具准备、上传和 post-action 的实际时间；远端验收须读取实际 run/attempt。

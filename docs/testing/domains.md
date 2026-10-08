# 测试域与 CI

## CI 与清理

CI 不创建临时测试账户、不写入测试账户密码、不使用令牌降级启动器，也不把日志、配置、密钥或运行产物加入版本库。

`Host / 构建检查` 是唯一 PR 必需检查，固定单 job 十分钟，直接调用 `tools/ci_check.py` 进行生产编译和有限严重架构静态检查。GitHub 不运行项目测试；文档和纯本机测试变更也生成固定成功结果。

从受保护 `main` 手动发起候选后，`.github/workflows/release.yml` 按 source plan 分阶段构建前端、bundled 插件、正式 Host、ZIP 与 Setup，并验收候选清单、来源与包完整性；builder 与 writer 权限分离。发布恢复只复用原候选字节。发布权限和远端保护规则不由测试命令修改。

托管层按文件区分测试域：System Smoke 为 `tests/system/runtime-smoke.mjs`、`tests/system/update-transaction.mjs` 与 `tests/system/judge-smoke.mjs`，UI Smoke 为 `tests/e2e/tests/app.smoke.spec.mjs`。System Smoke 共 13 个用例，包含真实 worker 提交与策略失败后的单次中止。每个 suite 使用独立端口和独立 runtime 目录，运行数据、PID 与退出标记都落在本次运行目录内；测试结束按本次身份清理进程与目录，不接触用户实例。Playwright 失败结果保留在外部测试根的 `runs/<runId>/ui/playwright/` 供本地诊断，清理按项目 AGENTS.md 的精确清单执行。

`daily` 四组各有独立 runtime、端口和报告，记录父命令完整实际耗时，不设执行时间预算。核心命令时长不能冒充 GitHub job 含工具准备、上传和 post-action 的实际时间；远端验收须读取实际 run/attempt。

Host 的 Python 门禁通过 `tests/support/unittest-runner.py` 输出原生用例 ID 和计数；本机报告拒绝空集合、跳过、预期失败及计数不符。发行契约组同时验证包、安装器元数据和候选 producer 来源。

`plugins.lock.json` 规定普通 PR 的默认对端为官方 Plugins 仓库 `main`，scope 在当次运行解析并固定其完整 SHA。配对 PR 使用受信 descriptor 中的真实对端 SHA，descriptor 同时绑定两仓输入锁字节。这个根文件不是 SDK 编译 SHA 或稳定预装包版本锁；正式预装字节由 `BundledPlugins.json` 及发行候选清单核验。


## 持续集成显示名称

唯一固定名称为 `Host / 构建检查`；旧本机批次的内部名称不决定远端 required context。完整候选和实际发布均由手动工作流发起。

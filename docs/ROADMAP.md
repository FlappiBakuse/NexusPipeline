# NexusPipeline 后续开发路线（Roadmap）

**编制日期**：2026-08-24｜ **当前版本**：v0.9.5｜ **下一开发版本**：v0.9.6｜ **发布模式**：v1.0.0 前一律 Pre-release、直接 push main；v1.0.0 起仅 PR 合入

> 本文档只记录尚未完成的版本计划、未来功能和仍需专项验证的风险。已完成版本的内容以 CHANGELOG、代码和测试结果为准。开工前先阅读项目 `AGENTS.md` 与本文件对应章节，并创建本地 `backup/vX.Y.Z-dev` 标签、同步项目版本号。已知问题台账见 [KNOWN_ISSUES.md](KNOWN_ISSUES.md)。

---

## v0.9.4：Runtime Monitor & Process Ownership Hardening

- [x] 将日志监控、Judge、配置同步、超时和进程检查拆分为独立 worker，避免单循环相互阻塞。
- [x] 建立独立 RunBudget watchdog，确保 TotalTimeout 使用严格 wall-clock 语义。
- [x] 为脚本强杀后的自重启和外部 watchdog 重新占用配置建立稳定观察窗口与恢复判据。
- [x] 区分瞬时无进程、稳定退出、owned PID 和 identity cleanup，收敛 PID 0 等混合语义。
- [x] 处理 Root PID 与 GameExe 同名时的排除边界。
- [x] 为 launcher 退出后脱离 Toolhelp 追踪的 detached child 建立 ownership 发现与清理模型。
- [x] 增加多种进程重启时序和 cleanup deadline 的专项 harness 与压力测试。

**v0.9.4 完成验证**：管理员构建、单元测试 195/195、加速与真实计时 E2E 87/87、judge 150/150、加速 chaos 167/167、真实 chaos 166/166 均通过。

---

## v0.9.5：内建通知、模拟器驱动与 Plugin API v1

- [x] 将 Webhook / SMTP 通知收归主程序通知领域，移除通知插件身份、状态和启停门禁。
- [x] 将模拟器适配收归主程序基础设施，建立通用 ADB 与 MuMu 独立 driver，并冻结一次运行的目标路由。
- [x] 建立独立的 `NexusPipeline.Plugin.Abstractions` Plugin API v1，支持配置、DPAPI 密钥、宿主通知和后台任务调度。
- [x] 增加 managed-code 插件 manifest、依赖隔离加载、启停配置与运行态状态；保留现有数据化专项插件兼容。
- [x] 补充通知、模拟器严格路由和 managed-code 插件 fixture 测试。

**v0.9.5 完成验证**：管理员构建通过（保留 3 个既有 nullable 警告）、单元测试 197/197、加速与真实计时 E2E 87/87、judge 150/150、chaos 166/166 均通过。

## 后续功能：自动签到通用插件

- [ ] 调研米游社/HoyoLab 与森空岛 API 的当前协议、凭证格式和变更风险。
- [ ] 基于 Plugin API v1 开发独立 managed-code 自动签到插件，通过插件 Scheduler、SecretStore 与宿主 Notification API 工作。
- [ ] 使用 SecretStore DPAPI 保存凭证，沿用 `enc:` 前缀、留空不变和不回显语义。
- [ ] 在脚本用户编辑区增加自动签到入口与凭证填写体验。
- [ ] 接入现有通知通道，并使用 mock API 服务器补充 E2E 回归。

---

## 后续功能：桌面分身

- [ ] 独立验证 Windows 会话创建（TS API / CreateProcessAsUser）。
- [ ] 独立验证 RDP 画面嵌入（mstscax ActiveX 或自绘协议）。
- [ ] 独立验证非前台会话的键鼠注入。
- [ ] 根据验证结果决定主线集成、窗口级降级方案或独立工具形态。

---

## 全局待决事项与专项验证

- [ ] 在真实机器人环境完成钉钉/飞书签名推送验证。
- [ ] 评估 judge、chaos 等专项测试进入 CI 的执行时长与资源隔离方案。
- [ ] 持续维护 `tests/e2e/FLAKE-LEDGER.md`，记录每次全量回归的 flake 现象、根因和处置。
- [ ] 持续维护版本号动态展示、真实计时回归和 Release 资产校验。
- [ ] 评估模拟器能力与并行调度矩阵的后续扩展边界。
- [ ] 评估判断脚本宿主接口扩展，如窗口、进程和 HTTP 探针能力。

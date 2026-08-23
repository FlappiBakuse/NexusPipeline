# NexusPipeline 后续开发路线（Roadmap）

**编制日期**：2026-08-23｜ **当前版本**：v0.9.3｜ **下一开发版本**：v0.9.4｜ **发布模式**：v1.0.0 前一律 Pre-release、直接 push main；v1.0.0 起仅 PR 合入

> 本文档只记录尚未完成的版本计划、未来功能和仍需专项验证的风险。已完成版本的内容以 CHANGELOG、代码和测试结果为准。开工前先阅读项目 `AGENTS.md` 与本文件对应章节，并创建本地 `backup/vX.Y.Z-dev` 标签、同步项目版本号。已知问题台账见 [KNOWN_ISSUES.md](KNOWN_ISSUES.md)。

---

## v0.9.4：Runtime Monitor & Process Ownership Hardening

- [ ] 将日志监控、Judge、配置同步、超时和进程检查拆分为独立 worker，避免单循环相互阻塞。
- [ ] 建立独立 RunBudget watchdog，确保 TotalTimeout 使用严格 wall-clock 语义。
- [ ] 为脚本强杀后的自重启和外部 watchdog 重新占用配置建立稳定观察窗口与恢复判据。
- [ ] 区分瞬时无进程、稳定退出、owned PID 和 identity cleanup，收敛 PID 0 等混合语义。
- [ ] 处理 Root PID 与 GameExe 同名时的排除边界。
- [ ] 为 launcher 退出后脱离 Toolhelp 追踪的 detached child 建立 ownership 发现与清理模型。
- [ ] 增加多种进程重启时序和 cleanup deadline 的专项 harness 与压力测试。

---

## 后续功能：自动签到通用插件

- [ ] 调研米游社/HoyoLab 与森空岛 API 的当前协议、凭证格式和变更风险。
- [ ] 设计内置 C# 通用插件，使用现有 IPlugin/INotifyChannel 契约并采用独立定时触发。
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

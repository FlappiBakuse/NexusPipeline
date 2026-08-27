# NexusPipeline 后续开发路线（Roadmap）

**编制日期**：2026-08-27｜**当前版本**：v0.10.9（Pre-release）｜**发布模式**：v1.0.0 前一律 Pre-release、直接 push main；v1.0.0 起仅 PR 合入

> 本文档只记录尚未完成的版本计划、未来功能和仍需专项验证的风险。已完成版本的内容以 [CHANGELOG.md](../CHANGELOG.md)、代码和测试结果为准。开工前先阅读项目 `AGENTS.md` 与本文件对应章节，并创建本地 `backup/vX.Y.Z-dev` 标签、同步项目版本号。已知问题见 [KNOWN_ISSUES.md](KNOWN_ISSUES.md)。

## 后续功能：自动签到通用插件

- [ ] 调研米游社/HoyoLab 与森空岛 API 的当前协议、凭证格式和变更风险。
- [ ] 基于 Plugin API v1 开发独立 managed-code 自动签到插件，通过插件 Scheduler、SecretStore 与宿主 Notification API 工作。
- [ ] 使用 SecretStore DPAPI 保存凭证，沿用 `enc:` 前缀、留空不变和不回显语义。
- [ ] 在脚本用户编辑区增加自动签到入口与凭证填写体验。
- [ ] 接入现有通知通道，并使用 mock API 服务器补充回归测试。

## 后续功能：桌面分身

- [ ] 独立验证 Windows 会话创建（TS API / CreateProcessAsUser）。
- [ ] 独立验证 RDP 画面嵌入（mstscax ActiveX 或自绘协议）。
- [ ] 独立验证非前台会话的键鼠注入。
- [ ] 根据验证结果决定主线集成、窗口级降级方案或独立工具形态。

## 活跃技术验证与决策

- [ ] 在真实机器人环境完成钉钉/飞书签名推送验证。
- [ ] 完成更新事务的进一步故障注入矩阵，覆盖长时间运行、文件锁和异常退出组合。
- [ ] 评估历史专项测试与 Chaos 诊断工具的执行时长、隔离和维护边界。
- [ ] 持续维护运行时版本动态展示、真实计时回归和 Release 资产校验。
- [ ] 评估模拟器 capability 与并行调度矩阵的后续扩展边界。
- [ ] 评估判断脚本宿主接口扩展，如窗口、进程和 HTTP 探针能力。

## 路线维护规则

- 项目完成并发布后，从本文件移除对应完成清单，在 `CHANGELOG.md` 记录发布内容。
- 未解决且影响当前版本的问题进入 [KNOWN_ISSUES.md](KNOWN_ISSUES.md)；修复后从台账移除，历史由 CHANGELOG 与 Git 保留。
- 新版本开发必须先创建本地开发基线备份并同步版本号；版本开发期间不拆分发布。

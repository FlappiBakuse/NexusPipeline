# NexusPipeline 后续开发路线（Roadmap）

**编制日期**：2026-08-30｜**最近发布**：v0.11.9（Pre-release）｜**发布模式**：v1.0.0 前一律 Pre-release、直接 push main；v1.0.0 起仅 PR 合入

> 本文档只记录尚未完成的版本计划、未来功能和仍需专项验证的风险。已完成版本的内容以 [CHANGELOG.md](../CHANGELOG.md)、代码和测试结果为准。开工前先阅读项目 `AGENTS.md` 与本文件对应章节，并创建本地 `backup/vX.Y.Z-dev` 标签、同步项目版本号。已知问题见 [KNOWN_ISSUES.md](KNOWN_ISSUES.md)。

## 后续功能：插件生态扩展

- [ ] 为更多官方 managed-code 插件补充 mock HTTP 与事件回归测试。
- [ ] 继续完善插件设置贡献的字段校验、错误呈现和迁移检查。
- [ ] 持续验证用户级插件数据清理、密钥保护和跨版本兼容行为。

## 后续功能：插件控制贡献

- [ ] 设计独立于 UI contribution 的 headless control contribution，声明稳定 ID、读写动作、输入输出 schema、风险级别和 secret 策略。
- [ ] 让 Web、CLI、MCP 共用该控制贡献，并保持现有 UI contribution 的兼容路径。
- [ ] 在 Plugin API minor 版本演进前完成现有插件迁移方案与控制面回归测试。

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

# 项目状态（Status）

**更新日期**：2026-09-20｜**发布模式**：`major=0` 或带 `-beta.N` / `-rc.N` 后缀的版本为 Pre-release；`major>=1` 且无后缀的版本为正式 Release。源码进入 `main` 必须经过 PR、当前候选的完整 Qualification 与 squash merge；日常开发目标为 `develop`。

> 本文件记录尚未完成的开发计划、活跃技术验证和当前未解决问题。已完成版本以 [CHANGELOG.md](../CHANGELOG.md)、代码和测试结果为准。版本号只在用户明确指定时修改；开发检查点保存在仓库外，不以 Git 标签代替文件备份。

## 当前未完成事项

- 两个仓库已安装资格控制面并配置独立 Qualification/Publisher App 与受保护 Environment；main Ruleset 和产品候选的完整远端资格仍待验收，不能以初始化 CI 代替产品资格。
- v0.16.7 已获发布授权；真实 GitHub preview/stable 发布、Host Release 和 Host 消费 preview 的端到端闭环尚待执行，完成前不认定版本已发布。

公共 UI 元件由对应 SFC 维护 DOM、交互和内部样式；feature 负责业务布局、状态与公开 props/events，插件通过注册的 `nxp-*` 元件消费宿主能力。

当前版本治理与验证结果记录在 [CHANGELOG.md](../CHANGELOG.md)；后续工作聚焦下方插件生态扩展和专项技术验证。前端架构现状以[架构索引](architecture/README.md)和[前端架构专题](architecture/frontend.md)为准；插件契约调整以[插件 API 索引](reference/plugin-api/README.md)为准。

## 后续功能：插件生态扩展

- [ ] 为更多官方 managed-code 插件补充 mock HTTP 与事件回归测试。
- [ ] 继续完善插件设置贡献的字段校验、错误呈现和目录元数据检查。
- [ ] 持续验证用户级插件数据清理、密钥保护和升级后的当前数据访问。

## 活跃技术验证与决策

- [ ] 在真实机器人环境完成钉钉/飞书签名推送验证。
- [ ] 完成更新事务的进一步故障注入矩阵，覆盖长时间运行、文件锁和异常退出组合。
- [ ] 持续维护运行时版本动态展示、真实计时回归和 Release 资产校验。
- [ ] 在 NexusPipeline-Plugins 的 `EmulatorSupport` 插件完成雷电、夜神和 BlueStacks 真机验证；实例识别、ADB 路由、启动/前台查询/截图/应用停止与安全关闭矩阵由[插件发行指南](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/RELEASING.md)维护。宿主 System Smoke 覆盖 Generic ADB、MuMuManager 和 managed-code provider 跨边界调用。

## 维护规则

- 发布完成后，从「当前未完成事项」与「活跃技术验证」移除对应完成项，在 `CHANGELOG.md` 记录发布内容。
- 未解决且影响当前版本的问题进入上方台账；修复、失效或确认为设计契约后立即移出，历史由 CHANGELOG 与 Git 保留。
- 新条目必须先确认当前代码路径、回归测试和影响范围，再写入台账。
- 发现文档与代码/测试冲突时，先核对 intended contract；涉及产品行为的决定需要单独确认，不通过文档修改掩盖冲突。
- 新版本开发必须先创建本地开发基线备份并同步版本号；版本开发期间不拆分发布。

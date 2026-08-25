# NexusPipeline 后续开发路线（Roadmap）

**编制日期**：2026-08-25｜ **当前版本**：v0.9.9（Pre-release）｜ **下一开发版本**：待定｜ **发布模式**：v1.0.0 前一律 Pre-release、直接 push main；v1.0.0 起仅 PR 合入

> 本文档只记录尚未完成的版本计划、未来功能和仍需专项验证的风险。已完成版本的内容以 CHANGELOG、代码和测试结果为准。开工前先阅读项目 `AGENTS.md` 与本文件对应章节，并创建本地 `backup/vX.Y.Z-dev` 标签、同步项目版本号。已知问题台账见 [KNOWN_ISSUES.md](KNOWN_ISSUES.md)。

---

## v0.9.8：测试体系精简与分层重构

- [x] 建立 Unit / Component / Web Logic / System Smoke / UI Smoke 五层测试模型与长期规范。
- [x] 将限额、用户/脚本/队列 mutation、判定、配置事务、调度和模拟器路由等确定性契约下沉到 xUnit 或 Node 内建测试。
- [x] 将 Playwright 收敛为 12～18 个关键用户路径，最终不超过 20 个 testcase。
- [x] 新增不依赖 Playwright 的 `tests/system/`，覆盖进程、HTTP、解释器和模拟器 driver 的少量跨层契约。
- [x] 将 `judge-scenarios` 迁移为确定性测试与 System Smoke；将 `chaos-queue` 调整为按需 stress/soak 工具，移出默认 CI 与发布硬门禁。
- [x] 重写 CI、开发文档和测试命令，取消 `NEXUS_CI` 双测试集与默认 flake 自动修复路径。
- [x] 完成构建、分层测试和发布前 System Smoke 验证，记录最终测试规模与耗时。

**v0.9.8 阶段验证（2026-08-25）**：Unit/Component 231/231；Web Logic 8/8；UI Smoke 15/15（4 specs，40.5 秒，含 360 宽度）；System Smoke 9/9（44.6 秒，UAC runtime）；release build 通过，保留 3 个既有 nullable 警告；所有新增/迁移 Node 脚本 `node --check` 与 `git diff --check` 通过。Stress/Chaos 已移至按需目录，本轮未纳入默认门禁。

## v0.9.9：UserId 数据一致性与绑定准入

- [x] 收敛配置交换、运行期脚本目录、自动更新事务和恢复扫描：`data/{脚本Id}/{UserId}` 为唯一写入路径。
- [x] 兼容脚本用户 API 按 Name 解析当前 `NexusUser`，再以 UserId 访问配置；历史用户名目录作为惰性遗留保留并跳过恢复。
- [x] 新增全局用户绑定时纳入脚本级活动租约与 `ScriptConfigGate`，快照复制位于 `DataLock` 外，快照失败回滚绑定元数据。
- [x] 冻结调度计划提供脚本/用户绑定查询，绑定更新与删除在待执行冻结计划存在时返回 409。
- [x] 增加 UserId 恢复、绑定运行冲突、快照失败和冻结计划精确匹配回归测试。

**v0.9.9 阶段验证（2026-08-25）**：Unit/Component 236/236；Web Logic 8/8；UI Smoke 15/15（4 specs，40.3 秒）；System Smoke 9/9（runtime 5/5、Judge 2/2、Emulator 2/2）；release build 通过，保留 3 个既有 nullable 警告；Smoke spec `node --check` 与 `git diff --check` 通过。

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

## v0.9.6：全局用户管理与脚本绑定

- [x] 新增仪表盘下方的独立全局用户管理界面，沿用项目卡片布局与拖拽排序交互。
- [x] 引入稳定 `UserId`、忽略大小写的全局用户名唯一性、改名迁移和同名旧用户合并迁移。
- [x] 支持头像首字母生成、自定义图片头像、用户运行优先级、绑定脚本数量和下一轮调度倒计时。
- [x] 在用户管理弹窗统一维护脚本绑定、参与运行、前置/后置脚本、自动签到占位和用户级通知设置。
- [x] 将脚本级通知作为一级开关，与绑定级通知开关共同决定用户运行通知；SMTP 支持绑定级收件人覆盖。
- [x] 移除脚本实例/调度队列的更多选项入口，统一右侧操作区；增加全局用户删除确认保险。

**v0.9.6 完成验证**：管理员构建通过（保留 3 个既有 nullable 警告）、单元测试 200/200、真实计时 Playwright 87 通过/3 跳过、judge 150/150、chaos 166/166 均通过。

## v0.9.7：用户管理界面重构

- [x] 用户管理弹窗按参考图重构：用户名/备注编辑区 + 已绑定脚本实例 1/2 双列卡片网格。
- [x] 添加脚本改为 1/1 全宽按钮 + 弹出面板（未绑定脚本实例可多选，确认/取消落位右下角）。
- [x] 绑定卡片点击展开为 1/1（其余卡片与添加面板隐藏），再点收回；同一时刻只展开一个。
- [x] 展开卡片内提供编辑配置（1/1）与自动签到占位、通知推送选项、高级选项（1/2）二级页。
- [x] 通知推送二级页（开启通知推送 + SMTP 收件人）、高级选项二级页（前后置脚本路径 + 运行天数）。
- [x] 备注（Remark）与运行天数（RunDays）后端落盘；运行天数每日递减，减至 0 不参与运行。
- [x] 自动签到入口改为绑定位占位卡片；用户管理界面移除用户级自动签到开关与空状态重复添加用户按钮。

**v0.9.7 完成验证**：管理员构建通过（保留 3 个基线 nullable 警告）；单元测试 204/204；发布前真实计时全量回归——Playwright 90/90、`judge-scenarios` 150/150、`chaos-queue` 166/166；`node --check` 与 `git diff --check` 通过。已发布 v0.9.7。

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

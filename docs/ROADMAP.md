# NexusPipeline 后续开发路线（Roadmap）

**编制日期**：2026-08-14（v0.6.10 交付与 v0.7.0 定版：2026-08-15）｜ **当前基线**：v0.6.10（已交付）｜ **发布模式**：v1.0.0 前一律 Pre-release、直接 push main；v1.0.0 起仅 PR 合入

> 本文档为当前版本之后的**可执行开发清单**：每个版本列出任务项、关键设计点、难度与验证要求。开工前先读项目 `AGENTS.md`（构建/测试顺序、运行时数据、环境陷阱、前端强约束）与本文件对应版本章节。**版本开工前**：打本地备份 tag `backup/vX.Y.Z-dev`（不 push）+ 同步 `src/NexusPipeline.csproj` `<Version>`。
> 已知问题台账（BUG / 死代码 / 文档不一致，按版本分步修复）见 [KNOWN-ISSUES.md](KNOWN-ISSUES.md)；全面评估报告见 [ASSESSMENT.md](ASSESSMENT.md)。

---

## 版本路线总览

| 版本 | 主题 | 难度 | 状态 |
|---|---|---|---|
| v0.6.9 | **测试 flake 治理（首要）** + 技术债清理（P1-P16） | ⭐⭐ | ✅ 已发布 |
| v0.6.10 | **长时脚本（-1 无限超时）** + 队列弹窗拖拽/任务卡片化 + 文档体系重组 | ⭐⭐⭐ | ✅ 已交付（2026-08-15） |
| v0.7.0 | **安卓模拟器（adb 方案）** | ⭐⭐⭐ | 未开始（技术验证后立项） |
| v0.7.0+ | 并行调度队列（资格矩阵收敛） | ⭐⭐⭐⭐ | 未开始（模拟器稳定后立项） |
| v0.8.0 | 自动签到通用插件 | ⭐⭐⭐ | 未开始（需 API 调研） |
| v0.8.0+ | 桌面分身 | ⭐⭐⭐⭐⭐ | 暂缓，拆分独立技术验证 |

---

## v0.6.9：测试 flake 治理（✅ 已交付，2026-08-14）

> 用户观察（2026-08-14）：多个版本（v0.6.6/v0.6.7/v0.6.8）的测试系统均触发多次 flake 现象——**本版首要目标为测试 flake 治理**，其次为 `docs/ASSESSMENT.md` 潜在问题清单 P1-P16（用户确认：队列任务「指定用户名」死字段**移除**）。本版为纯修复版本，无新功能。

### A. 测试 flake 治理（✅ 全部完成，2026-08-14 验证）

| # | 现象 | 根因 | 处置（v0.6.9） | 状态 |
|---|---|---|---|---|
| F1/F4 | 服务「无日志死亡」级联 ECONNREFUSED | `killRuntimeServices` 固定 600ms sleep 后启动 web，旧进程互斥体未释放 → web 模式互斥失败**静默退出**（仅 Info 日志，stdio ignore 丢弃） | ① `killRuntimeServices` 轮询确认进程完全消失；② `Program.cs` 互斥失败日志升级 Warn 带诊断；③ `waitForService` 失败 dump 进程/端口/日志尾部；④ `uitest/flake-monitor.mjs` 采样器（500ms，日志 `uitest/flake-monitor-logs/`）；⑤ spec 级 `ensureService` 兜底隔离级联 | ✅ 加速档 3 轮全量 60/60 无复现 |
| F2 | 02:193 删除偶发失败残留 | finally 删除不检查 res.ok、不确认消失；sid2 为 null 时删 null | finally 重写（res.ok + 轮询确认列表消失 + null 跳过）+ 用例开头按名防御清理 | ✅ |
| F3 | 05:113 重启后页面滞留 loading | reload 后模块加载/服务接管竞态 | 页面错误探针（pageerror/console.error）+ loading 滞留重载重试（3 次） | ✅ |
| F5 | chaos 丙单次成功轮采样丢失 | 判定→收尾窗口（数百毫秒）短于 100ms 采样间隔 | 乙/丙 seen 缺失时以历史记录 + 日志文件采样佐证（复用 maxDone noSkip 先例），其余用户保持严格 | ✅ |

- [x] **flake 台账机制**：`uitest/FLAKE-LEDGER.md`（现象/复现条件/根因/处置/回归记录），每次全量回归更新直至清零；`uitest/flake-monitor.mjs` 采样器配套
- [x] 验证：build + 单测 58 + e2e 60（**加速档 3 轮全绿**）+ judge 115 + chaos 166（加速档全绿；真实档 judge 115 / chaos 166 全绿）；发布前真实计时档全量另行执行

### B. 技术债清理（✅ 全部完成）

- [x] **P1 移除 `QueueTask.UserName` 死字段**：模型删除（无任何读取方）；前端/e2e/文档确认无引用
- [x] **P3 解依赖环**：`Logger` 独立取日志路径（`AppContext.BaseDirectory`），不再依赖 `Persistence.AppPaths`
- [x] **P2 自愈语义对齐**：`RecoverIfNeeded`/`TryRecoverItem` 的 cache 空分支统一——`GeneratedTemplate`（编辑会话模板产物）必须 `DoRestore` 清理，非模板会话仅清标记（防窄窗口误删用户新写入的 config）
- [x] **P5 Python 判断脚本 stderr 可观测性**：stderr 独立收集；无合法 JSON 输出时尾部放入 JudgeError + Logger.Warn（stdout 无结果时保持宽容回退 stderr 解析）
- [x] **P6 配置替换等待进程退出**：替换动作延迟到尝试收尾（杀进程确认退出后）应用，仅失败结果时应用
- [x] **P7 exit 完成操作收尾竞态**：`Application.Exit()` 延迟到队列 finally（FinishedAt/Unregister）之后
- [x] **P4 HistoryService 清理加锁**：`Cleanup` 与 `Save` 共享 Sync 锁
- [x] **P9 定时时间格式校验**：API 保存时严格 HH:mm 校验（"8:00" → 400 报错），Normalize 回退保留给旧数据
- [x] **P10 跨午夜服务日志滚动**：`Logger` 按天实时求值日志文件（随 P3 完成；`AppPaths.LogFile` 随 P13 孤儿 API 移除删除）
- [x] **P15 审计豁免文档修正**：AGENTS.md 措辞统一（审计行 INFO 随阈值过滤、无豁免）
- [x] **P12 token 输入层无障碍**：token-mask 自绘遮罩改 `showModal(..., locked)` 复用 modal 组件（role=dialog/aria-modal/焦点陷阱），移除内联 style 与硬编码色值
- [x] **P8 日志截断重读重复行**：部分截断（缩短未归零）从新文件尾续读；归零仍从头读（契约不变）
- [x] **P11 轻量模式托盘「打开管理页面」**：菜单项禁用 + tooltip + OpenWeb 防御提示
- [x] **P13**：令牌比较改 `CryptographicOperations.FixedTimeEquals`（常量时间）；`/api/logs` 孤儿 API 移除（无前端/e2e 引用）；静态文件补 `X-Content-Type-Options`/`Referrer-Policy`/CSP 安全头
- [x] **P14 resolve.json 占位符限制**：`plugins/README.md` 明示「占位符仅整体替换，不支持路径内拼接」
- [ ] **P16 钉钉/飞书签名真机验证**：需真实机器人环境推送验证——**延后验证**（用户确认 2026-08-14；签名逻辑已按官方规范修正并通过 Webhook 单测，真机推送验证留待有真实机器人环境时进行）

---

## v0.6.10：长时脚本 + 队列弹窗拖拽 + 文档体系重组（✅ 已交付，2026-08-15）

> **2026-08-15 定版（用户决策）**：已完成的全部开发（原计划 v0.7.0 阶段 A 的长时脚本、队列编辑弹窗拖拽排序、任务列表卡片化）与文档体系重组统一作为 **v0.6.10** 最后的补充版本交付；**未开工的安卓模拟器正式定为 v0.7.0**。

### 长时脚本实例（挂机场景）

- [x] **语义**：`LogStallTimeoutMinutes` 与 `TotalTimeoutMinutes` 均 = -1 → 长时脚本（无限超时）；**-1 必须成对**（任一 -1 另一正常 → 拒绝，`Limits.CheckScriptTimeouts`）
- [x] **校验体系改造**：前端 scripts.js（min/max 约束）与后端 `Limits.CheckStallMinutes/CheckTotalMinutes` 放开 -1（输入框 placeholder「填入 -1 表示不超时（长时脚本）」）；CLI ScriptsMenu 输入接受 -1
- [x] **队列混合校验**：长时脚本实例不能与普通脚本实例同队列——API 保存（`Limits.CheckQueueMix`，Web + CLI）+ 运行期防御（`DispatchCenter.StartQueue` 抛错 / `Scheduler.TriggerQueue` 跳过并记录 failed 历史）+ 前端 saveQueue 拦截、任务下拉标注「（长时）」
- [x] 前端「长时」徽章（卡片高亮经用户审阅后取消，仅保留徽章）；e2e 用例（-1 成对保存/徽章、长时运行不因 stall 超时失败、队列混排拒绝/纯长时通过）
- [x] 单元测试：`IsLongRunning` 判定、`CheckStallMinutes/CheckTotalMinutes(-1)`、`CheckScriptTimeouts` 成对（单测 58 → **62**）
- [x] 交互语义确认：长时下判断脚本周期触发（30 秒）与成功关键字「等待退出 60 秒」仍生效（运行时 `> 0` 判断天然支持 -1，RunSession 零改动）

### 队列编辑弹窗拖拽与卡片化

- [x] 定时列表/任务列表拖拽排序（任务列表上/下移按钮废除；`data-ts-idx` 写回修正、重排后 DOM 下标对齐防二次 sync 错写）——e2e 新增拖拽排序用例
- [x] 任务列表合并为整体卡片（与定时列表卡片同宽同构）、删除行内序号、删除按钮宽度统一 84px

### 文档体系重组

- [x] README 大众化重写、CONTRIBUTING 扩充、DEVELOPMENT/RELEASING 拆分、开发清单入库（本文件）、KNOWN-ISSUES 台账、ci.yml 编码修复、DESIGN/ARCHITECTURE 过时点修正

### 验证

- [x] build + 单测 62 + e2e 64（加速档）+ judge 115 + chaos 166 全绿（2026-08-15）；发布前真实计时档全量另行执行

---

## v0.7.0：安卓模拟器（优先 MuMu，技术方案已定：adb；验证后立项）

> **2026-08-15 定版（用户决策）**：v0.6.10 交付后，**v0.7.0 正式定版为安卓模拟器**（原 v0.7.0 阶段 A 长时脚本已随 v0.6.10 交付）。本版先做技术验证 demo（MuMu 实测），验证通过并出设计后再正式开发。

- [ ] **技术验证先行（demo）**：本机 MuMu 实测——模拟器命令行启动/关闭、`adb connect 127.0.0.1:16384`、`am start`/`am force-stop` 应用、`dumpsys activity` 前台应用检测、adb 路径解析（PATH/MuMu 安装目录兜底，参考 `ResolvePythonExe` 模式）；demo 不可行即降级方案，可行则出设计文档再正式开发
- [ ] **模型扩展**：`ScriptInstance` 新增游戏类型（通用/安卓模拟器）+ 模拟器字段（模拟器路径、adb 端口、应用包名、应用 Activity）；兼容旧配置（反序列化默认通用类型）
- [ ] **运行链路集成**（`RunSession`）：启动序列（模拟器未启动→启动；应用未在前台→启动）→ 运行中检测（应用前台监控）→ 失败重试（关闭应用→重启应用）→ 收尾（成功/最终失败→关闭应用→关闭模拟器）；进程树清理/游戏管理逻辑泛化（模拟器与游戏进程分离管理）
- [ ] **前端**：游戏配置区扩展（类型选择器 + 模拟器字段）；脚本卡片徽章「安卓模拟器」
- [ ] **测试**：e2e 用 stub 模拟 adb（假 adb 可执行脚本）+ 模拟器启动器伪进程；专项场景（启动/重试/收尾序列断言）
- [ ] Limits/路径校验扩展（模拟器路径存在性、包名格式）

### 随版修复（高优先级，排期按用户意愿可调整）

以下问题已在 [KNOWN-ISSUES.md](KNOWN-ISSUES.md) 台账登记，建议优先纳入 v0.7.x：

| 编号 | 问题 | 建议 |
|---|---|---|
| KN-01 | 损坏配置解析失败静默空值 → 保存覆盖丢数据 | v0.7.x |
| KN-02 | POST 注入已存在 Id → 重复 Id | v0.7.x |
| KN-03 | 队列重复触发（手动 + 定时并发双跑/双系统操作） | v0.7.x |
| KN-05 | CLI 删除脚本/队列残留 data 目录与门禁 | v0.7.x |
| KN-06 | 远程访问下脚本图标 401 | v0.7.x（前端） |

---

## v0.7.0+：并行调度队列（以现有松散并行为基础收敛）

- [ ] **先出设计文档**：并行资格矩阵——「仅含安卓模拟器脚本的队列可并行调度，且最多再并一个非模拟器队列」；其余组合串行（与现状一致的基线收敛）
- [ ] 并发点改造：队列进度/历史/通知/取消/完成操作并发化；调度器触发竞态（`_runningQueueIds` 扩展为资格矩阵判定）；脚本进程冲突检测保持原子
- [ ] 测试：chaos-queue 扩展并行场景（两队列并行注入、冲突矩阵）；该功能 BUG 风险高，建议模拟器 + 长时脚本稳定后再立项
- [ ] **前置文档化**：README/设计文档明确「队列内串行、队列间可并行（无全局上限）」现状边界

---

## v0.8.0：自动签到通用插件

- [ ] **前置调研**：米游社/HoyoLab API（参考 Womsxd/MihoyoBBSTools）与森空岛 API（参考 AEtherside/skland-daily-attendance）现状与凭证格式；协议变更风险评估
- [ ] **形态**（已定）：内置 C# 通用插件（IPlugin/INotifyChannel 契约）+ **独立定时触发**（宿主内置调度，不并入脚本队列）；凭证存 SecretStore DPAPI（`enc:` 前缀）
- [ ] 前端：脚本用户编辑区新增「自动签到」按钮 + 凭证填写框（密钥语义：不回显、留空不变）
- [ ] 通知集成（签到结果走现有通知通道）；e2e 用 mock API 服务器
- [ ] 数据化插件形态不适用（需网络/定时能力），本插件走编译内置——注意与 v0.6.3「专项插件数据化」方向不冲突（签到是通用能力插件）

---

## v0.8.0+：桌面分身（暂缓，拆分独立评估）

- [ ] 不做入主线版本。独立研究项三块 demo：① 独立 Windows 会话创建（TS API / CreateProcessAsUser）；② RDP 画面嵌入（mstscax ActiveX 或自绘协议）；③ 非前台会话键鼠注入
- [ ] 任一 demo 不可行即降级为「窗口级方案」或集成第三方工具；评估报告先行，再决定是否立项

---

## 全局待决事项与风险

| 项 | 说明 | 归属 |
|---|---|---|
| 钉钉/飞书签名真机验证 | 已按官方规范修正，需真实机器人环境推送验证——**延后验证**（用户确认 2026-08-14） | 待真机环境 |
| 专项测试进 CI | judge 115 + chaos 166 仅发布前本地跑；CI 超时 15 分钟撞墙风险 | 建议 v0.7.x 评估 |
| e2e 加速档 flake | F1-F5 已治理（v0.6.9：killRuntimeServices 轮询确认/ensureService 兜底/页面探针/采样佐证），台账 `uitest/FLAKE-LEDGER.md` 持续追踪 | v0.6.9 治理完成 |
| flake 台账 | 每次全量回归记录 flake 现象/复现条件/根因归属，直至清零 | v0.6.9+ 长期 |
| 版本号硬编码 | 01-core 已改动态读取；其余版本相关行为断言发版时核对 | 已基本解决 |
| 长时脚本与并行调度的依赖关系 | 并行矩阵依赖模拟器脚本类型判定；长时脚本已交付（v0.6.10） | v0.7.x 规划 |
| **判断脚本接口扩展（预留）** | 用户确认目前无计划：未来考虑在判断脚本输入中增加更多宿主接口（如窗口/进程/HTTP 探针）；记录不动作 | 长期方向 |
| **队列间并行现状文档化** | 当前队列内串行、队列间可并行（无全局上限）；并行调度（v0.7.0+）立项前需在 README 明确此边界 | v0.7.0+ 前置 |
| **v0.6.9 技术债** | 评估 P1-P15 已全部完成（P16 真机验证延后） | ✅ v0.6.9 完成 |
| **文档体系重组（2026-08-15）** | README 大众化重写、CONTRIBUTING/DEVELOPMENT/RELEASING 三份拆分、开发清单入库（本文件）、KNOWN-ISSUES 台账建立、docs 文档过时点修复 | ✅ v0.6.10 完成 |

---

## 开发流程提醒（AGENTS.md 摘要）

1. 版本开工：本地备份 tag `backup/vX.Y.Z-dev` + csproj 版本号同步
2. 破坏性/新增功能：备份 tag + 分步验证；改烂用 `git reset --hard backup/...` 本地还原
3. 测试分层：仅前端 → build + e2e 全量；涉及后端 → + judge + chaos + 单测；**发布前真实计时档全量**
4. 提交：Conventional Commits（type 英文 + 描述中文），同版本累积一个 commit，未经要求不拆
5. 发布：`gh release create --prerelease` + zip（排除 config/）+ 成对 `.sha256`；gh 中文操作三坑（UTF-8 文件/备份原 body/输出编码）
6. 收尾：发布后更新本文件勾选状态与 KNOWN-ISSUES 台账状态

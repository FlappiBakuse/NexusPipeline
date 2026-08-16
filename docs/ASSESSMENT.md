# NexusPipeline 全面评估报告

> 编制背景：v0.6.8 开工前对项目（LLM 全程开发）的一次完整评估，含核心业务/功能板块设计逻辑核对、潜在问题清单与后续开发方向。对齐结论已与用户确认。
> **评估记录**：① 2026-08-14（v0.6.8 前）首版（下文第 1-6 节）；② 2026-08-16（v0.7.6 交付后）破坏性更新专项评估 + 内容守护修复（第 7 节）。

## 1. 对齐结论（与用户确认）

| 板块 | 用户设想 | 实际实现 | 结论 |
|---|---|---|---|
| 完成判定机制 | 日志为真相（非进程退出码），三种触发时机 | `SessionJudge` + `LogMonitor`：日志新增批次/30 秒阻塞周期/进程退出最终触发；FileId 替换检测、按尝试切片防跨尝试污染 | ✅ 一致，实现更完备 |
| 多账号配置交换 | 运行前切换、运行后还原、各账号隔离 | original > config > store 保全序 + .session 标记 + 跨进程 Mutex + 后台 10 秒重试自愈；**代价：运行期间脚本写入 configPath 的文件会被删除** | ✅ 一致（用户接受隔离代价） |
| 判定优先级模型 | 判断脚本 > 关键字 > 进程退出；失败快停、成功等退出 60 秒 | `SessionJudge` 状态机：失败命中立即终止、成功命中等待 60 秒宽限；判断脚本可返回 `replaceConfigs` 选择性重试 | ✅ 一致 |
| 队列任务「指定用户名」 | 字段暴露但语义从未实现（死字段） | `QueueTask.UserName` 全库无消费，运行时静默跑全部启用用户 | 🔴 用户决策：**移除该字段**（纳入 v0.6.9） |
| 判断脚本接口扩展 | 未来可能增加更多宿主接口（窗口/进程/HTTP 探针等） | 目前仅 readFile/writeFile/listFiles 三类 | 📝 预留方向，无计划不动作 |

## 2. 核心业务板块设计逻辑（代码验证）

### 2.1 运行链路（RunSession.RunAttemptAsync，src/Services/RunSession.cs:361）
前置检查 → 启动游戏（可选，轮询确认 GameWaitSeconds）→ 启动主程序（已在运行仅监控）→ 日志监控初始化（严格 fresh：尝试开始前不存在的文件才从头读，否则按尝试开始长度续读）→ 1 秒监控循环（路径轮换/FileId 替换/截断检测 → 逐行判定）→ 判定分支（关键字/判断脚本/进程退出六分支）→ 超时（LogStallTimeout / TotalTimeout 整个运行计时）→ 尝试结束清理（KillAndConfirmExited 进程树 + 按名轮询强杀防自重启）→ 重试循环（replaceConfigs 已应用）→ finally 收尾（替换还原 → 清脚本区 → 配置交换还原，顺序固定）。

### 2.2 完成判定（SessionJudge）
- 优先级：判断脚本（启用即忽略关键字）→ 成功/失败关键字（组内逗号 AND——整个尝试日志中分别出现即命中（v0.7.1+）、换行 OR）→ 进程自行退出。
- 状态机：失败优先（`_failureSeenAt <= _markerSeenAt`），成功/失败各只记首次命中。
- 判断脚本契约：输入 JSON（脚本字段+用户+config 只读/script 可读写文件清单+本次尝试日志段+timeScale）、输出 stdout 尾行 JSON（status/reason/notifyText/replaceConfigs）、30 秒上限（不随加速缩放）、Jint 沙箱（readFile 限 2MB 范围、writeFile 防 `../` 逃逸）。

### 2.3 配置交换（ConfigSwapSession/Primitives/Paths）
- 运行前：.session 标记先行 → config 整体 MoveAs 到 original → store 快照 CopyAs 到 config；任一步失败自动回滚。
- 运行后：清 config → original 移回 → 清标记；Missing 形态还原为「不存在」。
- 崩溃自愈：启动扫描 RecoverInterrupted（含旧名迁移 MigrateLegacyLayout）+ 后台 10 秒重试循环（孤儿进程占用时等待）；恢复前检测脚本进程仍在运行则跳过。
- 数据保全序：original（原配置）> config（运行时生效）> store（用户快照，可重建）。

### 2.4 日志监控（LogMonitor）
三种文件形态：追加（增量读）/ 截断（Length<position 归零重读）/ 替换（FileId=卷序列号+文件索引对比，根治 move 归档句柄残留）；严格 fresh 判定（LastWriteTime ≥ 尝试开始时间）。

### 2.5 调度队列（Scheduler/DispatchCenter）
- 队列内按 Index 串行；同一脚本三层防重入（Register 锁内查重 / 进程检测 / ScriptConfigGate 信号量排队）。
- **队列间可并行**（无全局上限）——v0.7.0+ 并行立项前需文档化此边界。
- 定时：分钟粒度秒级检测，错过整点不补跑；完成操作 exit 立即 / sleep/reboot/shutdown 60 秒倒计时（Web 卡片可取消，真实墙钟）。

### 2.6 插件体系
- 通用插件：内置 NotifyPlugin（IPlugin/INotifyChannel，Webhook+SMTP 并行，DPAPI 加密）。
- 数据化专项插件：plugins/<名称>/plugin.json + data/{resolve.json, judge.{js,py}, config-template/}；require 校验（searchUpward 最多 4 层）+ paths 模板（{var} 绝对 / {rel:var} 相对）；判断脚本按扩展名定语言、宿主固化不可编辑。
- 内置四插件：bettergi（失败任务改写 TaskEnabledList 选择性重试）、march7th（管理端/执行端分离 + 6 失败模式 + ERROR 快速失败）、zzzonedragon（部分失败仍 success + notifyText）、maaend（MXU 任务级选择性重试，最复杂）。

### 2.7 Web 层
特性路由（[ApiRoute] 反射扫描）、远程 Bearer 令牌 + Origin 校验（CSRF/rebinding）+ 每 IP 5 次失败锁 60 秒、/api/fs 白名单、请求体 10MB 上限、GET /api/status 审计豁免。

## 3. 潜在问题清单（P1-P16，已归档）

> 首版评估产出 P1-P16（高优先 7 项：死字段/自愈语义不一致/依赖环/历史清理并发/判断脚本 stderr/P6 替换时机/P7 完成操作收尾；中优先 9 项：日志截断/时间格式/跨午夜滚动/轻量托盘/token 弹窗/安全加固/占位符文档/审计措辞/Webhook 状态码）。**全部已随 v0.6.9 修复**（详见 CHANGELOG v0.6.9；P16 钉钉/飞书签名真机验证延后至真实机器人环境），本节不再保留条目明细。后续评估产出见 KNOWN-ISSUES.md 台账（仅未修复项）与本节第 7 节（v0.7.6 专项）。

## 4. 后续开发方向（与 ROADMAP 对应，当前基线 v0.7.6）

| 版本 | 主题 | 难度 | 建议 |
|---|---|---|---|
| v0.7.7 | KN-77 自动更新配置内容守护修复（已交付未发布）；台账 KN-78~83 随版项按用户意愿排期 | ⭐ | 版本号待用户确认；修复与测试已全绿 |
| v0.7.0+ | 并行调度队列（v0.7.0 已交付后立项） | ⭐⭐⭐⭐ | 先出资格矩阵设计文档 + README 明确队列间并行现状 |
| v0.8.0 | 自动签到通用插件 | ⭐⭐⭐ | 前置 API 调研；需新增队列外独立定时通道 |
| v0.8.0+ | 桌面分身 | ⭐⭐⭐⭐⭐ | 维持暂缓，三块 demo 评估先行 |

> 已交付版本（v0.6.8~v0.7.6）的开发明细见 [CHANGELOG.md](../CHANGELOG.md) 与 [ROADMAP.md](ROADMAP.md)。

## 5. 架构符合性核查

- Models 无依赖 ✅；Services 依赖 Models/Persistence/Utilities（另有 Plugins，文档未列，v0.6.3 契约内置后属文档滞后）⚠️；Persistence 依赖 Utilities（另有 Models，合理）⚠️；**Utilities→Persistence 真实违规（P3）** ❌；壳式 DI 组合根 ✅；public 仅限契约 ✅。
- 前端 AGENTS.md 强约束（零构建/零 CDN、data-action 事件委托、CSS 变量、Notion 基线、响应式三档、弹窗/Toast 无障碍、轮询清理、密钥语义）逐条核对**高度一致**，仅 token-mask 内联 style 一处违规（P12）。

## 6. 工程治理亮点（评估顺带确认）

崩溃安全设计（标记先行 + 双保险 + 自愈重试）、日志续读语义、失败优先判定、原子防重入、Jint 沙箱边界、Windows 环境陷阱规避（0x800700E8/740/进程树排除游戏）、可测试性（NEXUS_TIME_SCALE/NEXUS_SYSTEM_ACTION_DRYRUN）、按尝试分批落盘、安全纵深（Origin+令牌+锁定+白名单）。

---

## 7. v0.7.6 自动更新配置专项评估（2026-08-16）

> 背景：v0.7.6 引入「自动更新配置」（AutoUpdateConfig，默认开）——运行产生的配置更改反向同步回用户快照 store（config → store 全量镜像），属**破坏性更新**（老用户升级后首次运行即开始回写 store，此前永不回写）。本节为专项评估：功能机制还原、可复现 BUG 清单、处置与修复。

### 7.1 功能机制还原（代码验证）

- **触发点 1（首次检测）**：运行开始 `ScaledSeconds(15)` 后主监控循环内一次性同步，`attempt.Number==1` 才执行，**开关关/开共有**（`src/Services/RunSession.cs:614-620`）。
- **触发点 2（收尾同步）**：finally 中、插队还原与配置交换还原**之前**，仅开关开时（`src/Services/RunSession.cs:290-300`）——config 此刻为脚本最终态；含 cancelled。
- **同步语义**（`ConfigSwapSession.SyncConfigToStore` → `MirrorToStore`）：`WithSwapLock` 内 copy-then-prune 全量镜像；插队文件（swap-backup/.meta 清单内）有还原描述（`config-restore.json`，array/map 两型、未覆盖键保持）先还原启停再写入、无描述跳过（store 保持原样）。
- **守护**：`.session` Phase=run 校验；config 缺失/为空/文件数骤降一半跳过；首次检测前置双采样稳定性检查（800ms 间隔）。

### 7.2 对齐结论（与用户确认，2026-08-16）

| 决策项 | 用户选择 | 影响 |
|---|---|---|
| 开关「关」的语义 | **维持现状**（关 = 仅首次检测同步，收尾不同步） | KN-80 文档化语义保留 |
| 失败/取消/超时收尾回写 | **照常回写但加强校验**（内容有效性探测 + 收尾同步稳定性检查） | 已实施（7.4） |
| 专项脚本恒开 | **合理**（前端不渲染开关、后端不强设） | KN-79 防御性建议待办 |
| 首次检测时机（15 秒 + 仅第 1 次尝试） | **符合设想** | KN-81 文档化保留 |
| 配置交换代价（运行期间脚本写入 config 的文件被删除） | **接受现状**，不加排除清单 | — |
| 评估产出 | **更新 ASSESSMENT.md** | 本节 |

### 7.3 可复现 BUG 清单与处置

| 编号 | 问题（可复现） | 位置 | 状态 |
|---|---|---|---|
| KN-77 | **单文件 config 半写入库（高，数据风险）**：脚本被取消/超时强杀瞬间正在写 config 时，半写 JSON 被收尾同步直接镜像进 store 永久污染快照（单文件形态无骤降检查兜底）；通用脚本（无判断脚本）无任何内容校验 | `ConfigSwapSession.cs:337-370` | ✅ 已修复（7.4） |
| KN-78 | **`ApplyToggle` 整树重写丢格式/BOM/编码**：ReadAllText 剥离 BOM + ToJsonString 重排 + 无 BOM 写入；GBK/UTF-16 源文件重写后编码变化 | `ConfigSwapSession.cs:646-659` | 随版 |
| KN-79 | **专项恒 true 仅前端保证**：curl/CLI 直连可构造专项脚本 `autoUpdateConfig=false` | `ApiScriptsHandler.cs:254-272` | 随版（防御性） |
| KN-80 | **「关」仍执行首次检测回写**（与直觉相悖，语义经用户确认保留） | `RunSession.cs:614-620` | 文档化 |
| KN-81 | **快速失败脚本（<15 秒）首次检测永不触发**（开关关时零同步；经用户确认可接受） | `RunSession.cs:614-620` | 文档化 |
| KN-82 | **首次检测同步阻塞主监控循环**（跨进程锁竞争最长 30 秒判定延迟） | `ConfigSwapSession.cs:299-331` | 随版 |
| KN-83 | **多轮替换后还原描述 index 漂移**（maaend `instances[下标]` 固化） | `plugins/maaend/data/judge.js:93-109` | 随版（低概率） |

### 7.4 本版已实施修复（随评估交付）

**KN-77 内容有效性守护**（`src/Services/ConfigSwapSession.cs`）：

1. **收尾同步同样执行稳定性检查**：`StableConfig` 从「仅首次检测」扩展为全部同步——短间隔两次采样不一致（外部守护进程仍在写）→ 跳过本次，保留旧快照。
2. **JSON 型内容有效性探测**（`ValidForSync` 内新增 `ContentValidForSync`/`JsonContentValid`）：`.json` 扩展名或内容以 `{`/`[` 开头的文件必须可解析，0 字节 `.json` = 半写坏态；非 JSON 文本不校验；单文件 32MB 上限跳过探测（防内存开销）；探测失败 → 跳过整个同步（宁可保留旧快照也不入库坏态）。

**测试**：单测 +6 方法/9 断言（坏 JSON 文件/目录跳过、混合合法通过、空 .json 跳过但空 txt 通过、无扩展名 JSON 内容校验）；judge-scenarios +1 用例/10 断言（半写 JSON 不入库 + config 还原、合法 JSON 照常入库 + config 还原）。

### 7.5 测试与断言数字（加速档，2026-08-16）

| 套件 | v0.7.6 | 本次后 |
|---|---|---|
| 单元测试 | 174 断言 | **183 断言**（+9） |
| judge-scenarios | 140 | **150**（+10） |
| e2e / chaos | 77 / 166 | 77 / 166（回归验证中） |

### 7.6 升级破坏性提示（用户须知）

老用户升级 v0.7.6 后首次运行即开始回写 store（默认开）——建议 CHANGELOG 升级说明显著标注行为变化；本次 KN-77 修复已大幅降低「失败/取消收尾把半写配置永久写入快照」的数据风险。

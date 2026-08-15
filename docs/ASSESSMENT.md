# NexusPipeline 全面评估报告（2026-08-14）

> 编制背景：v0.6.8 开工前对项目（LLM 全程开发）的一次完整评估，含核心业务/功能板块设计逻辑核对、潜在问题清单与后续开发方向。对齐结论已与用户确认。

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

## 3. 潜在问题清单（P1-P16）

> 全部纳入 v0.6.9 技术债版本（用户确认）。高优先级已在开发清单 v0.6.9 章节登记，此处为完整记录。
> **处置状态（2026-08-14 v0.6.9 已交付）**：P1-P15 全部完成（详见 CHANGELOG v0.6.9 与开发清单 v0.6.9 章节）；P16 钉钉/飞书签名真机验证延后（需真实机器人环境）。

### 高优先级
| # | 问题 | 位置 |
|---|---|---|
| P1 | `QueueTask.UserName` 死字段——队列任务级指定用户名被静默忽略（用户决策：移除） | src/Models/DispatchQueue.cs:22 |
| P2 | `RecoverIfNeeded`（cache 空→仅清标记）与 `TryRecoverItem`（cache 空→DoRestore）语义不一致，窄窗口下 store 快照固化到 config | src/Services/ConfigSwapSession.cs:290 / :366 |
| P3 | Utilities→Persistence 反向依赖（Logger→AppPaths），形成环 | src/Utilities/Logger.cs:2 |
| P4 | HistoryService.Cleanup 不加锁，与运行中 Save 并发可致历史丢失 | src/Services/HistoryService.cs:190 |
| P5 | Python 判断脚本 traceback 被静默吞掉，调试无线索 | src/Services/JudgeScriptRunner.cs:402 |
| P6 | replaceConfigs 在脚本进程仍运行时复制覆盖 config（占用/半写窗口） | src/Services/SessionJudge.cs:120 |
| P7 | exit 完成操作立即退出，队列收尾（FinishedAt/Unregister）来不及执行 | src/Services/DispatchCenter.cs:585 |

### 中优先级
| # | 问题 | 位置 |
|---|---|---|
| P8 | 日志截断重读产生重复行（轻微内容污染） | src/Services/LogMonitor.cs:138 |
| P9 | 定时时间 "8:00"（无前导零）静默不触发且无校验 | src/Services/Scheduler.cs:84 |
| P10 | 跨午夜服务日志文件名不滚动（static readonly 启动时求值） | src/Persistence/AppPaths.cs:29 |
| P11 | 轻量模式托盘「打开管理页面」→ 404 | src/TrayApp.cs:25 |
| P12 | token 输入层内联 style + 硬编码色值 + 无 dialog/焦点陷阱（违反自约束） | wwwroot/app.js:124 |
| P13 | 令牌比较非常量时间；/api/logs 孤儿 API；静态文件无缓存/安全头 | src/Web/WebServer.cs:298 |
| P14 | resolve.json 占位符仅整体替换（多占位符/文本混排不支持），README 未明示 | src/Plugins/DataSpecializedPlugin.cs:218 |
| P15 | 「审计行不过滤豁免」文档措辞与实现（随阈值过滤）矛盾 | AGENTS.md:60 vs src/Utilities/Logger.cs:53 |
| P16 | 钉钉/飞书签名真机验证缺失；Webhook 非 JSON 响应与网络失败混淆 | src/Services/WebhookSender.cs:85 |

## 4. 后续开发方向（与后续开发清单对应）

| 版本 | 主题 | 难度 | 建议 |
|---|---|---|---|
| v0.6.8 | 日志全量更新 + 拖拽排序 | ⭐⭐ | 按清单执行；毫秒时间戳先 grep 测试依赖；排序后端存顺序数组防 Index 漂移 |
| v0.6.9 | 技术债清理 P1-P16 | ⭐ | 高优先级 7 项必做，中优先级 4 项，低优先级视余力 |
| v0.6.10 | 长时脚本（-1 无限超时）+ 队列弹窗拖拽 + 任务卡片化 + 文档体系重组 | ⭐⭐⭐ | ✅ 已交付（2026-08-15）；模拟器部分剥离至 v0.7.0 |
| v0.7.0 | 安卓模拟器（adb） | ⭐⭐⭐ | 技术验证（MuMu 实测 adb 启停/前台检测/路径解析）先行，验证通过再立项；模拟器进程治理复用 KillTree 原语 |
| v0.7.0+ | 并行调度队列 | ⭐⭐⭐⭐ | 维持暂缓；先出资格矩阵设计文档 + README 明确队列间并行现状 |
| v0.8.0 | 自动签到通用插件 | ⭐⭐⭐ | 前置 API 调研；需新增队列外独立定时通道 |
| v0.8.0+ | 桌面分身 | ⭐⭐⭐⭐⭐ | 维持暂缓，三块 demo 评估先行 |

## 5. 架构符合性核查

- Models 无依赖 ✅；Services 依赖 Models/Persistence/Utilities（另有 Plugins，文档未列，v0.6.3 契约内置后属文档滞后）⚠️；Persistence 依赖 Utilities（另有 Models，合理）⚠️；**Utilities→Persistence 真实违规（P3）** ❌；壳式 DI 组合根 ✅；public 仅限契约 ✅。
- 前端 AGENTS.md 强约束（零构建/零 CDN、data-action 事件委托、CSS 变量、Notion 基线、响应式三档、弹窗/Toast 无障碍、轮询清理、密钥语义）逐条核对**高度一致**，仅 token-mask 内联 style 一处违规（P12）。

## 6. 工程治理亮点（评估顺带确认）

崩溃安全设计（标记先行 + 双保险 + 自愈重试）、日志续读语义、失败优先判定、原子防重入、Jint 沙箱边界、Windows 环境陷阱规避（0x800700E8/740/进程树排除游戏）、可测试性（NEXUS_TIME_SCALE/NEXUS_SYSTEM_ACTION_DRYRUN）、按尝试分批落盘、安全纵深（Origin+令牌+锁定+白名单）。

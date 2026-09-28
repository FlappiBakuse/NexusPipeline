# v0.16.9 语义债务与性能处置

本页只登记本轮已审查的候选；“未删除”表示仍有现役责任或缺少安全证据，不按文本搜索结果推断死代码。证据范围包括 Host 编译引用、测试、HTTP 路由、DI、持久化读取和 Plugins 生成源。

| 候选 | 分类与现役 owner | 引用/行为证据 | 本轮处置 |
|---|---|---|---|
| `ExecutionCoordinator.ExitGraceSecondsAfterMarker` | `confirmed_dead`，Host 执行 | 全仓语义查用只找到本声明；实际 60 秒标记退出限额由 `AttemptMonitorLoop` 使用 | 删除未使用声明，保留实际限额与测试 |
| `configValidator` runner、descriptor 和保存路径 | `confirmed_dead`，插件配置诊断 | 新版 `taskProtocol.discover.configAssessment` 已承担诊断；旧包显式拒绝，历史读取只读 | 删除运行器和调用，保留旧 manifest 拒绝及历史说明 |
| `TaskConfigView` 同一视图的重复解析 | `duplicate_work`，配置脚本视图 | 一份冻结字节可被多次 `ReadConfig`/选择器访问；文件变更仍由 `VerifyUnchanged` 字节复核 | 每视图惰性解析/序列化一次；20 次重复访问的计数断言为 1，保留提交前实时字节检查 |
| `RuntimeWorkers.QueueJudge` 忙时快照与 `TaskProtocolRun.Publish` 的无变化通知 | `duplicate_work`，运行判断 | 单飞忙时不准备普通无效快照；final 独立排队并消费，缺口及终态仍发布 | 已收敛；真实 Jint Observe 和 600 次忙时请求的 30 样本测量中，空发布 100→0、快照 601→1，final 始终保留一次 |
| `SystemActions.MinimizeWindow` 与同步最小化轮询 | `confirmed_dead`，平台窗口动作 | 内部门面及唯一同步实现没有 SDK、路由或动态消费者；启动辅助动作已使用异步身份轮询 | 删除同步门面与实现；目标退出/取消测试覆盖异步替代路径 |
| 配置事务、更新 journal、插件恢复分支 | `compatibility_live`，各现有事务 owner | 启动恢复和旧现场读取需要精确状态；不能按低调用量清除 | 保留；旧版格式读者与故障注入另行验收 |
| public HTTP/CLI/MCP 字段与 `nxp-*` 元件 | `public_or_dynamic_root`，控制面/前端 | 路由、动态组件和外部插件可消费，静态本仓 import 不完整 | 保留；只对新增字段做兼容扩展 |
| Plugins `tools/task-protocol/` 与生成后 `data/*.js` | `generated_asset`，Plugins 生成器 | 八款生产脚本由当前源模块生成，生成一致性门禁覆盖 24 个脚本 | 保留双层源码/产物关系，不手改产物 |
| 默认关闭的 March7th 源码桥与历史上游 probe | `active_experiment`，Plugins 实验工具 | 未进入正式包，不能视为游戏运行证据 | 保留隔离研究用途，不进入应用载荷或支持承诺 |
| `configAssessment` 收尾诊断的重复内部分支 | `confirmed_dead`，Plugins 配置评估 | 外层已处理全部系统动作后返回，内层同条件不可达；生成器和真实 Jint 的动作全集检验现役结果 | 删除不可达重复分支，保留一次明确的危险动作评估 |

删除审计覆盖两仓全部 tracked 与非忽略 untracked 文件、Python AST、TypeScript/Vue 语义引用和全部 24 个 managed 工程的 Roslyn 声明/边；SDK、路由、DI、序列化读者、资源、生成源和实验均按动态根保留。静态零边不直接取得删除资格；只有上表独立审查的 `confirmed_dead` 被移除。历史路径对账与全仓库存保存在交付证据中，不作为未来构建输入，也不表示全量清债已经完成。

同平台性能对账以实际原 HEAD 为基线，记录仅用于测量的插桩；每组保留 30 个样本、CPU、工作集、分配和原始计数。发现路径实际读取冻结资源并运行 Jint；50 资源冷读 p50 66.72→32.83 ms、热读 60.94→14.11 ms。每资源冷读构造/解析从 9→1、热读从 8→0，配置写入前仍逐字节实时复核。1 资源冷读的 p95 本轮为 7.92→17.02 ms，较早轮次也有同方向异常；原因未证明，不能宣称所有尾延迟改善。

Observe/忙时请求组合的 p50 分配从约 262.6 MB→13.1 MB、墙钟 115.0→48.7 ms。端到端取消在 3 次预热后测 30 次，请求 p95 731.0→2.12 ms、终态 1205.6→270.9 ms；终态轮询为 250 ms，内部阶段单独记录。100 MiB 日志每次只保留 4 MiB 检查点，追加读取 p50 4.56→4.58 ms；整体诊断进程 p50 107.0→115.1 ms，未宣称其改善。600 个空闲 tick 无状态保存；这只是 10 分钟调度语义复放，不是持续 10 分钟真实游戏。CPU 存在 15.625 ms 量化，工作集是采样而非操作分配。以上是限定工作负载的测量，不推出整体产品性能达标。

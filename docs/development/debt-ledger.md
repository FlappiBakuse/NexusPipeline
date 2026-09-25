# v0.16.9 语义债务与性能处置

本页只登记本轮已审查的候选；“未删除”表示仍有现役责任或缺少安全证据，不按文本搜索结果推断死代码。证据范围包括 Host 编译引用、测试、HTTP 路由、DI、持久化读取和 Plugins 生成源。

| 候选 | 分类与现役 owner | 引用/行为证据 | 本轮处置 |
|---|---|---|---|
| `ExecutionCoordinator.ExitGraceSecondsAfterMarker` | `confirmed_dead`，Host 执行 | 全仓语义查用只找到本声明；实际 60 秒标记退出限额由 `AttemptMonitorLoop` 使用 | 删除未使用声明，保留实际限额与测试 |
| `configValidator` runner、descriptor 和保存路径 | `confirmed_dead`，插件配置诊断 | 新版 `taskProtocol.discover.configAssessment` 已承担诊断；旧包显式拒绝，历史读取只读 | 删除运行器和调用，保留旧 manifest 拒绝及历史说明 |
| `TaskConfigView` 同一视图的重复解析 | `duplicate_work`，配置脚本视图 | 一份冻结字节可被多次 `ReadConfig`/选择器访问；文件变更仍由 `VerifyUnchanged` 字节复核 | 每视图惰性解析/序列化一次；20 次重复访问的计数断言为 1，保留提交前实时字节检查 |
| `RuntimeWorkers.QueueJudge` 忙时快照与 `TaskProtocolRun.Publish` 的无变化通知 | `needs_measurement`，运行判断 | 涉及 final 请求、cursor 与重连 revision；直接省略可能丢终态 | 本轮保留，待同输入计数和长日志基准后调整 |
| 配置事务、更新 journal、插件恢复分支 | `compatibility_live`，各现有事务 owner | 启动恢复和旧现场读取需要精确状态；不能按低调用量清除 | 保留；旧版格式读者与故障注入另行验收 |
| public HTTP/CLI/MCP 字段与 `nxp-*` 元件 | `public_or_dynamic_root`，控制面/前端 | 路由、动态组件和外部插件可消费，静态本仓 import 不完整 | 保留；只对新增字段做兼容扩展 |
| Plugins `tools/task-protocol/` 与生成后 `data/*.js` | `generated_asset`，Plugins 生成器 | 八款生产脚本由当前源模块生成，生成一致性门禁覆盖 24 个脚本 | 保留双层源码/产物关系，不手改产物 |
| 默认关闭的 March7th 源码桥与历史上游 probe | `active_experiment`，Plugins 实验工具 | 未进入正式包，不能视为游戏运行证据 | 保留隔离研究用途，不进入应用载荷或支持承诺 |

性能对账：当前稳定门禁证明同一不可变 `TaskConfigView` 在重复访问下从逐次解析降为一次，并在同长度改写后仍拒绝旧视图。取消 30 样本的请求/退出实测见本次隔离报告；文件读字节、CPU、工作集、长日志、1/10/50 绑定及无变化报告事件的同平台前后对照尚未完成，不据此声称整体性能达标。

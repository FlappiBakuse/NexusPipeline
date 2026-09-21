# 专项任务协议

宿主从 `0.16.8` 支持 data-specialized 的 `taskProtocol.version = "1.0"`。完整作者契约、生产适配器源文件、生成器与源码派生夹具由 [官方插件仓库](https://github.com/FlappiBakuse/NexusPipeline-Plugins) 的 `docs/TASK_PROTOCOL.md` 维护。

宿主责任按现役模块划分：Plugins 验证 manifest 和冻结脚本；Configuration 提供只读 JSON/YAML 视图、CAS 选择补丁与恢复 journal；Execution 按当前运行日志归并任务事实；History 保存不可变 TaskReport、最近结果索引及崩溃检查点；Notifications 统一格式化任务分类；Host 组合适配器协调用户查询。

## 用户入口

- 用户绑定中的任务预览只读取用户配置快照，配置被占用时返回 busy；实际运行在前置脚本结束后重新发现并冻结任务。
- 运行页显示任务树、当前尝试和已确认事实；实时事件只提示更新，完整快照支持断线恢复。
- 历史报告保存当时名称、任务范围、尝试与证据；不依赖插件继续安装。最新记录被删除或保留策略清理后保留 tombstone，不能回退旧成功记录。
- 用户徽章按当前启用专项绑定的最近结果汇总。最终实例异常/全部业务失败为红，部分明确失败为黄，未知为灰，全部满足为绿；技术准备不参与业务分母。运行中数量单独展示。
- 自动重试只能覆盖有明确失败/未执行证据且风险验证安全的任务和必要前置；不把原本关闭的业务任务打开。恢复冲突保留现场，不同步临时选择。

## 控制面

| 请求/事件 | 用途 |
|---|---|
| `GET /api/users/task-summaries` | 一次查询所有用户及绑定最近结果，不逐用户扫描历史 |
| `GET /api/users/{userId}/bindings/{scriptId}/task-plan` | 只读发现，返回 plan/stale/revision 或错误 |
| `GET /api/runs/{executionId}/tasks` | `{runId,revision,reports}` 完整运行任务快照 |
| SSE `task-report-changed` | `{runId,recordId,revision}` 更新提示 |
| `GET /api/history/detail?id={recordId}&metadata=true` | 通过稳定 ID 查元数据，不受页面日期过滤限制 |
| `#/history?recordId={recordId}` | 打开指定历史记录 |

协议错误、缺证据和日志缺口不会转为成功。成功次数配额仅计入实际有成功业务任务的成功运行；全正常跳过不消耗配额。恢复成功后聚合可以回到绿色，旧尝试仍保留失败证据。

## 维护和验证

低层回归位于 `tests/NexusPipeline.Tests/{Configuration,Execution,Plugins,History}`。生产适配器联调显式提供插件 checkout：

```text
dotnet run --project tools/NexusPipeline.TaskProtocolTests -- --plugin-root <Plugins>
node tests/run.mjs release all
```

联调工具不启动游戏或读取用户配置；使用真实 Jint、协议校验、结果归并和配置事务。正式 EXE 的管理员清单保持不变，系统功能验收使用 asInvoker Test Host。最终资格 H1–H5 仍全部执行。

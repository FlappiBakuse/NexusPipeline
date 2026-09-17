# 隔离与故障夹具

## 当前格式与恢复契约

- `.session`、`.session.bak`、`store-meta.json`、`edit-isolation`、`original-extra`、`swap-backup`、`store-txn` 和附加配置 manifest 只按当前 schema 读取；字段缺失、未知字段、未知目录或身份不明的现场保留并告警。
- 配置定位变化时，新位置缺失会阻断运行并保留旧快照；新位置存在时按当前配置重新建立 `store`，更新当前元数据，不跨定位复用旧快照内容。
- 配置恢复、快照事务、fail-closed、Windows 真实进程边界和第三方插件契约需要持续覆盖；删除的迁移链路不通过测试保留。
- 测试 runtime、日志、PID、服务停止信号和 Playwright 结果必须使用隔离目录。测试结束后确认进程退出，再清理本次产生的精确临时路径。
- 历史计算测试应覆盖状态筛选、负耗时归零、未结束运行不计入耗时、摘要状态计数和每日聚合；`durationMs` 只验证 Web/MCP 返回投影，不把它写入持久化 fixture。

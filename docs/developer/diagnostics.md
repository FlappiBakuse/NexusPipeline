# 诊断与支持材料

`GET /control/v1/doctor`、CLI `control doctor` 与设置诊断页使用同一 Runtime 用例及
`control.doctor.read` 权限。诊断按需读取，不参与日常状态轮询，不执行脚本、解释器、ADB 或网络探测。
状态区分 `pass`、`warn`、`fail`、`notRequired` 和 `notRun`；文件存在不能证明依赖可执行或设备可用。

| 检查 | 当前事实 owner 与结果 |
|---|---|
| `host.version` | 当前 Host 0.18.0、SDK 0.1.0；环境材料记录实际框架、OS 与架构 |
| `host.admin-integrity` | 当前 Windows token；普通权限标记关注，保留 Test Host 的 asInvoker 用途 |
| `host.install-write` | 当前实际载荷根；独占创建一个随机命名探针，写入、flush、删除，失败不声称可更新 |
| `web.listener` / `mcp.listener` | 已启动 Host 绑定的监听端口；MCP 未启用为 `notRequired` |
| `recovery.state` | 现役 RecoveryIndex 的准入、修订、隔离与持有计数，不导出资源名或原因原文 |
| `update.transaction` | 持久维护操作计数与自动更新循环健康；待处理或异常时关注 |
| `config.recovery` | 当前未完成配置编辑数；具体恢复仍由配置事务及 RecoveryIndex 负责 |
| `execution.state` | 当前执行消费者健康及待确认操作数 |
| `scheduler.state` | 调度循环健康、已定义队列及等待发生项数 |
| `plugin.runtime` | 安装身份及版本、期望启用与当前活动状态；未知运行状态为 `notRun` |
| `plugin.pending` | 未完成插件 apply journal 数；不绕过原插件恢复流程 |
| `dependency.python` | 有 Python 判定时核对现役 discovery 在 PATH 中找到的解释器文件；不执行解释器 |
| `dependency.adb` | 有 Android 目标时核对所选根内的工具及截图工具文件；不以全局 PATH 代替显式工具，不连接设备 |
| `logs.recent` | 最多 64 条类型化完成事实；无完成记录为 `notRequired` |

旧诊断的管理员、安装目录、Web/MCP、更新/配置恢复、执行/调度、插件/待处理操作、Python/ADB、日志及版本
分别映射到上表。未知 writer 与外部副作用仍由原 owner 保留、隔离和恢复，诊断不修复或重放。

支持包使用相同权限，`GET /control/v1/support` 返回最多 65536 字节的 ZIP；不在运行根持久保存导出。
六个文件为 `manifest.json`、`doctor.json`、`environment.json`、`plugins.json`、`runtime-state.json`、`safe-log.json`。
包内只包含固定白名单环境、插件身份/版本/活动布尔值、状态计数及类型化完成事件。
不读取或导出配置正文、凭据、Cookie、截图、数据库字节、worker 原文、私有原因、任务/账号名称或本机路径。
环境版本与插件文件存在检查不替代真实运行资格。

```text
NexusPipeline.exe control doctor --address http://127.0.0.1:58731
NexusPipeline.exe control support --address http://127.0.0.1:58731 --binary-stdout true
```

支持包的 stdout 是二进制；非交互 CLI 诊断仍写 stderr。调用者自行选择安全的外部输出路径。

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

## 启动与读写归因

重复 I/O 排查先绑定当前源码清单，分别记录文件、SQL、网络、流输出与内存操作。
候选须包含资源表达式、调用者、启动或周期触发链、循环及变化门禁；不同 SQL 和互斥分支保留各自语义。
静态未解析调用保持待审查，实际成本由独立的启动、更新、稳定闲时采样确定。
插件目录按活动载荷、签名 ZIP、旧事务工作树和业务数据归属分别统计；相同摘要不直接授权删除。

`startup.profile` 汇总数据库准入、恢复和逐插件激活；阶段墙钟以 `startup.stage` 为准，并发与嵌套子项不可相加。
`provider.prepare/inputs/runtime.capture/runtime.protect/hold` 区分输入、闭包及重新保护；
`managed.load/metadata/callback` 区分加载、PE 扫描及回调。`inventory.*`、`history.protect/sql-snapshot`、
`extension.binding/binding-sql`、`contract.evaluate`、`writer.executor/identity`、`recovery.*`、
`execution.reconcile`、`providers.restore` 指向实际 owner。连接打开计时未覆盖全部 SQL 成本。

`installation.sql.validate`、`activation.sql.validate`、`catalog.sql.validate`、`history.text.parse` 分别记录实际完整核验或解析。
浏览器 `nexus.frontend.<extensionId>.<stage>` 记录 resources、import、identity-before、activate、identity-after，
同阶段保留最近一次，失败/取消保留耗时；不记录账号、请求内容或秘密。
结合 session/current-page/plugins prepared、interface presented 和网络时间线定位首屏阻塞。

逐文件诊断从首次进程创建前开始，记录管理员令牌、动态后代创建身份、事件丢失和解析缺口。
保留进程句柄并核对内核创建时间；发现的子进程必须在当前父进程创建后出生，避免父 PID 复用引入外部进程。
ETW 以进程创建/结束事件、内核身份和生存区间归因，并与资源采样成员核对。System 对 runtime 的已解析回写单列。
设备事件丢失的窗口标记缺测，保留观察值及丢失数量；独立完整的 CPU、内存和进程逻辑计数按各自范围报告。
以原始历史 run/journal/资产路径匹配读写，避免只按目录总量归因；当前 SQLite 与旧历史正文分列。
进程逻辑计数、FileIO 请求和设备 I/O 分别保留，系统映像加载、IPC 与业务校验用实际调用栈解释。
SQL 记录真实语句及查询计划，区分索引查找、标量子查询和表扫描。观察器开销独立于普通复测。
八库按实际文件名归因。SQL 使用领域限定名，跨库语句同时列出参与库；提交、回滚与 busy 等待归于实例事务，
控制请求的 I/O 失败只记录异常类型及 owner 方法名，不记录请求正文、秘密或任意异常消息。
保留相关参与库和耗时，不将一个跨库事务重复计为多个独立保存。文件 I/O 区分各库、对应 `-journal`、super-journal、
物理 journal、blob 及进程管道。按需原生 SQL 观察与 ETW 文件采样分别输出逐库明细，普通运行不逐条写诊断日志。
完整数据库检查返回八库身份、契约和长度，备份回执返回全部文件及其摘要；业务 HTTP、CLI、MCP 和 SDK 协议沿用原入口。
剩余耗时需 CPU 热点证据，缓存开关微测或逻辑字节下降不代替端到端吞吐结果。

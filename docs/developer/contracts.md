# 新契约与 SDK

`contracts/schemas` 是自有 JSON 格式的权威源，`protocol-catalog.json` 登记格式与实施状态。
自有格式从整数版本 1 开始；Host 0.18.0、插件 0.5.0 与 SDK 0.1.0 各自独立。
没有 schema 或运行用例的目录项不能视为已经实现。

```text
python tooling/contract_codegen.py
python tooling/contract_codegen.py --check
python tooling/nexus.py check --scope contracts
```

生成器产生 C#/TS 消息形状及两种 SDK 的相同契约指纹。
`contracts/fingerprint.json` 摘要 schema、能力/API、UI ABI、手写公共 SDK 源码与生成器，
排除自身、生成文件和测试样例。公共方法变化也必须改变握手身份。
相同版本字符串不能替代实际源码、契约和二进制身份。

生成类型不替代运行时校验。输入先复制为独立、有界字节并严格解析，拒绝重复成员和超深结构，
再按嵌入 schema 验证；引用离线解析，UUID、日期和绝对 URI 使用明确格式检查。
固定 schema 的成功校验最多保留 256 个 schema 名称、长度及 SHA-256 键，不保留输入 JSON、DTO 或明文。
每次读取仍独立执行严格解析；该缓存不授权领域关系、SQL、物理输入、恢复或副作用。

## 扩展生命周期与权限

SDK 接收显式资源所有权，不暴露容器、Core、数据库或任意 Host 调用。
注册在激活成功后可见；停止先撤销贡献，再取消并逆序清理。
迟到资源在注册返回前释放，单个清理错误不会跳过其他资源，但会使整个停止操作失败。

manifest 的 `contractFingerprint` 绑定实际 SDK 契约字节，适用于 managed 与 specialized 包。
managed 激活在构造扩展前验证 manifest、扩展身份、SDK 版本/指纹和最低 Host 版本。
失败撤销 scope 的部分注册；浏览器握手也绑定当前指纹。
包签名、安装、回滚保护与生产激活由 Extensions owner 组合，进度见[当前范围](../status.md)。
受控 DLL 或脚本夹具不代替正式包信任，也不代表任意 managed 代码已获沙箱隔离。

## 定义与受控执行

`nexus.automation.bundle`/1 保存定义关系与相对根路径。
Automation 用例执行预览、完整引用验证及事务导入/导出，拒绝重复 ID、悬空关系、逃逸路径和旧格式。
附件准备和定义公开共用持久操作事实，秘密不随普通 bundle 导出。

`nexus.execution.plan`/1 描述冻结总计划，`nexus.execution.run-attempt-plan`/1 描述独立尝试。
总计划绑定实际定义、修订、输入映像/runtime 与物理根；尝试绑定独立配置计划、日志前态和输出目录。
worker 响应必须匹配 phase、operation、run、attempt 和冻结计划，schema 合法不能替代调用身份。
物理输入租约、持久重试与恢复边界见[状态存储](storage.md)和[执行](execution.md)。

`IExecutionRegistrationPort` 注册已经验证的执行包入口，控制帧与 stdout 分离。
bootstrap、首个 terminal、配置资源与恢复事实见[执行 provider](execution-providers.md)。
专项使用 `nexus.task.adapter`/1 传递任务与资源；配置资源携带 Host 提供的正整数 revision。
`discover`/`configuration` 只使用实际选择，不接受 run、attempt 或计划摘要；
`observe`/`retry` 只由实际运行步骤消费，绑定原总计划摘要及独立尝试，不从任意运行 UUID 取得调用权。
`nexus.config.patch`/1 的提案绑定原 operationId、资源 SHA 和 revision。
实际授权、CAS、journal 与配置提交属于 Configuration owner；observe/retry 不获得补丁提交权。
专项入口验证 scope、能力、脚本摘要、schema 和当前调用身份，拒绝自行提升重试安全级别。
Jint 引擎只接收 JSON，无 CLR、文件、网络或 Node 对象，并受语句、内存、递归、时间和取消预算限制。
实际专项业务重写与上游实测的完成范围独立维护，不从适配器夹具推断全部插件已完成。

## 秘密、调度与通知

`SecretDraft` 和 `SecretChange` 拥有短期明文内存，释放后拒绝读取，普通 JSON 序列化/反序列化被拒绝。
`IExtensionSecretPort` 向 Secrets owner 显式传递 keep/set/clear、operationId 和 expectedRevision。
每次调用检查激活、声明权限、主体、字段及读写权；失败与迟到结果均释放临时明文。
当前用户 DPAPI、独立密文文件、SQL 引用事务和未知密文保全见[秘密](secrets.md)。

`IScheduleRegistrationPort` 注册宿主调用的 work provider，scope 停止后取消并拒绝迟到结果。
调用绑定 occurrence、run、attempt、秘密主体、deadline 和选定通知任务。
宿主提供只读冻结计划入口，并以当前已发布计划修订执行；扩展不能维护第二套 occurrence、重试或持久幂等状态。
持久 occurrence、分钟调度和安全重试规则见[调度](scheduling.md)。

`INotificationSubmissionPort` 只接受冻结调用已选的任务 ID，沿用宿主分配的通知 operationId。
它不接收任意收件地址；队列回执表示接受，不能解释为投递成功。
已提交后收到取消仍返回原接受回执，未提交失败保留真实错误。
宿主持久化来源快照、任务顺序和 delivery 身份，显式记录 Queued/Sending/Sent/Failed/Unknown。
未知送达不会自动重发，通知失败不回滚原执行事实。
已保存任务的测试发送复用相同配置与秘密读取规则；普通响应不回显目标凭据。

## 媒体与控制面

`IMediaPreviewRegistrationPort` 注册媒体 provider，激活成功前不可见。
`IRunScreenshotPort` 每次调用重新检查运行授权；`nexus.media.capture`/`nexus.media.frame`
绑定 instance、run、attempt、预览和单次请求。
PNG 限 8 MiB，并核验摘要、尺寸、像素预算、完整数据流和实际格式；关闭预览或撤销权限后拒绝迟到帧。
执行截图的持久 source/root/collected 与媒体预览授权各有边界，不能互相授予设备控制权。

`/control/v1/capabilities` 提供公开能力；桌面会话与原生动作授权见[桌面原生授权](desktop-authorization.md)。
完整 Web/CLI/MCP 业务表面与媒体浏览器表面以[当前范围](../status.md)为准。
产品不提供旧格式 reader、旧 `/api` 路由或旧 SDK 别名。

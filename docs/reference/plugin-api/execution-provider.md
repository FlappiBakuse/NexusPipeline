# 执行 provider（Plugin API 1.9）

这是独立 managed 插件接入现有执行生命周期的公共扩展。接口位于 `src/NexusPipeline.Plugin.Abstractions/ExecutionProviderApi.cs`。旧 Plugin API 和 [taskProtocol](task-protocol.md) 的日志证据格式保持兼容；provider 不创建第二套用户、队列、历史或恢复引擎。

## 注册和计划

插件声明 `apiVersion: "1.9"`、`minHostVersion: "0.16.9"`，初始化时检查 `IPluginHostContextV1_9`，通过 `ExecutionProviders.Register` 注册唯一 provider ID，停止时释放注册。只有当前启用插件可以注册；停用后配置与历史保留，新的执行报告 provider 不可用。

`InspectAsync` 返回只读项目能力和公共 schema。`PrepareAsync` 返回冻结的 `PluginProviderPlan`：稳定 plan ID、配置 revision、授权指纹、资源、任务身份与顺序、私有执行数据。任务 ID 不等于翻译后的显示名称。Host 校验任务与资源预算、资源根和整份计划的 1 MiB 上限，随后使用原有准入和资源租约。

允许的资源为 `writable_root`（批准项目根及其子目录）、`desktop_input/current_session`、精确 `adb_endpoint`。项目 Agent 可能写共享目录时，必须声明相关可写根；独立配置不意味着上游程序具有文件沙箱。配置写入先取得 `TryAcquireConfiguration` 的既有编辑／运行／恢复门禁，随后 clone、校验、原子保存并发布。

## Host 与 worker

`RunAsync` 接收 execution、record、attempt、用户、脚本、冻结计划和 Host 控制的 worker port。Host 已负责游戏启动时，`LaunchTarget` 提供就绪后的强身份：Win32 为完整映像、PID、创建时间和可见 HWND；ADB 为精确 endpoint。provider 必须再次核验目标与授权配置匹配。

worker EXE 只能位于已安装插件根内，禁止链接、目录逃逸和任意系统程序替代。CWD 必须为批准项目根。Host 创建本次 Job，独立确认必要进程已经停止；插件的 `WorkerCleanupConfirmed` 不是足够证据。

IPC wire version 为 1。事件与控制使用独立 CurrentUserOnly 命名管道，核验连接客户端 PID；stdin 仅传 bootstrap，nonce 不放命令行。帧使用四字节 little-endian 长度、最多 1 MiB、深度 32 的 JSON，拒绝重复或未知 envelope 成员。每帧核验 execution/record/attempt/session、方向和连续 sequence。

worker 先 handshake，Host 发 start，然后 worker 发 ready；握手与原生就绪共享 30 秒启动预算。任务事件或 completed 早于 ready 被拒绝。ready 后允许 `task_event`、`progress`、`cancel_ack`、`completed`、`fault`。控制台 stdout/stderr 被有界资源生命周期管理，但不充当结构化证据。

取消沿独立控制通道先发停止请求；两秒宽限后按本轮 Job 与强身份停止必要 worker，确认写入静止后才恢复。恢复失败形成原有 journal 的持久隔离事实，阻断共享资源，独立后继仍按原顺序检查；人工整队取消停止全部后继。

## 结果与兼容

引擎 `succeeded` 只表示上游引擎完成。无独立业务证据时，任务仍为 unknown，运行显示“流程已结束 · 有未核验项”。历史新增 `outcomes`，分别保存 `engineStatus`、`businessVerification`、`executionOutcome`、`recoveryOutcome`；旧历史缺字段时不推断已核验成功。

provider 事实使用 `structuredEvidenceVersion: 1` 和受限 `structuredEvidence`，不伪造 stdout 行、source epoch 或日志位置。节点诊断不增加业务分母，也不把匿名 focus 当作任务终态。重试仍要求安全风险与依赖闭包证据；当前 Maa 首版不自动重跑 unknown 项。

## 验证入口

Host 单元测试覆盖 registry、计划、worker、既有 runner、资源和 journal。现役系统 runner 的 `node tests/run.mjs dev system maa` 使用显式 `NEXUS_OFFICIAL_PLUGINS_ROOT`，经官方仓库工具生成实际插件 ZIP、锁定 native、受控窗口／ADB／Agent，然后验证商店安装、绑定、队列和跨重启历史。它不连接真实账号或设备。

框架和项目的使用范围在 [官方 Plugins 仓库](https://github.com/FlappiBakuse/NexusPipeline-Plugins) 的 `docs/MAAFRAMEWORK_DRIVER.md` 维护；本公共协议不复制项目解析规则。该指南随驱动源码交付，商店正式上架状态以实际发行记录为准。

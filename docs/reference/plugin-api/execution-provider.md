# 执行 provider（Plugin API 2.3）

这是独立 managed 插件接入现有执行生命周期的公共扩展。接口位于 `src/NexusPipeline.Plugin.Abstractions/ExecutionProviderApi.cs`。当前 provider 使用 API 2.3 和独立 worker IPC；报告为 schema 1 / provider-execution-v1，[taskProtocol](task-protocol.md) 日常报告为 schema 2 / daily-flow-v1。provider 使用现有用户、队列、历史和恢复引擎。

## 注册和计划

插件声明 `apiVersion: "2.3"`、`minHostVersion: "0.17.6"`，初始化时检查 `IPluginHostContext`，通过 `ExecutionProviders.Register` 注册唯一 provider ID，停止时释放注册。只有当前启用插件可以注册；停用后配置与历史保留，新的执行报告 provider 不可用。

`InspectAsync` 返回只读项目能力和公共 schema。`PrepareAsync` 返回冻结的 `PluginProviderPlan`：稳定 plan ID、配置 revision、授权指纹、资源、任务身份与顺序、私有执行数据。任务 ID 不等于翻译后的显示名称。Host 校验任务与资源预算、资源根和整份计划的 1 MiB 上限，随后使用原有准入和资源租约。

允许的资源为 `writable_root`（批准项目根及其子目录）、`desktop_input/current_session`、精确 `adb_endpoint`。项目 Agent 可能写共享目录时，必须声明相关可写根；独立配置不意味着上游程序具有文件沙箱。配置写入先取得 `TryAcquireConfiguration` 的既有编辑／运行／恢复门禁，随后 clone、校验、原子保存并发布。

## Host 与 worker

支持保存初始化的 provider 实现 `IPluginProviderConfigurationLifecycle`，并将 `minHostVersion` 设为 `0.17.3` 或更高。只读 `DiscoverAsync` 与 `StageAsync` 不启动项目程序。Host 持久记录 operation、取得项目和设备资源租约，调用可选的 `IPluginProviderConfigurationPreparation.PrepareDependenciesAsync` 准备固定依赖，再执行 `InitializeAsync`。worker 必须停在首条正式任务提交前，发布包含 operationId、revision、fingerprint 与 `formalTasksSubmitted: 0` 的认证初始化事实。Host 独立确认 worker 终止和清理后，先调用 `CommitAsync` 写入不可变修订，再 CAS 发布脚本的 `executionProviderConfigRevision`；失败调用 `AbortAsync`，旧可见修订继续保留。

保存请求携带稳定 `operationId`；同一 ID 的相同请求返回既有操作，不重复启动 pretask，不同请求被拒绝。关闭页面或请求超时后通过查询恢复进度。取消仅请求停止，须等待 `cleanupConfirmed`。重启恢复检查已有可见指针和进程身份，不重放项目程序；未知 journal 保留原字节，显示需要处理并进入恢复隔离。`provider_configuration` 实时事件仅发布脱敏状态与有界进度码。

写入停止且资源租约释放后的终态回执保存在 journal 根目录的 `completed/`。它们不占用在途操作容量；状态列表保留最近 256 项，按 `operationId` 查询或重发请求仍校验完整历史回执及请求摘要。未确认清理的操作继续保留在活动目录并阻断对应资源。

候选可通过 `PluginProviderConfigurationLaunch` 声明一个已映射的 PC 启动目标：完整 EXE 路径、参数、1–1200 秒就绪预算和 EXE SHA-256。候选必须持有桌面输入资源，依赖准备不得改写此声明。Host 重新核验路径与文件摘要，复用现役游戏启动器的已运行检查与就绪等待，再将实际映像、PID、创建时间和窗口身份作为 `LaunchTarget` 交给初始化 worker。启动出的游戏目标沿用现役目标生命周期；初始化 worker、Agent、controller 和 tasker 必须完成清理。该声明不支持 shell、等待退出的前置脚本或模拟器启动。

实现 `IPluginProviderConfigurationSynchronization` 的 provider 可通过只读 `InspectSourceAsync` 报告 `current`、`changed` 或 `needs_attention`。Host 按物理根共享 watcher，合并变更并低频核验元数据。Maa 被动同步使用内部 SourceOnly 操作，只读编译并联合提交，不启动项目程序；普通手动保存仍执行初始化。来源歧义、已删除实例或物理根身份变化进入异常，不回退其他来源，也不重放失败操作。

编辑器 slot 的 `executionProviderConfigRevision` 指定当前已提交修订。插件通过 `nxp-provider-configured` 事件提供 `{ providerId, profileId, packageRoot, configuration }` 草稿；宿主“保存”将草稿交给后台初始化操作。该事件本身不提交配置。

用户绑定 slot 还提供 `userConfigRevision`，表示该绑定独立提交的修订。经用户确认后，插件可通过冒泡的 `nxp-provider-user-save` 事件提交 `{ configuration }`；Host 使用 slot 的脚本与用户身份启动同一初始化事务。请求中的 `userId` 与 `expectedUserRevision` 绑定现有用户，忽略客户端提供的脚本定义，重新读取当前脚本。初始化前检查用户绑定指纹，最终同时核对脚本与绑定，再由 Users owner 发布绑定修订。普通用户设置保存不接受修订指针替换。运行准备通过 `PluginProviderPrepareRequest.UserConfigRevision` 接收冻结的用户修订，原构造签名保持不变。后台同步先更新共享配置，再对绑定执行只读检查和重新套用覆盖；Maa 使用同一 SourceOnly 提交，其他 provider 保持初始化；失败保留旧指针，恢复不重放程序。

`RunAsync` 接收 execution、record、attempt、用户、脚本、冻结计划和 Host 控制的 worker port。Host 已负责游戏启动时，`LaunchTarget` 提供就绪后的强身份：Win32 为完整映像、PID、创建时间和可见 HWND；ADB 为精确 endpoint。provider 必须再次核验目标与授权配置匹配。

legacy-provider 可选 `PluginProviderPlan.EffectiveConfiguration` 提供本次冻结的有效配置 JSON。Host 在当前脚本／用户的运行工作区创建 `configuration.json`，把该路径作为通用判断输入的 `script.ConfigPath`，并通过 `PluginProviderRunContext.EffectiveConfigurationPath` 交给 provider。源构造签名保持兼容。provider 每次尝试重新读取、验证并编译该配置，不能从该文件接受新的安装根、设备、程序或资源授权。原始共享声明和不可变修订保持原字节。

通用 `replaceConfigs` 仅在本次 failed 结果、判断代次有效且 worker 独立清理确认后执行。它使用现役 scriptDir 相对源与配置替换备份，目标只允许该有效配置文件；下一次尝试读取替换值。最终恢复验证备份目标、文件摘要和会话所有权，再清理工作文件。取消或清理不明保留 journal 与备份，恢复阶段须先确认写入进程停止。未提供有效配置的旧 provider 继续使用原有合同。

worker EXE 只能位于已安装插件根内，禁止链接、目录逃逸和任意系统程序替代。CWD 必须为批准项目根。Host 创建本次 Job，独立确认必要进程已经停止；插件的 `WorkerCleanupConfirmed` 不是足够证据。

IPC wire version 为 1。事件与控制使用独立 CurrentUserOnly 命名管道，核验连接客户端 PID；stdin 仅传 bootstrap，nonce 不放命令行。帧使用四字节 little-endian 长度、最多 1 MiB、深度 32 的 JSON，拒绝重复或未知 envelope 成员。每帧核验 execution/record/attempt/session、方向和连续 sequence。

`PluginProviderWorkerRequest.UseSessionArtifacts` 可选请求宿主拥有的独占会话目录，由 bootstrap 的 `SessionArtifactsDirectory` 返回。worker 可以在此写本次日志；宿主确认本轮进程退出后按归属回执清理。未请求时该 bootstrap 字段不出现，既有构造签名与旧 worker 保持兼容。回执包含宿主与 worker 的 PID、映像路径和进程开始时间。会话目录只在本进程持有有效所有权时视为活动目录；重启后遗留目录、未知条目或无法读取的目录进入恢复隔离，阻止新执行、配置编辑和维护。宿主不会仅因 worker PID 已消失而删除目录，后代写入进程尚需独立确认。日志以 `log` 事件分帧，每帧 text 最多 2048 字符；宿主继续采集后续帧，判断输入沿用通用字符尾部截断。provider 日志来源诊断可在进度事件中提供相对路径、文件身份、起止偏移、轮转 epoch 与完整性状态；它们不证明业务成功。

worker 先 handshake，Host 发 start，然后 worker 发 ready；握手与原生就绪共享 30 秒启动预算。任务事件或 completed 早于 ready 被拒绝。ready 后允许 `task_event`、`progress`、`cancel_ack`、`completed`、`fault`。控制台 stdout/stderr 被有界资源生命周期管理，但不充当结构化证据。

取消沿独立控制通道先发停止请求；两秒宽限后按本轮 Job 与强身份停止必要 worker，确认写入静止后才恢复。恢复失败形成原有 journal 的持久隔离事实，阻断共享资源，独立后继仍按原顺序检查；人工整队取消停止全部后继。

## 结果与兼容

引擎 `succeeded` 只表示上游引擎完成。无独立业务证据时，任务仍为 unknown，运行显示“流程已结束 · 有未核验项”。历史新增 `outcomes`，分别保存 `engineStatus`、`businessVerification`、`executionOutcome`、`recoveryOutcome`；当前合同允许 outcomes 缺省或为空时不推断已核验成功；旧格式历史不参与解析。

provider 事实使用 `structuredEvidenceVersion: 1` 和受限 `structuredEvidence`，不伪造 stdout 行、source epoch 或日志位置。节点诊断不增加业务分母，也不把匿名 focus 当作任务终态。重试仍要求安全风险与依赖闭包证据；当前 Maa 首版不自动重跑 unknown 项。

## 验证入口

Host xUnit 覆盖 registry、计划、worker、既有 runner、资源和 journal。真实 Jint 的生产适配器联调显式提供插件 checkout，见[插件 API 索引](README.md)与[测试命令](../../testing/commands.md)。

框架和项目的使用范围在 [官方 Plugins 仓库](https://github.com/FlappiBakuse/NexusPipeline-Plugins) 的 [MaaFramework 指南](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/frameworks/maa-framework-driver.md) 维护；本公共协议不复制项目解析规则。该指南随驱动源码交付，商店正式上架状态以实际发行记录为准。

## 框架任务结果与有限续跑

`PluginProviderPlan.ResultBasis` 为可选属性，缺省 `legacy-provider` 保持既有判断行为；`framework-task-v1` 以认证原生任务终态为执行结果。既有构造签名保持不变。`PluginProviderRunContext.SelectedTaskIds` 为本次尝试的有序任务子集；缺省 null 表示完整计划。provider 必须校验身份、唯一性和原计划顺序，空集合不得启动 worker。

框架任务 running/terminal 事件携带本次真实 nativeTaskId，终态必须匹配先前身份。普通任务失败清理后发送 completed/failed、code=maa.task_failed 并以非零码退出；Host 独立确认 worker 清理后才允许重试。启动、协议、取消及清理错误不能作为普通任务失败。Host 在同一 run 的 reducer 保留成功任务和逐次证据，续跑首个失败任务与后续未成功条目，使用同一最大尝试次数和总预算。

历史的 resultBasis 和 attemptReports 区分框架结果、实际选中任务及业务核验。框架成功的 businessVerification 保持 unverified，不宣称游戏业务成功。Maa 隐藏并拒绝新增/修改通用判断，旧资产保留但不执行，也不通过 EffectiveConfiguration/replaceConfigs 改写本次计划。其他 provider 的旧历史与通用判断不变。

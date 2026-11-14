# 架构与业务所有权

本轮保留 next 的工程组织、严格新格式、typed ports 与代码风格。恢复的是用户可见语义；不移植 main 的运行时，也不为追求目录比例进行全仓搬迁。

## 从一次用户操作理解分层

页面或 CLI 收集用户意图，经 Host 控制边界验证身份、能力与输入；应用用例决定业务顺序；Core 规则解释可观察结果；Platform 执行数据库、文件、Windows 和网络效果。结果与未知状态按原操作身份返回，展示层不重新决定是否允许恢复或重试。

| 工程或层 | 负责 | 不负责 |
|---|---|---|
| Core | 领域规则、用例与 ports | SDK wire DTO、SQLite、UI、DI 容器 |
| Platform | Core ports 的 SQL、文件、进程、网络和扩展适配 | 为无关页面隐式决定业务策略 |
| Host | 组合根、控制入口、身份能力、生命周期 | 在每个 HTTP handler 重写领域规则 |
| Web / Desktop | 操作与展示、草稿、原生桥接的受限客户端 | 另建恢复权威或把 PID 存在当停止证明 |
| Maintenance / Installer | 退出后的受控切换、恢复、安装生命周期 | 运行一般业务或接管未知路径 |
| Extensibility | 插件公开协议与 SDK | 依赖 Host 私有页面或 Storage 具体实现 |
| GUI launcher（候选） | 固定 GUI 入口创建 Host | 判断数据库就绪、持有运行实例或接管任务进程族 |

Core 的现有标准库白名单与程序集约束继续生效；精确条目以工程和边界工具为准，不在入口页复制第二份白名单。

## 一个事实只有一个 owner

| 事实 | 业务 owner | 持久化或效果 |
|---|---|---|
| 脚本、账号、绑定、队列定义 | Automation | SQL 修订与只读投影 |
| 已受理运行、调用次数与预算 | Execution | 原运行/attempt/调用事实和受控进程 |
| 账号配置修订、活动输入与外部原现场 | Configuration | 不可变内容、配置会话、SQL 与恢复记录 |
| 日程 occurrence 与重复约束 | Scheduling | 分钟水位、受理事实 |
| 历史筛选、账号范围与展示证据 | Records | 已封存事实和可重建展示投影 |
| 插件安装与激活资格 | Extensions | 实际已验证包、scope 与能力 |
| 秘密、通知与外部发送状态 | 对应领域 | 专用秘密保护和副作用事实 |

派生 Map、缓存、索引和进度条可以加速或展示，但不能成为第二份业务真相。源身份、实际活动输出与长期发布修订是不同事实，不因都叫“配置”而混用。

## 本轮局部拆分边界

当前 Storage 内存在 Configuration 与 Execution 对具体实现的双向依赖。先在这条已证闭环上明确端口：配置提供准备、开始、采集、还原、完成；执行提供已验证 writer 身份与停止事实；应用编排协调顺序；持久化适配器承接 SQL/文件/journal。

保留同一事务、实例租约和恢复事实 owner。不要让新的应用层再维护一个“已停止”布尔值，也不要给所有 Storage 文件逐一套接口。只有抽出的边界拥有独立业务责任、实际消费者或可消除的具体依赖，才值得增加类型。

插件跨域 port 的组合应由组合边界承担；现有安全适配仍可留在 Platform。公共组件只能接收展示所需数据和具名动作，不能为了复用要求整个 AppStore 或导入上层页面的私有 view model。

## 继续保留的约束

未知格式保留原字节；恢复前确认实际 writer 已停止；无法证明拥有权时隔离资源；未知外部副作用不自动重放；秘密不进入普通 DTO 或日志。Web、CLI、MCP 经相同 use case 和权限判断，UI 修复不能绕过这些边界。

同一拥有期内可复用未变化输入，减少重复散列和查询；一旦跨越进程、释放所有权、撤销能力或面对可变外部输入，就必须重新判断证据是否仍有效。

## 实际边界与目标差异

现有 Roslyn 环路检查只针对 Core.Settings，不能据此声称全部 Storage 领域无环。本轮 Roslyn 规则禁止配置控制器消费执行的 writer state/codec，并禁止执行直接装配配置控制器与交换器。ConfigurationSessionFactory 由 Host 的 ExecutionRuntimeServices 组合；StepWriterScope 提供真实停止证明，配置控制器消费类型化事实。共同事务与恢复 owner 保持原位置。

证据：[现役工程组织](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/NexusPipeline.slnx)、[ConfigurationRunController](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/src/platform/Storage/Configuration/ConfigurationRunController.cs#L70-L87)、[AttemptRuntimeSession](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/src/platform/Storage/Execution/AttemptRuntimeSession.cs#L89-L110)、[实际环路检查](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/tooling/boundaries/SourceBoundaries.cs#L20-L65)。

其他精确参考：[存储](storage.md)、[流程归属](storage-owners.md)、[进程拥有权](process-ownership.md)、[控制面](control-http.md)、[SDK](sdk.md)。这些材料的旧阶段结论以本轮状态与实际结果为界，不构成默认全量验证要求。

## 插件业务边界

Host 消费注册的卡片贡献、manifest 元数据、静态图片和 emulator provider，不通过插件 ID 分配优先级或推导业务。概览布局持久化通用贡献键。前端准备协调器负责预算、取消和首次展示，不包含插件业务。

模拟器 provider 声明枚举参数、安装提示、配置扩展名、安装目录规则，以及选中实例的目录证据和启动/退出命令。Host 校验有界输入、根目录、工具物理身份、安装及授权代次，并将 provider 身份和命令封入执行证据；运行前再次要求相同 provider 解释当前源。旧选择缺少 provider 身份时仅接受唯一已发布 provider。MuMu 由宿主现役 Android 驱动实现。

预装范围由 Plugins 的构建声明决定；包回执传递 `bundledByDefault`，签名本地商店回执携带预装选择，产品装配据此复制已核验包。首次初始化从已核验的签名 catalog 与一次性选择清单安装，输入独立于应用文件归属，完成后清理；不维护固定插件名单。原生依赖准备由 Plugins 工具提供。

编译/AST 边界检查拒绝产品代码按固定 extensionId/providerId 分支。宿主测试保留通用授权、生命周期和显式消费真实插件包的集成场景。插件协议输出与厂商模拟行为位于 Plugins 的 `tests/host-fixtures`，由显式测试输入编译到所属 fixture；不进入 Host 产品。

| 内容 | 当前归属 | Host 消费方式 |
|---|---|---|
| 概览活动标题、说明、位置和布局键 | 活动插件前端贡献 | `SurfaceContribution.dashboard` 与通用贡献键校验 |
| 专项脚本图标、名称和说明 | 对应插件 manifest、资源和本地化元数据 | 授权静态图片接口、通用元数据列表 |
| 雷电、夜神、BlueStacks 的枚举、目录和命令 | emulator-support provider | SDK 描述与证据接口，Host 冻结权限及物理身份 |
| MuMu 驱动及其命令 | Host Android 能力 | 现役设备、媒体和执行端口 |
| Maa 原生输入、预装名单 | Plugins 构建声明与工具 | Host 装配消费正式包回执 |
| 专项协议输出与模拟器厂商 fixture | Plugins 测试源码 | `NexusPluginFixturesRoot` 显式编译输入及指纹 |

SDK 的模拟器 vendor 为插件声明的有界标识，Host 对选择使用小写标识和通用格式校验；厂商名称、稳定证据编码、安装发现、路径和命令规则由 provider 提供。标识在安装身份计算中保留原拼写，插件保持已保存目标 ID 的证据编码。旧配置未声明 provider 时，冻结证据单独保存唯一解析出的 owner，原配置字段保持不变。契约示例使用独立示例身份，宿主本地化资源不包含插件专属文案。

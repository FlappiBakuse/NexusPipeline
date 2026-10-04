# 专项任务协议

宿主从 `0.16.8` 支持 data-specialized 的 `taskProtocol.version = "0.1.0"`。完整作者契约、生产适配器源文件、生成器与源码派生夹具由 [官方插件仓库](https://github.com/FlappiBakuse/NexusPipeline-Plugins) 的 `docs/TASK_PROTOCOL.md` 维护。

宿主责任按现役模块划分：Plugins 验证 manifest 和冻结脚本；Configuration 提供只读 JSON/YAML 视图、CAS 选择补丁与恢复 journal；Execution 按当前运行日志归并任务事实；History 保存不可变 TaskReport、最近结果索引及崩溃检查点；Notifications 统一格式化任务分类；Host 组合适配器协调用户查询。

## 协议版本与日常语义

Host 0.16.14 增加 `0.2.0` 日常任务协议。它保留独立线版本 `0.2.0`，生成 `schemaVersion: 2`、`semanticsVersion: daily-flow-v1` 报告；不改变 `0.1.0/0.1.1` 或 managed 提供者语义。新包最低 Host 为 0.16.14，manifest 同样声明 `repairRules`（无修复时为空数组）。

每个新任务冻结 `completionPolicy`（flow/authoritative）、`workflowRole`（daily/technical/manual_only）、`observationContract`（ruleSetId/sources/rules）和 `retryPolicy`（mode/resourceConsumption/limitRefs）。观察必须给出与状态对应的 `factKind`，证据只能引用冻结来源和规则。插件不能提交 Host 证据。未结束、日志缺口和观察器错误由 Host 留存独立 `hostEvidence`，结果通过 `hostEvidenceRefs` 引用，不能伪造为上游日志。

flow 的正常结束不能抵消同范围未恢复错误；权威父任务成功且必需子任务失败归并为 partial，该父子范围禁止重试。顶层独立任务一成一败汇总为 failed。Host 取消只终结仍待定/运行中的任务；上游取消属于失败。恢复后的新执行序号可成为完成，正常展示不追加恢复提示。

新协议日常消费失败参与原有有限重试预算，`retryRisk` 不再作为 safe-only 门槛。`selective` 使用声明的 `selective_config` 策略，仍经 CAS/journal 和补丁后重新发现；`native_resume` 不写选择配置，复核冻结任务及行为签名后按原启用范围启动。重试报告区分 `targetTaskIds` 与 `launchScopeTaskIds`。原生续跑中的跳过不能清除前次失败事实。

报告2保存自身状态 `ownStatus`，用于崩溃恢复时重新计算父子状态；报告1仍按旧规则恢复。业务汇总与运行/配置恢复维度分开存储；正常跳过不并入实际完成计数，全部跳过不累计成功额度。

宿主支持 `0.1.0` 声明和 v0.16.9 新增的 `0.1.1` 声明。后者扩展受控修复及运行状态字段声明，最低 Host 为 `0.16.9`；三阶段输出线格式均为 `0.1.0`。旧开发版 `1.0`、`1.1`、`1.2` 声明会被拒绝，不回退旧 judge。

`taskProtocol` 要求 `localization: {defaultLocale, messages}`；messages 将 locale 映射到包内 `data/i18n/*.json` 平面字符串词典。最多 16 个语言、每个语言 4096 项，全部词典共 256 KiB；路径、重解析点、重复 key 和词条长度均校验。输出版本随声明冻结：0.1.x 使用 0.1.0，0.2.0 使用 0.2.0。

### 启动进程与输出声明

`resolve.json` 可声明 `outputEncoding` 为 `utf-8`、`windows-936` 或 `system-default`，在逐行切分前按该编码解码 stdout 和 stderr；缺省保持既有系统默认，未知值拒绝推导。

带 `taskProtocol` 的包可声明 `process: {rootRole: "game_launcher", writesManagedConfig: false}`。只有完整启动身份匹配且明确不写接管配置的纯 launcher 可以保留；`writesManagedConfig: true` 按必要配置 writer 处理，不能因 launcher 名称逃过停止和恢复屏障。缺声明仍为普通必要自动化进程；未知角色或缺少写入声明拒绝推导。

任务可附 `nameText`，观察、诊断、重试可附 `reasonText`。引用为 `{kind:"literal",value}` 或 `{kind:"plugin",key,args,fallback}`，不允许 owner；插件身份由 Host 从已解析实例赋予。key 最多 160 个 ASCII 字母、数字、点、横线或下划线；args 最多 16 个字符串/有限数值/布尔值，字符串最多 256 字符；literal/fallback 最多 2048 字符。占位符为 `{argument}`，缺失参数使用回退，不递归解释参数文本。不应把账号、日志或凭据传入参数。

启动时冻结词典；预览和报告的 `displaySnapshot` 保存 pluginId、pluginVersion、defaultLocale、localizationHash 与实际引用的 messages。报告动态原因从启动时词典取值，不读取当前安装目录。快照最多 256 KiB，超过预算仅舍弃翻译，仍保留任务和引用的 fallback。解析顺序为 literal、请求语言、同语言规范化回退、默认语言、fallback、原 name/代码。前端只渲染纯文本，通知另外消除换行。

名称和文本引用不参与行为签名与重试选择比较；稳定 id/sourceKey 才是身份。旧历史直接保留原名称和原因代码，不反查游戏名称或重算结果。

## 用户入口

- 用户绑定中的任务预览只读取用户配置快照，配置被占用时返回 busy；实际运行在前置脚本结束后重新发现并冻结任务。
- 运行页显示任务树、当前尝试和已确认事实；实时事件只提示更新，完整快照支持断线恢复。
- 历史报告保存当时名称、任务范围、尝试与证据；不依赖插件继续安装。最新记录被删除或保留策略清理后保留 tombstone，不能回退旧成功记录。
- 用户徽章按当前启用专项绑定的最近结果汇总。最终实例异常/全部业务失败为红，部分明确失败为黄，未知为灰，全部满足为绿；技术准备不参与业务分母。运行中数量单独展示。
- 自动重试按每个有明确失败/未执行证据的候选独立评估重试单元和必要前置；旧协议中不相关的 unknown 或危险失败不否决安全候选，0.1.x 共享单元须全部安全；0.2.0 日常单元按声明策略纳入消费失败。不把原本关闭的业务任务打开。恢复冲突保留现场，不同步临时选择。

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

低层回归位于 `tests/NexusPipeline.Tests/Execution` 与 `tests/NexusPipeline.Tests/Configuration`。生产适配器联调显式提供插件 checkout：

```text
dotnet build tools/NexusPipeline.TaskProtocolTests -p:NexusTestHost=true
dotnet bin/test-host/NexusPipeline.TaskProtocolTests/Debug/net8.0-windows/NexusPipeline.TaskProtocolTests.dll --plugin-root <Plugins>
node tests\run.mjs integration
```

联调工具不启动游戏或读取用户配置；使用真实 Jint、协议校验、结果归并和配置事务。正式 EXE 的管理员清单保持不变，系统功能验收使用 asInvoker Test Host。PR 的 smoke 门禁与合并后候选的集成验收均须通过。

账号配置矩阵入口：`dotnet bin/test-host/NexusPipeline.TaskProtocolTests/Debug/net8.0-windows/NexusPipeline.TaskProtocolTests.dll --plugin-root <Plugins> --account-isolation <报告.json>`。八个生产适配器分别使用同名、不同开关的 A/B 合成配置，经过真实配置交换、Jint 发现/重试、快照写回及持久化恢复；覆盖自动同步、禁止同步、选择性重试或安全停止、取消、进程清理未确认和重试中断。校验原现场和另一账号（含快照元数据）的字节不变、本账号完整配置语义与业务计数。失败现场保留供诊断；该探针不启动游戏，不替代进程租约、编辑器、真实账号登录或发行版资格验证。

生产适配器按 `discover`、`observe`、`retry` 独立构建。各入口拒绝错误的 `input.phase`；重试可以复用发现函数来校验当前配置，但发现和观察产物不包含重试执行器。联调验证三个入口的阶段拒绝和真实 Jint 权限边界。

观察可以携带独立的 `incidents`：包含问题 id、可空 taskId、scopeId、executionOrdinal、kind、resolution、reasonCode、可选 reasonText 和 evidence。首次为 open，只能在保持身份、原因和旧证据且增加证据后变为 recovered/terminal；重放幂等，冲突整批拒绝。0.1.x 的问题事件不改变任务状态；0.2.0 同范围未恢复业务错误阻止完成，并参与失败与重试归并。单批最多 2048 项，运行历史最多 4096 项且累计 256 KiB；报告冻结实际引用的原因词典和问题证据。卡片可展示问题恢复及无法归属的异常，业务计数仍只取归并器任务结果。

隔离日志桥的专用联调入口是 `dotnet build tools/NexusPipeline.TaskProtocolTests -p:NexusTestHost=true
dotnet bin/test-host/NexusPipeline.TaskProtocolTests/Debug/net8.0-windows/NexusPipeline.TaskProtocolTests.dll --plugin-root <Plugins> --bridge-replay <外部 replay.json>`。事件必须由插件仓库源码分支探针生成；该工具仅把显式测试流送入 TaskLogBuffer、受限 Jint 和归并器，不注册生产日志源，不安装到用户软件，也不表示官方发行支持已验证。

## 通知和离线历史

计划和运行 `diagnostics` 在任务面板的“计划说明”中展示。可选 `taskId` 标识所属任务，`reasonText` 从冻结词典解析；无 TextRef 的旧数据回退到 `message`。同一诊断去重，禁用任务的说明不混入启用任务列表，插件文本始终按纯文本呈现。说明不创建任务，也不改变业务计数。

任务通知固定先列成功、失败，再列部分失败、未知、未执行、取消、正常跳过。受影响子项使用父／子路径，避免同时列出聚合父项造成重复；业务完成数量读取 Host summary。异常按 attempt 与 incident 身份取最新恢复状态，未归属异常单独显示。

每类最多显示 12 项、360 个名称字符，每项最多 120 字符；长列表明确显示省略数量，附运行历史 ID。任务名、原因与证据均使用运行时冻结词典，卸载或替换插件后不重新读取插件资源。既有通知上下文、账号收件人覆盖和截图传递保持有效。

作者可用 `dotnet run --project tools/NexusPipeline.TaskProtocolTests -- --plugin-root <插件源码目录> --history-plugin <示例目录名>` 验证 JSON-list 示例的安装、真实 Jint 失败、历史持久化、词典替换、事务卸载及双语通知。该探针使用独立临时目录，不发送外部通知。

## 固定运行资源的完整性

`0.1.0` 的 `readResources` 可添加可选 `sha256`，值为 64 位小写十六进制，且资源必须为 `source: root`、`format: text`。宿主按捕获的原始字节比较，包含 BOM 和换行；`readResource` 增加 `integrity: verified|mismatch`。不匹配时 document 为 null，缺失/不可读仍按资源不可用处理。未声明哈希的资源返回形状不变；配置 revision 仍为不透明令牌，不返回内容摘要。既有路径白名单、单文件及总量预算不变。

哈希声明可固定只读研究资源，不能用旧版源码哈希代替当前兼容性判定。0.2 的 OK 两款以官方渠道、安装元数据、完整 HEAD、任务结构及当前进程归属建立真实日常计划；兼容的新版本仍调用 observe/retry，不建立未核验占位计划。Host 在接受观察前及结束时核对冻结的运行身份，更新器活动、路径冲突、活跃 worker 或不可读进程状态会阻断准入。持久 `running=true` 但已确认无 worker 时不单独拒绝兼容日常入口。

只读 JSON 资源可声明最多两个 `operationalFields`，类型限布尔与时间戳；运行期只允许这些字段按类型改变，其余冻结身份字段保持一致，发行身份及更新字段不能列入该声明。可写用户配置通过配置事务恢复。运行链归属变化会停止接受后续证据和自动重试，保留此前业务事实。旧 0.1 报告与受限运行历史仍按原有契约读取。

环境目标可声明 `source: {kind: "mainConfig", selector: [...]}`，仅在绑定捕获恰好一份主配置时解析，文件改名不改变归属；目录多配置或无配置时返回未检查，不从附加配置或其他账号回退。可选 `defaultValue`、`secondaryDefaultValue` 必须为有界字符串且对应单层属性 selector，只用于该属性不存在，显式 null、空值和错误类型不能被默认值掩盖。ADB 端口 selector 接受整数或字符串，地址组合仍只作格式/相等比较，不联网探测。默认值必须有锁定上游依据。

Host 联调工具的 `--runtime-installations <matrix.json>` 从显式 `cases`（id/artifact/root/expectedEvaluation）只读捕获官方安装资源，账号配置仍为合成夹具；`output` 指向不存在的报告路径。先传 `--plugin-root <插件检出>`；不能用合成资源集合代替安装来源证据。运行前用 `dotnet build <Host>/tools/NexusPipeline.TaskProtocolTests -m:1 -p:NexusTestHost=true` 构建，随后运行对应 Test Host 输出 DLL。旧 validator 对比实验结果保留在历史报告，不再提供执行入口。


保存诊断保留完整 `validation.diagnostics`；每项 `shouldNotify` 为 false 时，前端不重复弹出提示。Host 在当前进程内按绑定、规则范围、快照修订、上下文与词典身份去重；修复后再次出现或修订改变会重新提醒。缓存最多保留 1024 个绑定，重启或容量淘汰后可再次提示，不改历史事实。配置整体修订是进程密钥生成的 HMAC 标识，不是可枚举内容摘要；选择事务的单资源 CAS 令牌仍只属于其冻结视图。

保存脚本实例与完成配置编辑使用包声明的 `discover` 配置诊断；诊断失败不回退旧接口。声明 `configValidator` 的旧包被拒绝并提示升级或卸载，现有用户快照保持不变。一个绑定的反馈不会因另一个绑定文字相同而被去重。

### 配置修复

Host 0.16.14 的 `0.2.0` 修复声明覆盖八个专项。仅对当前用户已保存的主配置或附加配置快照操作；支持 JSON/YAML 单字段、路径绑定和有限的 MXU/MFA 任务列表修复。每次预览返回一项变更，明确应用后自动重读计划；仍有其他变更时继续显示下一项预览。开关默认关闭，插件脚本不能借此直接写现场配置。

`repairRules` 限定为最多 8 条，必须关联已声明的 `configRules`。`kind` 为 `replace_enum`（仅匹配列出的旧值）、`normalize_enum`（`fromValues` 是保留值，其他字符串改成 `toValue`）、`bind_game_path`、`mxu_tasks`、`mxu_preactions`、`mfa_tasks` 或 `enable_boolean`（缺失/false → true）。类型错误不猜测修复。资源仅允许 `config:$main`、快照内相对 `config:<文件>` 和 `extra:0`；`{instance}` 只接受已解析的稳定实例 ID。`skipWhen` 支持单字段布尔条件，用于云游戏等不适用范围。`preconditions` 限定快照类型、独占资源与附加配置条件。旧 `0.1.1` 保持 `config.yaml/after_finish` 的既有有限枚举修复。

路径修复只在 PC 模式且 Host 已配置绝对游戏路径时提出。保留启动参数、额度和其他配置。MXU 使用当前 `autoStartInstanceId`；MFA 只写选定实例，继承的共享任务在此实例建立覆盖，不改共享文件。mxu_preactions 仅接受当前实例的 preActions 数组，缺失时在已校验的实例父节点内建立字段；首项启用且不等待退出，保留参数和其余程序。mxu_tasks/mfa_tasks 仅关闭系统动作任务。MFA 启动修复分别写 SoftwarePath 与 BeforeTask，后者默认 None 可以明确修复；非字符串值不猜测。编辑准备以 NoAutoStart 屏蔽启动操作，进程退出后 config-edit-commit 清除临时标志再提交，正常执行不调用编辑脚本。未接管配置、未声明字段、格式错误和未知资源不提供修复。

令牌绑定账号、脚本、插件版本、冻结规则、Host 游戏路径/参数、配置元数据代次及主/附加快照全部字节。应用在配置租约内再次核验，并在 journal 提交前执行最终 CAS；失配不覆盖新内容。原始文件、元数据和摘要保留在用户的 `repair-backups`，主配置与附加配置各自复用现役存储事务。失败保留证据，恢复门禁不放宽。

预览的 `configuration_busy`、`cancelled`、`timeout`、`resource_limit` 与 `protocol_error` 分别表示占用/变化、取消、超时、资源预算和插件协议错误，不视作配置通过或业务失败。保存完成后的检查异常仅返回安全摘要，不返回原始脚本异常、文件路径或凭据。旧开发版协议声明会明确拒绝；`0.1.0` 的 `configAssessment` 必须完整且通过声明约束。

## 专项 PC 启动事实

`executionContext.gameTarget.ready` 是可选的宿主只读布尔值：仅 MaaEnd 与 MaaStellaSora 的 PC 运行前准入在 `LaunchGame=false` 时填入。`true` 表示按本次配置的游戏进程名找到了可见窗口；`false` 表示该时点未找到。预览、其他模式及旧宿主没有此字段，插件须按未知处理，不能把缺失当作 `false`。它不证明 MXU/MFA 控制器已成功连接，也不能替代启动后 stdout/超时监控。

`executionContext.queue` 在队列运行时还可提供 `nextTargetRelation`（`same` / `different` / `unknown`）和 `nextLaunchOwner`（`host` / `upstream` / `already_running` / `unknown`）。只有同一目标且下一项依赖已有窗口，才将“关闭游戏和软件”判为确定冲突；不明责任保持告警。跨队列静态预览不能把其中一个后继上下文写成永久配置错误。数据化插件可在 `resolve.json` 声明 `outputEncoding` 为 `utf-8`、`windows-936` 或 `system-default`，通用进程读取器按声明解码 stdout/stderr；缺省保持旧行为。

0.2.0 修复策略由当前冻结的 configEditor 以 config-repair 调用返回 selector/value；该调用仅提供 input、当前用户快照只读 readConfig 与 proposeRepair，不开放文件、进程或网络。Host 校验字段和 MXU 当前实例归属，绑定 editor 内容到令牌并使用现役 CAS、备份和 journal。BAAH 的附加软件快照可将缺失/false 的 SAVE_LOG_TO_FILE 修复为 true。0.1.1 的既有有限枚举处理保持兼容。

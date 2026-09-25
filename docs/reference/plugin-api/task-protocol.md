# 专项任务协议

宿主从 `0.16.8` 支持 data-specialized 的 `taskProtocol.version = "0.1.0"`。完整作者契约、生产适配器源文件、生成器与源码派生夹具由 [官方插件仓库](https://github.com/FlappiBakuse/NexusPipeline-Plugins) 的 `docs/TASK_PROTOCOL.md` 维护。

宿主责任按现役模块划分：Plugins 验证 manifest 和冻结脚本；Configuration 提供只读 JSON/YAML 视图、CAS 选择补丁与恢复 journal；Execution 按当前运行日志归并任务事实；History 保存不可变 TaskReport、最近结果索引及崩溃检查点；Notifications 统一格式化任务分类；Host 组合适配器协调用户查询。

## 首发协议文本引用

宿主仅识别首发专项协议 `0.1.0`；当前八个专项插件均声明该协议，最低 Host 为尚未发布的 0.16.8 开发构建。旧开发版 `1.0`、`1.1`、`1.2` 声明会被拒绝，不回退旧 judge。

`0.1.0` 在 `taskProtocol` 中要求 `localization: {defaultLocale, messages}`；messages 将 locale 映射到包内 `data/i18n/*.json` 平面字符串词典。最多 16 个语言、每个语言 4096 项，全部词典共 256 KiB；路径、重解析点、重复 key 和词条长度均校验。所有阶段的输出版本必须与 manifest 一致。

任务可附 `nameText`，观察、诊断、重试可附 `reasonText`。引用为 `{kind:"literal",value}` 或 `{kind:"plugin",key,args,fallback}`，不允许 owner；插件身份由 Host 从已解析实例赋予。key 最多 160 个 ASCII 字母、数字、点、横线或下划线；args 最多 16 个字符串/有限数值/布尔值，字符串最多 256 字符；literal/fallback 最多 2048 字符。占位符为 `{argument}`，缺失参数使用回退，不递归解释参数文本。不应把账号、日志或凭据传入参数。

启动时冻结词典；预览和报告的 `displaySnapshot` 保存 pluginId、pluginVersion、defaultLocale、localizationHash 与实际引用的 messages。报告动态原因从启动时词典取值，不读取当前安装目录。快照最多 256 KiB，超过预算仅舍弃翻译，仍保留任务和引用的 fallback。解析顺序为 literal、请求语言、同语言规范化回退、默认语言、fallback、原 name/代码。前端只渲染纯文本，通知另外消除换行。

名称和文本引用不参与行为签名与重试选择比较；稳定 id/sourceKey 才是身份。旧历史直接保留原名称和原因代码，不反查游戏名称或重算结果。

## 用户入口

- 用户绑定中的任务预览只读取用户配置快照，配置被占用时返回 busy；实际运行在前置脚本结束后重新发现并冻结任务。
- 运行页显示任务树、当前尝试和已确认事实；实时事件只提示更新，完整快照支持断线恢复。
- 历史报告保存当时名称、任务范围、尝试与证据；不依赖插件继续安装。最新记录被删除或保留策略清理后保留 tombstone，不能回退旧成功记录。
- 用户徽章按当前启用专项绑定的最近结果汇总。最终实例异常/全部业务失败为红，部分明确失败为黄，未知为灰，全部满足为绿；技术准备不参与业务分母。运行中数量单独展示。
- 自动重试按每个有明确失败/未执行证据的候选独立评估重试单元和必要前置；不相关的 unknown 或危险失败不否决安全候选，整个共享单元仍须全部安全。不把原本关闭的业务任务打开。恢复冲突保留现场，不同步临时选择。

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

联调工具不启动游戏或读取用户配置；使用真实 Jint、协议校验、结果归并和配置事务。正式 EXE 的管理员清单保持不变，系统功能验收使用 asInvoker Test Host。PR 快速检查与合并后候选的集成验收均须通过。

账号配置矩阵入口：`dotnet run --project tools/NexusPipeline.TaskProtocolTests -p:NexusTestHost=true -- --plugin-root <Plugins> --account-isolation <报告.json>`。八个生产适配器分别使用同名、不同开关的 A/B 合成配置，经过真实配置交换、Jint 发现/重试、快照写回及持久化恢复；覆盖自动同步、禁止同步、选择性重试或安全停止、取消、进程清理未确认和重试中断。校验原现场和另一账号（含快照元数据）的字节不变、本账号完整配置语义与业务计数。失败现场保留供诊断；该探针不启动游戏，不替代进程租约、编辑器、真实账号登录或发行版资格验证。

生产适配器按 `discover`、`observe`、`retry` 独立构建。各入口拒绝错误的 `input.phase`；重试可以复用发现函数来校验当前配置，但发现和观察产物不包含重试执行器。联调验证三个入口的阶段拒绝和真实 Jint 权限边界。

观察可以携带独立的 `incidents`：包含问题 id、可空 taskId、scopeId、executionOrdinal、kind、resolution、reasonCode、可选 reasonText 和 evidence。首次为 open，只能在保持身份、原因和旧证据且增加证据后变为 recovered/terminal；重放幂等，冲突整批拒绝。问题本身不改变任务状态或重试范围。单批最多 2048 项，运行历史最多 4096 项且累计 256 KiB；报告冻结实际引用的原因词典和问题证据。卡片可展示问题恢复及无法归属的异常，业务计数仍只取归并器任务结果。

隔离日志桥的专用联调入口是 `dotnet run --project tools/NexusPipeline.TaskProtocolTests -- --plugin-root <Plugins> --bridge-replay <外部 replay.json>`。事件必须由插件仓库源码分支探针生成；该工具仅把显式测试流送入 TaskLogBuffer、受限 Jint 和归并器，不注册生产日志源，不安装到用户软件，也不表示官方发行支持已验证。

## 通知和离线历史

计划和运行 `diagnostics` 在任务面板的“计划说明”中展示。可选 `taskId` 标识所属任务，`reasonText` 从冻结词典解析；无 TextRef 的旧数据回退到 `message`。同一诊断去重，禁用任务的说明不混入启用任务列表，插件文本始终按纯文本呈现。说明不创建任务，也不改变业务计数。

任务通知固定先列成功、失败，再列部分失败、未知、未执行、取消、正常跳过。受影响子项使用父／子路径，避免同时列出聚合父项造成重复；业务完成数量读取 Host summary。异常按 attempt 与 incident 身份取最新恢复状态，未归属异常单独显示。

每类最多显示 12 项、360 个名称字符，每项最多 120 字符；长列表明确显示省略数量，附运行历史 ID。任务名、原因与证据均使用运行时冻结词典，卸载或替换插件后不重新读取插件资源。既有通知上下文、账号收件人覆盖和截图传递保持有效。

作者可用 `dotnet run --project tools/NexusPipeline.TaskProtocolTests -- --plugin-root <插件源码目录> --history-plugin <示例目录名>` 验证 JSON-list 示例的安装、真实 Jint 失败、历史持久化、词典替换、事务卸载及双语通知。该探针使用独立临时目录，不发送外部通知。

## 固定运行资源的完整性

`0.1.0` 的 `readResources` 可添加可选 `sha256`，值为 64 位小写十六进制，且资源必须为 `source: root`、`format: text`。宿主按捕获的原始字节比较，包含 BOM 和换行；`readResource` 增加 `integrity: verified|mismatch`。不匹配时 document 为 null，缺失/不可读仍按资源不可用处理。未声明哈希的资源返回形状不变；配置 revision 仍为不透明令牌，不返回内容摘要。既有路径白名单、单文件及总量预算不变。

此声明用于已审查发行的高级判定。发现时由插件 critical 规则决定阻断；运行阶段 Host 在接受观察前及结束时复核已固定资源，变化或不可读会停止接受后续证据及自动重试，记录运行链异常，保留此前任务事实。OK 两款较新的同主版本只有在对应渠道的官方来源、安装元数据、空闲更新器、完整 HEAD 和基础配置形态可确认时才建立受限计划。受限运行只保留基础生命周期，业务任务始终未核验；Host 不调用旧版观察器或选择重试。运行阶段逐字节复核运行资源；`runtime-app` 仅允许上游写入布尔 `running` 和 `last_start`，版本、渠道、更新状态等字段仍需一致。可写用户配置由现有事务恢复，不作为发行身份字节。资源变化或不可读仍会拒绝后续证据。受限路径不证明全部 Python 依赖、解释器或瞬时替换安全，也不会阻止上游启动器修复/更新；新发行的高级判定仍须重新审查。

环境目标可声明 `source: {kind: "mainConfig", selector: [...]}`，仅在绑定捕获恰好一份主配置时解析，文件改名不改变归属；目录多配置或无配置时返回未检查，不从附加配置或其他账号回退。可选 `defaultValue`、`secondaryDefaultValue` 必须为有界字符串且对应单层属性 selector，只用于该属性不存在，显式 null、空值和错误类型不能被默认值掩盖。ADB 端口 selector 接受整数或字符串，地址组合仍只作格式/相等比较，不联网探测。默认值必须有锁定上游依据。

Host 联调工具的 `--runtime-installations <matrix.json>` 从显式 `cases`（id/artifact/root/expectedEvaluation）只读捕获官方安装资源，账号配置仍为合成夹具；`output` 指向不存在的报告路径。先传 `--plugin-root <插件检出>`；不能用合成资源集合代替安装来源证据。运行前用 `dotnet build <Host>/tools/NexusPipeline.TaskProtocolTests -m:1 -p:NexusTestHost=true` 构建，随后运行对应 Test Host 输出 DLL。旧 validator 对比实验结果保留在历史报告，不再提供执行入口。


保存诊断保留完整 `validation.diagnostics`；每项 `shouldNotify` 为 false 时，前端不重复弹出提示。Host 在当前进程内按绑定、规则范围、快照修订、上下文与词典身份去重；修复后再次出现或修订改变会重新提醒。缓存最多保留 1024 个绑定，重启或容量淘汰后可再次提示，不改历史事实。配置整体修订是进程密钥生成的 HMAC 标识，不是可枚举内容摘要；选择事务的单资源 CAS 令牌仍只属于其冻结视图。

保存脚本实例与完成配置编辑都使用 `0.1.0` 协议的 `discover` 配置诊断；诊断失败不回退旧接口。声明 `configValidator` 的旧包被拒绝并提示升级或卸载，现有用户快照保持不变。一个绑定的反馈不会因另一个绑定文字相同而被去重。

v0.16.9 的本地 Web 可在设置页逐次开启“允许逐次配置修复”。当前自动修复仅覆盖 March7thAssistant 插件 0.3.0 的用户级单文件快照中已知危险 `after_finish` 系统动作，将其建议为 `None`。预览只返回插件、用户绑定、字段、白名单原值、建议值、原因、影响及进程内预览令牌；点击应用才重新核对开关、插件/profile、快照元数据和字节修订，借现役配置快照事务提交。共享 extra config、多文件快照、未知值、旧/受限版本均须手动编辑；本功能不修改未接管的现场配置、不对其他诊断自动推断修复。事务出错保留恢复现场。内部受控选择补丁和常规配置编辑不受该开关影响。

预览的 `configuration_busy`、`cancelled`、`timeout`、`resource_limit` 与 `protocol_error` 分别表示占用/变化、取消、超时、资源预算和插件协议错误，不视作配置通过或业务失败。保存完成后的检查异常仅返回安全摘要，不返回原始脚本异常、文件路径或凭据。旧开发版协议声明会明确拒绝；`0.1.0` 的 `configAssessment` 必须完整且通过声明约束。

## MXU PC 启动事实

`executionContext.gameTarget.ready` 是可选的宿主只读布尔值：仅 MaaEnd 与 MaaStellaSora 的 PC 运行前准入在 `LaunchGame=false` 时填入。`true` 表示按本次配置的游戏进程名找到了可见窗口；`false` 表示该时点未找到。预览、其他模式及旧宿主没有此字段，插件须按未知处理，不能把缺失当作 `false`。它不证明 MXU 控制器已成功连接，也不能替代启动后 stdout/超时监控。

# 测试命令

## 默认命令

配置破坏性修订诊断：在已登记的外部测试根中设置 `NEXUS_TEST_MODE=test-host`、`NEXUS_TEST_HOST_DIR` 为现役 runner 构建的 asInvoker Host、新的 `NEXUS_TEST_RUN_ID` 和独立 `NEXUS_FINITE_RESULT`，执行 `node tests/system/config-revision.mjs`。它通过现役 runtime-helper 验证旧格式快照保全、实际 HTTP 候选信封、脚本设置确认及重启保全；使用合成插件与账号，不执行原生自动化程序。该诊断不替代核心 smoke 或完整 daily。

以下命令均在仓库根目录执行：

```text
node tests/run.mjs list --json
node tests/run.mjs ci --group backend
node tests/run.mjs ci --group frontend
node tests/run.mjs smoke
node tests/run.mjs daily
node tests/run.mjs daily --group schedule
node tests/run.mjs diagnostic --group store
node tests/run.mjs integration
node tests/run.mjs release
node tests/run.mjs plan --base <完整基线SHA> --include-working-tree --partner-root <固定Plugins检出> --output <外部scope.json>
node tests/run.mjs batch --plan <外部scope.json> --batch <control或batch-01至batch-05> --partner-root <同一Plugins检出>
node tests/run.mjs gate --id host.backend.updates-restart
node tests/run.mjs gate --id host.ci-policy
```

`ci` 必须指定一个已知分组。backend 选择真实后端规则、文件事务、执行与调度窄测试；frontend 运行类型检查及核心状态、用户配置请求与公开桥接测试。`tests/policy.json` 中的预期用例与实际原生报告严格匹配。新增或更名选中用例时同步清单，不能删除失败用例以绕过验证。

正式 PR 门禁由 `plan` 把逻辑 gate 义务转换为执行单元，再分配到可选 control 和最多五个 batch；容量不足报告 CAPACITY_EXCEEDED，不缩减义务。`gate --id` 保留为显式诊断。batch 从首次受控步骤继承父截止，工作窗口 130 秒、硬截止 180 秒，最后 50 秒用于清理及证据收尾；准备与子命令不重置预算。本地过程加准备耗时上限 150 秒，只证明本地预算。`smoke` 与两条 `ci` 是本地聚合诊断入口，使用同一核心执行器并共享 180 秒父预算，最后 10 秒预留收尾。输入识别、隔离复制、必要增量构建、依赖准备、测试、报告和清理均计入预算。失败保留原退出码；超时返回 5，清理失败返回 6，主动取消返回 130。报告不完整返回 4。未知命令或参数返回 2。

`daily` 使用两个独立 Host 槽位，先启动真实分钟调度，另一槽运行执行和配置，空闲槽接续控制面；共享准备、四组工作及清理合计 180 秒。支持 `--group execution|config|control|schedule` 进行明确的单组诊断。每组保留 `evidence.json`，父级 `daily-evidence.json` 只有全部预期组及清理通过才成功。

批次中的 execution、config、control 按实际选中集合共享 Test Host 构建和两个运行槽位，每组使用独立进程、端口、数据和运行身份。分钟调度单独分配；不需要浏览器的批次只准备 API Test Host。必需汇总 逐份核对 `finite-H-E01.json`、`finite-H-E02.json`、`finite-H-E03.json` 中对应选中场景的原生结果与清理证据。

| 场景 | 真实证明 | 外部替代 |
|---|---|---|
| H-E01 execution | 浏览器选择队列并运行，6 脚本、3 队列、4 账号，12 次请求、4 次取消、64 次读取，进程退出和历史归属 | 拥有的外部目标程序 |
| H-E02 config | A/B 保存、取消、正常/失败执行，目录配置还原，落盘 journal 后异常终止与两次启动恢复 | 合成配置与拥有的目标进程 |
| H-E03 control | 真实 CLI、MCP HTTP 初始化与运行观察、合法包安装更新、坏 hash 阻断、两次重启交接 | 官方 HTTPS 响应和合成测试插件 |
| H-E04 schedule | 真实分钟计时产生队列执行、结果归属、持久化水位与重启去重 | 拥有的外部目标程序 |

`integration` 保留已有 UI Smoke/System Smoke 诊断，部分 UI 响应由夹具提供；它不是 `daily` 的替代。`release` 在外部隔离源副本构建生产 requireAdministrator EXE，输出到 `runs/<runId>/production/`，不写项目 release 目录。更新事务诊断同时验证独立 worker、真实新实例启动收据及用户文件保全。

`diagnostic --group store` 在同一180秒父预算内构建实际 Test Host 和合成 managed fixture，访问官方 HTTPS catalog/package 地址，由 Test Host 的传输夹具返回固定响应。它验证坏 hash 拒绝、安装事务、新安装后自动启用、重复重启保持启用、显式禁用、插件加载和卸载。商店与更新重启的 System gate 不访问浏览器资源，因此 Test Host 只准备空 `wwwroot`；前端资产由独立的生产构建和 UI gate 验证。原生 TAP 与 `store-evidence.json` 位于本次运行目录；这是 H-C09 的安装生命周期诊断，不代表浏览器交互、全部商店恢复矩阵或四组 daily 已完成。

受控 HTTP 实现 `tests/host/TestHostTransport.cs` 仅在 `NexusTestHost=true` 时编入宿主，生产程序不读取 `NEXUS_TEST_HTTP_PLAN`。测试计划必须是绝对路径且 `runId` 匹配 `NEXUS_TEST_RUN_ID`；每项按 HTTP 方法、完整 URI 及可选凭据头/请求体 hash 匹配，未知请求失败，不回退到真实外网。收据只包含场景标识和匹配结果，并在重启后继续消费剩余响应；不会记录凭据、URL或请求体。回环控制面保持真实网络通信。

## 隔离与运行输出

测试不提权或降权，使用当前终端权限。可设置 `NEXUS_TEST_ARTIFACT_ROOT` 为本次专用的绝对路径；目录内 `.nxp-test-artifact-root.json` 内容如下，`directory` 必须填写该目录的实际绝对路径：

```json
{"schemaVersion":1,"owner":"NexusPipeline.Tests","directory":"<absolute test root>"}
```

未显式设置时使用 runner 临时目录或系统临时目录下的 `NexusPipeline.Tests`，已有无归属目录、链接和非法 run ID 均拒绝执行。本地有目录管理要求时先设置进程级 `TEMP`、`TMP` 或上述显式根；不要改变全局环境或登记已有用户目录。

原字节隔离副本位于 `cache/<source-and-toolchain-fingerprint>/Host`。构建使用真实产品源和依赖，测试不修改产品工程。相同输入的缓存复用需匹配源文件和工具链；独占 lease 防止两个命令同时写同一构建图，未完成清理的 lease 保留供检查。后端与前端共享只读源副本，各自使用不同构建输出。

报告位于 `runs/<runId>/<backend|frontend>/`：`commands.log`、原生 `native.trx` 或 `native.json`、`native-counts.json` 和 `summary.json`。只有预期场景/用例、原生计数、源码/输入身份、预算和清理全部一致才给 PASS；报告明确实际被测实现、外部替代及未证明范围。该 PASS 只覆盖所选组，不宣称 CLI、MCP、商店下载、真实调度和 native 设备已通过。

## 质量门禁顺序

`.github/workflows/ci.yml` 先生成含 source/partner/policy/control manifest 的完整 base/head 范围计划，再执行可选 control 与最多五个 Windows batch。`Host / 必需汇总` 核验本次计划、报告、原生计数、清理与 Actions API 返回的完整前序 job 时长；每个 job 硬限制三分钟。完成后还须执行只读 `python tests/audit-jobs.py --run-id <ID> --attempt <N>`，检查包括 必需汇总 在内的完整 job 时长。可信 `main` 的 begin 控制器先登记同 PR/head/producer run/attempt 身份并清除旧成功，finalize 在 CI 完成后审计所有物理 job，写入 `Host / 完整预算` 检查；该检查须与 `Host / 必需汇总` 一同绑定到 main 规则。回写检查不存在或失败时不得合并。

工具自测与文档检查按改动显式运行，例如：

```text
node --test tests/support/budget.test.mjs
node tools/check-doc-links.mjs
python -m unittest discover -s tools/tests -p test_pe_manifest.py
```

`tools/host_release.py` 继续负责生产构建、清单、打包、来源与发行完整性检查；不改变发行程序管理员清单或稳定包事实。

## 插件与任务协议联调

生产专项适配器的真实 Jint 联调：`dotnet run --project tools/NexusPipeline.TaskProtocolTests -- --plugin-root <Plugins>`。此命令使用显式插件 checkout 和合成夹具；插件仓库的 `python tools/repository.py verify --scope all` 运行相同入口。

跨账号配置与恢复：`dotnet run --project tools/NexusPipeline.TaskProtocolTests -p:NexusTestHost=true -- --plugin-root <Plugins> --account-isolation <报告.json>`。八个生产适配器、两个账号、六种生命周期情景，共 96 项；使用新建目录和合成配置，不启动目标软件。失败时输出并保留该情景的隔离目录。

修改 `frontend/src/plugin-bridge/**`、`frontend/src/platform/appearance.ts`、公开 `nxp-*` 元素或 Frontend API 契约时，在插件仓库执行 `NexusPipeline-Plugins/tests/frontend/run.mjs`，用 mock host 验证插件业务与生命周期；两仓库各自固定一次对端官方完整 SHA。

## 外部实例日志只读回放

`dotnet run --project tools/NexusPipeline.TaskProtocolTests -- --plugin-root <Plugins> --replay-manifest <外部清单.json>` 使用真实 Jint/发现/归并器读取显式声明的实例文件，不把旁边的历史 JSON 当成成功证据，不启动游戏或修改配置。清单和报告必须保留在仓库外。

清单字段：`report` 为外部报告路径；`scenarios` 每项包含 `artifact`、`resources`（每项 `id`、`path`、`format`）和 `logs`（每项 `path`、`source`）。资源 ID/日志来源须按插件实际启动契约映射：主配置为 `config:` 加相对文件名；额外资源使用 manifest 中声明的 ID；MXU 的任务回调来自 stdout，其他插件通常来自 file。报告按日志 SHA256 关联，记录发现覆盖、任务结果与运行边界。当前配置与历史配置可能不同，回放结果不能替代当时冻结的计划或真机运行验收。

## 逐插件 Jint 选择

`tools/NexusPipeline.TaskProtocolTests` 支持 `--plugin-root <Plugins> --plugin <已知专项> --report <新报告路径>`，可附加 `--scenario <fixture文件名去掉.json>`。未知插件、重复参数、空选择和已有报告路径均失败；不通过删除生产数据选择场景。

使用 `NexusTestHost=true` 时先 `dotnet build tools/NexusPipeline.TaskProtocolTests -p:NexusTestHost=true`，再运行实际输出 `bin/test-host/NexusPipeline.TaskProtocolTests/Debug/net10.0-windows/NexusPipeline.TaskProtocolTests.dll` 并传上述参数。全部命令应在外部隔离副本中运行；该工具报告仅证明选中的真实 Jint/发现/归并/配置事务，不表示上游真实设备已经验证。

固定对端插件的编辑用例由其 `tests/policy.json` 声明的 `editorCaseIds` 约束，保留旧版 BetterGI/ZZZ 的两项编辑用例。MaaStellaSora 的旧 MXU 协议没有编辑入口；新版 MFA 协议要求真实编辑脚本的选择、保留无关设置与重复执行证据，入口读取实际 manifest，缺少声明或文件即失败。

批次内部报告使用 schemaVersion 2，包含精确预期义务、原生 TRX/Vitest/TAP/场景文件及其 hash。必需汇总 从固定源码重新推导计划，拒绝缺失、重复、skip、错 attempt、错 partner/policy、路径逃逸或规范化与原生报告不一致。完整资格上限为每物理 job 150 秒（含 checkout、工具/依赖准备、上传和 post-action），合计最多十个 job：scope + 可选 control + 五批 + 必需汇总 + begin + finalize。必需汇总 自身和 finalize 收尾仍须用完成后的只读服务端记录验收。

必需汇总 只等待仍在排队或运行中的可信 main begin 登记，最多 100 秒，并受自身 130 秒工作截止约束；登记身份错误或已完成失败立即拒绝。API 中带 runner 选择标签、但从未分配 runner 且没有 steps 的已知可选 skipped job 不计为物理作业；已分配 runner、实际 steps 和未知作业继续严格审计。

### 同 SHA 完整重跑

首次 CI 由 `requested` 事件登记一次可信 begin。完整重跑不产生该事件，须由已审核 `main` 显式登记新的 attempt；新 必需汇总 只接受本次 PR/head/run/attempt 身份，未登记时失败。先重跑整个生产者，读取新的 attempt，再执行：

```text
gh run rerun <producer-run-id> --repo FlappiBakuse/NexusPipeline
gh workflow run final-budget.yml --repo FlappiBakuse/NexusPipeline --ref main -f run_id=<producer-run-id> -f attempt=<新的attempt> -f phase=begin
```

不要只重跑失败 job 来替代完整资格。CI 完成后自动触发 finalize；检查成功后仍须读取生产者与两个控制器的完整服务端作业记录，包括 post-action。

## 持续集成显示名称

项目维护的工作流、任务与步骤使用中文职责名称，保留 Host、Plugins、工具与插件的专有名称。`tests/ci-names.json` 登记“控制检查”“必需汇总”“完整预算”和未选中批次的名称；`tests/ci-names.mjs` 从逻辑门禁注册表生成批次标题。标题列出前三项职责和总数，运行摘要列出该批次完整门禁清单。内部 batch ID、artifact 名称与 run/attempt 身份保持稳定。

必需汇总按同一命名入口核验 Actions 实际任务集合，完整预算复核批次编号、任务总数、完整耗时及可信登记。显示名称改变时，可信 main 控制器的触发名称、任务匹配与分支保护绑定须同步；名称或身份不一致时失败，不放宽检查。GitHub 自动生成的启动、收尾步骤由平台提供。

## 双仓候选配对

默认输入使用固定的已合入源码：Host 在一次 scope 中解析 Plugins main，Plugins 使用 `tests/inputs.lock.json` 中已经属于 Host main 历史的提交。API 或源码路径需要两仓同时修改时，可在两个正式 PR 描述中登记同一份 `nexus-ci-pair` JSON 代码块。配对只替换测试来源，现有义务、必需检查与预算保持不变。

从已审核的控制器 checkout 运行只读命令，参数依次为 Host 和 Plugins 的实际 PR 编号：

```text
python tests/ci_inputs.py --create-pair <Host_PR编号> <Plugins_PR编号>
```

命令查询已经存在的 base/head、PR merge commit/tree、策略和输入锁摘要、main 控制器身份，输出待登记的源对象。将完整输出置于双方描述的 `nexus-ci-pair` 代码块中；不要手填未来 SHA 或 run ID。生成输出不代表已获资格，控制器会重新核验实际服务端状态。生成与验证共用 Python 的递归键排序、紧凑 UTF-8 JSON 和 SHA256，源摘要排除自身字段，运行绑定另含真实 run/attempt。

配对要求双方 PR 属于官方仓库、面向 main 且保持打开。scope、实际 checkout、报告和可信 main 的 begin/finalize 必须引用同一 pair；缺少登记、摘要不同、对端变更或部分重跑均不能通过。正式 producer 仍是 `pull_request` 的 `ci.yml`，包含正文 edited 事件，普通正文编辑也运行正常义务。默认模式不接受任意对端覆盖环境变量。

配对登记须先于同一 head 的首个 producer。维护者可在准备两端引用与正文期间短暂暂停 `ci.yml` 调度，登记后立即恢复工作流，并通过 `ready_for_review` 事件启动两端首次完整运行。准备期间必需检查规则保持生效，尚未验证的新 head 不具备合并资格；不得删除早先失败记录或以局部重跑代替完整验证。

两边描述同步后，两边都须完成新的完整运行。另一仓提交不会自动撤销本仓旧绿勾；合并前必须只读复核双方当前 base/head/merge tree、pairDigest 和最新完整 attempt 均一致且检查成功。两个 finalize 独立完成，不互相等待。Host 合入后，将 Plugins 默认测试锁改为真实已合入 Host SHA，移除配对块并重新执行完整默认 CI，再按现役流程合并与发行。源码合入、插件稳定字节和最终 Host 预装候选分别验证。

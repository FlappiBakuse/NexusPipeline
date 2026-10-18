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
node tests/run.mjs gate --id host.integration.desktop
node tests/run.mjs gate --id host.integration.installation-adoption
node tests/run.mjs gate --id host.integration.maintenance
node tests/run.mjs gate --id host.ci-policy
```

`ci` 必须指定一个已知分组。backend 选择真实后端规则、文件事务、执行与调度窄测试；frontend 运行类型检查及核心状态、用户配置请求与公开桥接测试。`tests/policy.json` 中的预期用例与实际原生报告严格匹配。新增或更名选中用例时同步清单，不能删除失败用例以绕过验证。

规范清单位于 `tests/policy.json`，各分组及用例在 `tests/policy/` 按职责维护。Node 读取器 `tools/json-parts.mjs` 与 Python 读取器 `tests/json_parts.py` 合并为原有 schema 1 对象；全局预算、CI batch policy 和有限场景仍在规范清单。报告及跨仓固定输入指纹包含清单和全部声明分片，固定 Git/API 来源的分片使用同一 SHA；缺片或冲突不能退回空策略。

`daily` 使用两个独立 Host 槽位，先启动真实分钟调度，另一槽运行执行和配置，空闲槽接续控制面；共享准备、四组工作及清理全部计时，不设总执行时间预算。支持 `--group execution|config|control|schedule` 进行明确的单组诊断。每组保留 `evidence.json`，父级 `daily-evidence.json` 只有全部预期组及清理通过才成功。

| 场景 | 真实证明 | 外部替代 |
|---|---|---|
| H-E01 execution | 浏览器选择队列并运行，6 脚本、3 队列、4 账号，12 次请求、4 次取消、64 次读取，进程退出和历史归属 | 拥有的外部目标程序 |
| H-E02 config | A/B 保存、取消、正常/失败执行，目录配置还原，落盘 journal 后异常终止与两次启动恢复 | 合成配置与拥有的目标进程 |
| H-E03 control | 真实 CLI、MCP HTTP 初始化与运行观察、合法包安装更新、坏 hash 阻断、两次重启交接 | 官方 HTTPS 响应和合成测试插件 |
| H-E04 schedule | 真实分钟计时产生队列执行、结果归属、持久化水位与重启去重 | 拥有的外部目标程序 |

`integration` 使用 `NEXUS_PARTNER_ROOT` 指定的 Plugins 检出，准备完整桌面应用及实际版本为 0.17.0/0.17.1 的 asInvoker 更新夹具，再运行 UI Smoke/System Smoke。浏览器插件来源默认使用同一显式检出，也可用 `NEXUS_OFFICIAL_PLUGINS_ROOT` 指定。部分 UI 响应由夹具提供；它不是 `daily` 的替代。`release` 同样要求 `NEXUS_PARTNER_ROOT` 为固定 Plugins 检出的绝对路径，从该快照的稳定 catalog 解析两个预装包及嵌入清单，在外部隔离源副本构建生产 requireAdministrator EXE，输出到 `runs/<runId>/production/`，不写项目 release 目录。更新事务诊断同时验证独立 worker、真实新实例启动收据及用户文件保全。

`diagnostic --group store` 构建实际 Test Host 和合成 managed fixture，访问官方 HTTPS catalog/package 地址，由 Test Host 的传输夹具返回固定响应。它验证坏 hash 拒绝、安装事务、新安装后自动启用、重复重启保持启用、显式禁用、插件加载和卸载。商店 gate 只准备 API Host；更新重启 gate 构建内嵌 Vue、完整 Electron 与精确应用清单，真实验证四项资产的交换和启动证明。原生 TAP 与 `store-evidence.json` 位于本次运行目录；这是 H-C09 的安装生命周期诊断，不代表浏览器交互、全部商店恢复矩阵或四组 daily 已完成。

受控 HTTP 实现 `tests/host/TestHostTransport.cs` 仅在 `NexusTestHost=true` 时编入宿主，生产程序不读取 `NEXUS_TEST_HTTP_PLAN`。测试计划必须是绝对路径且 `runId` 匹配 `NEXUS_TEST_RUN_ID`；每项按 HTTP 方法、完整 URI 及可选凭据头/请求体 hash 匹配，未知请求失败，不回退到真实外网。收据只包含场景标识和匹配结果，并在重启后继续消费剩余响应；不会记录凭据、URL或请求体。回环控制面保持真实网络通信。

## 隔离与运行输出

测试不提权或降权，使用当前终端权限。通用运行目录显式使用 `LightweightMode=true`、`OpenDesktopOnStartup=false`；桌面、模式切换和自动打开专项显式记录配置例外。浏览器启动通过 Test Host 适配器记录请求，不能弹出用户浏览器。可设置 `NEXUS_TEST_ARTIFACT_ROOT` 为本次专用的绝对路径；目录内 `.nxp-test-artifact-root.json` 内容如下，`directory` 必须填写该目录的实际绝对路径：

```json
{"schemaVersion":1,"owner":"NexusPipeline.Tests","directory":"<absolute test root>"}
```

未显式设置时使用 runner 临时目录或系统临时目录下的 `NexusPipeline.Tests`，已有无归属目录、链接和非法 run ID 均拒绝执行。本地有目录管理要求时先设置进程级 `TEMP`、`TMP` 或上述显式根；不要改变全局环境或登记已有用户目录。

原字节隔离副本位于 `cache/<source-and-toolchain-fingerprint>/Host`。构建使用真实产品源和依赖，测试不修改产品工程。相同输入的缓存复用需匹配源文件和工具链；独占 lease 防止两个命令同时写同一构建图，未完成清理的 lease 保留供检查。后端与前端共享只读源副本，各自使用不同构建输出。

报告位于 `runs/<runId>/<backend|frontend>/`：`commands.log`、原生 `native.trx` 或 `native.json`、`native-counts.json` 和 `summary.json`。只有预期场景/用例、原生计数、源码/输入身份、计时字段和清理全部一致才给 PASS；报告明确实际被测实现、外部替代及未证明范围。该 PASS 只覆盖所选组，不宣称 CLI、MCP、商店下载、真实调度和 native 设备已通过。

## 生产 PR 检查

GitHub PR 只要求 `Host / 构建检查`，固定单 job 十分钟内执行影响闭包中的真实前端、Host、桌面编译、预装字节核查及适用 A01–A05，不运行测试。文档和纯本机测试变更生成无需构建的成功结果。可在本机用外部输出目录复现：

```text
python tools/ci_check.py plan --base <完整base SHA> --head <完整head SHA> --checkout <实际checkout SHA> --report <外部plan.json>
python tools/ci_check.py run --plan <同一plan.json> --output <新的外部输出目录> [--plugins-root <固定Plugins检出>]
```

计划绑定完整 source tree、原字节指纹及固定对端 SHA。run 重新核对计划与源码，不消费修改过的目标集合。生产编译报告不代表完整运行时装配、候选或发布资格。主分支合并不触发完整候选，候选和发布分别手动执行。

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

## 本机批次诊断

本机按需诊断由 plan 将逻辑义务合并为执行单元，按准备依赖划分 control 与 Windows batch。本机批次中的构建、测试、插件 profile 和命令不设执行时间预算上限；policy 与门禁注册表使用 null 表示无限制，估算耗时只供观察。实际耗时仍记录准备、子命令、原生测试、真实场景、报告、上传和收尾。源码身份、预期用例和场景、原始证据及进程清理仍必须完整；功能失败、取消或缺少报告不能成功。操作协议和进程退出观察仍保留有限等待，用于识别功能失败。

配对核验继续严格检查实际 commit、tree、policy、input lock 和可信 main 登记；核验子进程不设执行时间预算。

批次中的 execution、config、control 按实际选中集合共享 Test Host 构建和两个运行槽位，每组使用独立进程、端口、数据和运行身份。分钟调度单独分配；不需要浏览器的批次只准备 API Test Host。本机批次报告逐份核对 `finite-H-E01.json`、`finite-H-E02.json`、`finite-H-E03.json` 中对应选中场景的原生结果与清理证据。

完整应用批次共用一次前端与 Electron 准备。计划已包含独立 `host.frontend.typecheck` 提供者时，其他应用构建复用该义务，本机批次报告仍核验其真实原生收据；独立 gate 和 release 继续自行类型检查。后端和架构同批时，架构工具及桌面输入在独立输出图中提前准备，原生后端先完成自己的引用图，随后执行两种模式的 Roslyn 检查。桌面观察与新旧应用夹具各用独立目录，并行准备；全部子任务、原生报告和清理均纳入父命令计时，功能失败、取消或清理不完整均使批次失败。

`node tests/run.mjs gate --id host.integration.installation-adoption` 验证手动覆盖后的安装归属登记与中断恢复。运行前设置绝对路径 `NEXUS_ADOPTION_BASELINE_PACKAGE`（官方 0.17.3 完整包）和 `NEXUS_ADOPTION_BASELINE_RELEASE`（独立取得的官方 Release API JSON，包含资产 digest）；入口只读核对旧包，使用当前源码构建 asInvoker Test Host。19 个固定场景覆盖辅助程序存在／缺失时的五个登记阶段、维护暂停的三个提交阶段、独立证明后补回缺失的共同入口与 README，以及未知辅助程序、离线和自行修改清单的拒绝路径，以及完整还原原载荷后维护恢复的中断路径；成功登记场景还验证卸载保留用户数据。HTTP、Setup 资产和注册表隔离范围为受控夹具；结果不代表官方在线发布、真实 Setup 安装或 UAC 验收。失败现场保留在本轮外部报告目录。

`plan`、`batch` 和 `gate` 保留为本机按需入口，报告核对实际输入、原生结果、计数、场景和清理。它们不参与 GitHub PR 检查，也不提供远端合并资格。源码配对和服务端作业审计工具仅用于显式诊断。

本机验收软件从双仓未提交源码使用 [local-acceptance](../development/local-acceptance.md) 构建。实际旧 Setup 升级、卸载日志迁移、元数据回滚与进程中断应使用独立 Windows 用户或可恢复虚拟机。功能 Test Host 的 asInvoker 结果不代替正式 Setup 或生产 manifest 的证据。

独立维护工具相关验证继续使用 `gate --id host.architecture.backend`、`ci --group backend.updates-restart`、`integration` 和 `gate --id host.integration.installation-adoption`。架构 checker 编译 Host、Maintenance、Updater、Uninstaller 及 installer metadata 的 production/test-host 图；工具闭包不允许反向依赖 Host。

`gate --id host.integration.maintenance` 使用真实 WinForms、loopback HTTP、独立 updater 工作进程和 Inno 原生安装/卸载链。设置 `NEXUS_ADOPTION_BASELINE_PACKAGE`、`NEXUS_ADOPTION_BASELINE_RELEASE` 和 `NEXUS_INNO_COMPILER` 为官方 v0.17.3 原包、独立 Release API 回执及固定 Inno 编译器的绝对路径。固定场景验证最终校验与取消交错、未知清理字节、双语安全错误投影、单轮询与晚到响应、窗口关闭所有权、Setup 同版本修复、schema1 首跳、卸载取消/默认保留/明确删除，以及安装/便携缺工具更新和篡改拒绝。测试目录和数据全部隔离；唯一 HKCU 测试 AppId 与归属键、管理目录、快捷方式及 asInvoker 交接由夹具明确记录，生产安装器不启用这些替换。原包 reader 从未修改的官方 bundle 中加载执行，不运行原版 Main，不把新版实现当成旧 reader。生产 UAC、真实用户安装及实际快捷方式仍需人工验收。

同一轮可将 `NEXUS_MAINTENANCE_PREPARED_ROOT` 指向该 gate 的已有运行目录，复用完整应用和未来目标夹具；入口核对实际产品源码字节。设置 `NEXUS_MAINTENANCE_REBUILD_NATIVE=1` 时重新编译原生安装夹具，允许安装器模板独立改变，其他产品源码必须一致。报告分别保留应用构建身份、原生脚本摘要、探针源码和本次场景结果；复用不改变原产物的源码身份。已完成场景只按冻结载荷清单删除可重建的匹配字节，未知文件、配置、数据和失败记录保留。

安装归属 gate 要求 `NEXUS_ADOPTION_BASELINE_PACKAGE` 和 `NEXUS_ADOPTION_BASELINE_RELEASE` 为独立认证的官方 v0.17.3 原包和 Release API JSON。原包只读验证；受控归属接管案例使用当前未发行 schema 2 源和明确标注的未来版本夹具，不执行候选来识别角色、不将夹具称为已发行旧 reader。官方旧版 Setup 首跳、生产 UAC 和原生安装/卸载仍须人工验收。缺输入直接拒绝运行，不伪造旧包。

## 本机低负载与构建复用

在进程环境设置 `NEXUS_TEST_LOW_RESOURCE=1`、绝对路径 `NEXUS_TEST_RESOURCE_ROOT` 和已登记的 `NEXUS_TEST_ARTIFACT_ROOT`，继续使用上述 `ci`、`smoke`、`daily`、`gate`、`batch` 入口。Host 与 Plugins 共用同一个 resource root；独立调用排队取得独占 reservation。锁中记录进程和随机身份，失效锁、未知内容或清理未确认时保留现场并拒绝接管，不根据 PID 自动删除锁。收到取消或测试失败仍按原生结果失败。

普通核心、API 测试在 `cache/incremental/<slot>/Host|Plugins` 维护独占原字节工作副本。槽位绑定源仓库路径、工具链与依赖/工程输入；每次完整记录实际源码指纹，只写变化文件，删除已登记且原字节未变的移除源码，保留增量编译输出。缓存源被外部修改、链接或未知同名内容时拒绝同步。源码变化不复用旧测试结果；原生报告、预期场景、用例集合和清理要求不变。

涉及完整应用身份、安装和更新夹具的 integration、相关 gate、batch 与 release 保留按完整源码指纹隔离的工作副本，防止可变槽位改变旧夹具的源码证明。完整应用准备及旧/新版本夹具按精确源码、工具链和构建参数缓存；命中时逐文件核对完整集合与 SHA256。缺少完成收据、失败构建或字节变化不得作为命中，也不自动删除失败现场后重试。维护 gate 的 `application-inputs-ref.json` 记录本轮实际使用的准备位置。

npm 安装收据绑定 Node/npm、平台架构、package/lock、项目 npm 配置、npm 环境及 workspace 选择；匹配已安装 lock 才复用。依赖输入变化或安装失败不生成成功收据。完整应用缓存允许省去相同输入的桌面 npm 安装、Electron 解压和维护工具编译。

低负载模式中 dotnet/npm/Python 重型子命令串行，MSBuild build/publish/test/restore 使用 `-m:1`，子进程设置 `DOTNET_PROCESSOR_COUNT=2`，Host Vitest 一个 worker，插件执行槽位一个。真实测试内部的并发、分钟调度、竞态、超时和取消语义不变；这不是整机 CPU 百分比硬上限。日志记录排队毫秒、源码复制文件/字节、删除文件及构建/依赖命中。

直接调用生产验收或其他 Python 构建时，使用同一 reservation 的包装入口：

```text
node tools/local-work.mjs python tools/local_acceptance.py build --host-root <Host> --plugins-root <Plugins> --output <外部独占验收目录> --cache <本轮共享缓存>
```

包装入口只控制本机资源，不改变生产构建参数、来源证明、十四插件装配或人工验收标准。先完成适用自动回归，再构建最终验收软件；存在验收软件时仍遵守保全和禁止覆盖要求。所有目录跟随本机临时目录策略；保留当前缓存以减少重复写入，过期缓存仅在无占用、可重建且不被失败证据引用时按归属清理。

工具验证：`node --test --test-concurrency=1 tests/support/local-resources.test.mjs tests/support/budget.test.mjs`。覆盖增量字节、删除源码、依赖失效、未知修改/链接拒绝、跨进程互斥、取消、失败传播和准备产物损坏拒绝，不加入产品核心用例清单。

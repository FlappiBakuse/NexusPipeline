# 自动更新

默认更新源读取 `metadata/updates/v2/channels.json` 的两槽声明。pendingTarget 只有在 Release 已公开、渠道正确、分页资产身份与协议要求有效时才成为可用版本；其尚未发布、损坏或要求未知能力时，独立校验 previousPublishedTarget 并保留待发布状态。没有上一目标时保持当前安装，不枚举历史 Release，也不推荐降级。旧 `metadata/updates/channels.json` 保留 reader 1 发现合同，不向旧 reader 投放新配方。

reader 2 将 Setup、Portable、Update 三个公开资产绑定到同一次 Release 的资产 ID、尺寸和 GitHub digest。Portable 与 Update 内的 schema 1 配方冻结 included/reused/removed 文件及目标 payload schema 2 清单。当前安装的精确基线匹配且差异包更小时使用 Update；传输不可用或合法配方的基线不匹配只允许一次 Portable 回退。摘要、路径、文件集合、能力或迁移路线错误直接阻断。重建后完整核对 PE、ASAR、前端构建身份及 payload manifest；Portable 的预装插件仅核验，不覆盖已有插件目录。reader 1 的组件解析保留给明确的旧协议目标。

当前已登记 g0170 无业务数据变化路线；目标 Host 的专用 runner 在普通服务初始化之前处理握手，父协调器验证真实进程与管道身份、一次性随机数、构建、能力和序号，确认结束后才交换应用。新事务使用 schema 3 journal，冻结发行资产、运输方式、配方摘要、基线清单、实际下载与复用字节及 runner 收据；已知 schema 1/2 现场仍按原 reader 恢复。跨 schema 的编译迁移器框架由模块 owner 提供明确存储根和 schema，通过隔离转换、备份、可恢复切换和精确文件收据保护数据。未知路线、schema 和恢复现场继续阻断。契约见[更新协议](../../contracts/updates/README.md)。

发现入口对未知目标能力先规划有界桥接：要求源 reader 与完整能力集合匹配、桥接入口可由当前 reader 验证、目标能力覆盖最终要求，且版本严格处于本机与最终目标之间。最多核验八个桥接，使用同一 Release/资产摘要验证，不下载未知协议目标清单，不执行入口声明的命令。状态分别报告 `bridge-required` 和 `bridge-unavailable`；桥接应用后重新检查最终目标。

## 安装器交接和启动确认

Setup 要求本实例停止，使用事务互斥体，将已验证的新候选 Host 复制到同根 `.nxp/runtime/workers/update-<事务随机ID>.exe`，通过 `installer-transition` 和 `--app-root` 进入专用协调器。协调器核对旧安装归属、旧载荷、候选映像及安装元数据检查点，再交由同一 `UpdateService` 和 apply worker 执行切换。配置恢复、运行、编辑和其他更新事务阻断切换。

`NexusPipeline Update.exe` 是 Host 的原字节副本。无参数启动该文件或显式 `update-window` 进入更新窗口；worker 参数优先分派。已有服务时窗口通过核验后的本地控制面请求更新，无服务时只组合最小更新依赖，不加载插件和调度服务。

交换完成先写 `AwaitingStartup`，保留 immutable backup。新宿主在维护租约下初始化插件和 Control API 后提交回执；worker 核对事务 ID、真实程序集版本、实际 EXE SHA256、PID、完整映像路径及启动时间，收尾完成后才返回成功。新宿主等待成功结果后解除维护，执行正常启动检查。回执失败时只按已捕获身份停止候选并回滚；无法确认停止或清理时保留 journal、备份和错误结果，不称更新完成。缺失 Phase、CreatedAt 或身份的旧 journal 保留原字节并阻断更新；当前事务的 CreatedAt 不刷新。worker 在备份前冻结自身身份和目标映像，失败证明与已退出 worker 对应后才清理本次暂存，避免重复启动失败事务。

## 实例归属和卸载

`InstallationOwnership` 管当前用户安装归属。随机 instance ID、规范根目录、用户 SID 和当前应用文件 SHA256 清单通过 DPAPI 保护，并与 HKCU 的摘要锚点核对；注册表路径本身不足以接管旧目录。辅助程序位于用户级管理目录，调用前也按当前受保护清单验证。

受保护身份将共同 Update 入口存入 `AuxiliaryPayloadFiles`，其他路径保留在旧卸载器可读取的 `PayloadFiles`；Host 解封后合并、校验唯一性并按完整归属操作。手动覆盖首次启动将旧归属标为 `manual-adoption-pending`，保留原版本及每个文件摘要，旧卸载器不能继续维护。暂停与恢复 active 均有独立 DPAPI 检查点及 HKCU 摘要核对；联网核验官方包后才更新载荷归属和安装 helper。普通运行与维护状态分开，自动检查不会隐式下载完整包进行归属收尾。

归属收尾允许共同 Update 入口与 README 缺失；独立验证官方完整载荷后，仅从本次已验证 staging 原子补回缺失文件，再核对完整载荷并提交登记。离线保持缺失及维护暂停；已存在但摘要不同的文件保留并报错，不以补文件为由覆盖未知字节。

更新提交刷新当前载荷清单，回滚恢复清单及辅助程序。卸载先完成归属、链接和恢复现场检查，再删除当前清单中仍匹配 SHA256 的应用文件；未知或已修改文件保留。默认保留数据并记录 retained-data 状态，再安装同一路径需明确确认保留数据重用。明确删除数据只遍历本实例固定数据目录，拒绝链接和未解决恢复现场，不触及游戏、脚本或系统运行时。

## 更新检查与闲时应用

`UpdateAutomationService` 是宿主级单例协调器，负责运行期间的更新检查周期和闲时更新编排；启动前更新由 `StartupUpdateCoordinator` 在启动恢复收尾完成后、宿主服务初始化前处理。`UpdateService` 继续拥有清单、下载、SHA256 校验、staging、应用 journal、恢复和回滚状态机。定期检查开关开启时，运行期服务启动约 5 秒后首次检查，之后以自动检查完成时间为起点每 12 小时检查一次；人工检查不会重置自动周期。宿主和插件版本使用 `major.minor.patch`、`-beta.N` 或 `-rc.N` 的受限格式，比较顺序为 beta、rc、stable。

“闲时自动更新”默认关闭，且要求定期检查开关同时启用。两项开关均开启时，启动恢复完成后先检查宿主更新（检查最多 30 秒、下载最多 5 分钟）；可下载的新版本会在宿主服务初始化前暂存并应用，已有 `Ready` 暂存也会应用，然后由更新器重新启动程序。启动检查、下载或应用失败时继续运行当前版本；同一目标失败后冷却 12 小时，人工更新操作可重置冷却。策略屏障阻断下载。运行期间仍保留原有闲时更新：更新包可先下载并校验为 `Ready`，真正应用前必须通过 `AutoUpdateIdlePolicy` 取得 `HostMaintenanceLease`，再在同一准入协调域内确认没有活动运行、编辑会话、待执行系统操作、待准入/等待中的调度 occurrence、未触发的启动队列，并且未来 5 分钟内没有 scheduled occurrence。调度器的 occurrence 注册与队列变更共享该协调域，避免检查与维护租约之间的竞态。启动恢复无法证明文件安全时停止服务启动。

插件自动更新使用独立的 `PluginAutoUpdateEnabled` 开关。启动时在宿主更新恢复完成后、插件待处理事务应用及 `LoadAll` 前，检查并暂存所有符合官方 catalog、宿主兼容且有新版本的已安装插件，包括已禁用插件；手动安装的插件仅在 artifact 与 catalog 精确匹配时参与。检查最多 30 秒、下载最多 5 分钟，失败不会阻断当前插件加载。运行期每 12 小时检查并下载，在取得同一维护租约后安全重启以应用；插件启用偏好在更新后保留。

运行期自动化等待信息通过更新状态 API 和 MCP `get_update_status` 的 additive `automation` 投影提供，包括检查开关、上次/下次自动检查、是否等待闲时和阻止原因。渠道或更新源变化会使发现结果进入 pending invalidation；正在进行或已 Ready 的事务保留给当前人工处理，自动化不会应用已失效的发现结果。

更新检查发现候选版本后，`UpdateService` 通过独立策略域读取 canonical `metadata/updates/legacy-policy.json`。策略必须通过 schema、仓库标识、版本顺序、屏障 code 和迁移 URL 校验；网络失败、响应超限或策略无效时保持发现结果并禁止内置下载。对当前版本到目标版本区间内的最早屏障，状态 API 和 MCP 返回 `manualUpdateRequired`、`updateBlockCode=breaking-update`、`barrierVersion` 与可选 `migrationUrl`，下载、启动前自动应用、下次启动应用和闲时自动应用均被拒绝。策略验证成功且未命中屏障时才允许下载；策略缓存只用于带 HTTP validator 的后续验证，网络失败不会授权旧缓存。

旧安装允许缺少根目录 `README.md`；已存在的说明文件仍按清单校验。备份清单固定说明文件的存在状态，回滚恢复原字节或原先缺失状态。候选包、交换后的新安装及启动回执要求完整载荷。应用请求在停止桌面前校验旧安装，worker 在宿主退出后重新核验。

更新状态的 `lastApplyFailure` 返回最近一次有效失败的事务 ID、目标版本、错误代码和 UTC 时间，仅在目标高于当前运行版本时显示。结果读取不改变未知或无效记录；更新卡按 `automation.nextCheckAt` 刷新首次及后续检查结果，检查、下载和应用期间每秒刷新。

主程序更新事务从已验证清单推导交换范围；schema 2 载荷包括 `NexusPipeline.exe`、`NexusPipeline Update.exe`、`resources/desktop/`、`README.md` 和 `resources/payload-manifest.json`。Vue 以资源形式内嵌在 Host 中。用户 `plugins/`、`config/`、`data/`、`history/`、`logs/`、`.nxp/state/desktop/` 和未知同级文件均不参与交换；官方插件仓库不参与宿主自动更新流程。

停止桌面前，当前事务冻结 schema 1 的 `DesktopResumeIntent`，Mode 仅接受 `show` 或 `background`。尚未创建窗口的根启动 Show 请求也进入冻结意图；恢复不依赖历史 handoff 或被删除的 session。明确未启动 worker 的失败由同事务 Desktop owner 撤销停止状态并恢复该意图；已启动或无法确认的 worker 保留租约、journal 与停止状态。普通重启未启动子进程时只撤销匹配的 handoff，当前 renderer 恢复 ready 并保留草稿。

journal 记录 staging、backup 的文件和目录精确清单、每文件长度及 SHA256，以及 worker 与已下载资产的摘要。新协议冻结 GitHub Release 及 asset ID、名称、长度和服务端 digest，恢复和应用继续使用该发行身份。清理先核验所有资源，再通过独占文件句柄复核和删除对应字节；目录仅允许非递归空目录删除。中断后可继续清理已缺失的已确认资源，新增、变形、链接或变化的未知字节保留现场。交换和回滚共用这些清单；已确认路径上的损坏候选先写保护记录并移入 `.nxp/state/updates/<事务>.preserved/`，随后恢复完整旧载荷。未知新增候选文件阻断回滚并保留 immutable backup 与 journal。

下载和解压只创建新文件，拒绝覆盖已有 staging。网络操作按实际写入的字节记录长度与摘要，并记录由本操作创建的目录；取消或失败先核验完整创建集合，出现未知子项或改变字节时保留现场。就绪后的暂存清单和 checksum 摘要沿用创建收据，申请应用时重新核验，不重新认领后来加入的内容。

候选下载及 staging 先按 SHA256、精确文件清单、规范构建身份、原始前端 index 摘要和 ASAR 内的身份记录执行只读校验，不运行候选 EXE。journal 冻结目标清单摘要、buildId、frontendHash、代际和原包摘要；交换前重新核对，交换后及启动回执提交前再次核对完整应用。每项资产交换进度单调落盘；回滚恢复同一冻结备份并核对旧载荷。桌面退出通知和已捕获的完整 Electron 家族退出证明必须先完成，文件锁或未知进程身份均保留现场并阻断替换。

v0.17.0 使用独立安装代际 `g0170`，不接管 `g01615` 的目录、注册表登记及管理辅助程序。`update-policy.json` 的 `0.17.0` 独立屏障要求旧版用户按新目录安装指南迁移；旧目录和配置原地保留。同路径 Setup 升级只接收当前代际、本 Windows 用户已登记且身份匹配的安装目录。文件归属清单包含 Host、桌面完整运行时及载荷清单自身；安装、更新和卸载使用同一应用边界。

候选构建对同一 production staging 生成 `NexusPipeline-v<V>-win-x64-Setup.exe`、`-Portable.zip` 和 `-Update.zip`。`candidate.json` 记录这三项公开资产与两份内部构建元数据；公开发行不包含 SHA 侧文件或独立配方资产，发行说明末尾列出资产摘要表。publisher 从原成功 run 下载服务端指定 artifact，核对来源、tree、依赖与编译器锁、完整资产字节，再发布并远端逐项回读；不从发行页按 latest 重新取包。

target manifest 按完整文件清单与构建身份重建 staging。未变组件仅复用当前安装中逐文件核验通过的内容；组件传输失败允许一次完整 ZIP 回退，身份、摘要或载荷错误直接失败。下载前同时计算新 staging、旧载荷备份、下载和工作空间的卷上峰值，应用仍执行同一维护租约与恢复事务。

当前迁移路线为 `g0170-no-store-change-v1`，数据 store 集合为空，保留用户数据原地不变。候选的 target runner 在正常宿主、插件及桌面初始化前通过私有管道完成实际映像身份、nonce、协议、能力和路线核验。`UpdateDataMigration` 提供独立副本、跨 schema 路线、输出清单、切换、恢复和提交框架；实际业务 schema 变更须先由对应 owner 登记并验证，不从无数据变更路线推定支持其他格式。

启动恢复发现未完成的 apply journal 且 immutable backup 包含宿主 exe 时，当前启动实例不会直接覆盖自己的映像；它会拉起独立 recovery worker，等待当前实例释放单实例互斥体后还原 backup、写入 `RollbackConfirmed` 并重拉宿主，旧版本启动收尾再删除 backup 与 journal。回滚失败时现场继续保留并由下一次启动重试。

应用准入、下次启动应用、apply worker 与 recovery worker 在切换前由配置模块只读检查 `data/` 内的会话标记及非空恢复工作区。当前或未知格式的任务选择 journal、编辑隔离、配置交换/快照事务均返回 `configuration-recovery-pending` 或保留启动事务并拒绝切换；不解析未知 journal，也不删除现场。重解析点及无法读取的现场同样阻断。配置恢复完成后重新检查即可继续，普通空目录和可重建的脚本工作区不永久阻断更新。

## Setup 元数据与空间

`InstallerMetadataCheckpoint` 使用 schema 2 记录 manager、应用根和可信快捷方式的精确字节；schema 1 现场继续由 `LegacyInstallerMetadataCheckpoint` 恢复。预置前冻结备份，先记录可恢复的 Prepositioning，再复制完整 EXE/DAT/MSG 文件组。Inno 后观察实际 ARP 和卸载文件，凭当前应用身份与更新回执判定提交或回滚；只删除已确认属于本事务的字节，第三方变化保留现场并阻断。提交清理可以在下次恢复时继续。

`tools/installer-space.iss` 从构建时冻结的载荷清单计算核心与首次插件选择，并按目标、manager、临时卷合并新增占用，包含新 staging、真实旧资产复制、卸载预置、记录和已知下载。永久 helper 是 Host 副本，临时元数据程序单独计入临时卷。分配粒度使用实际卷信息，元数据余量随清单路径与记录数增长。预览复用已核对的保护身份，向导和互斥下最终检查共用原生计算；最终检查重新解封归属、检测依赖并读取各卷可用空间。ARP EstimatedSize 沿用 Inno 的实际安装计数，不能写入临时峰值。

准备数据副本前必须通过调用方持久回调保存 Preparing 状态、输入清单和预期工作区。独占创建目录后记录物理目录身份与 owner token，完成文件写入后更新精确清单，最终发布 Prepared。恢复核对原始 store 和已记录工作区；创建后尚未取得身份、转换中未入清单的字节或其他未知内容保留现场，不递归删除。该框架的受控强杀恢复测试不代表真实业务 schema 迁移已经登记或验收。

空间检查按实际卷的分配单位计入解压、重建、备份、worker、安装 helper 与 journal 余量；应用与每用户安装管理器跨卷时逐卷检查，空间不足在写入或交换之前拒绝。

手动覆盖维护暂停期间，若用户完整还原原登记版本的应用载荷，新 Setup 先复核原 DPAPI/HKCU 锚点和每个受管文件摘要，再通过可恢复的双 seal 检查点恢复旧维护状态。旧载荷未完整还原、未知 helper 或归属变化时保留暂停与现场。

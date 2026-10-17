# 自动更新

更新域负责发现与资格判断，独立 Maintenance/Updater 负责应用交接，Host 提供维护准入和启动回执。本页说明这些 owner 的事务协作；字段与配方定义见[更新协议](../../contracts/updates/README.md)。

## 发现与候选校验

默认更新源读取 `metadata/updates/v2/channels.json` 的两槽声明。pendingTarget 只有在 Release 已公开、渠道正确、分页资产身份与协议要求有效时才成为可用版本；其尚未发布、损坏或要求未知能力时，独立校验 previousPublishedTarget 并保留待发布状态。没有上一目标时保持当前安装，不枚举历史 Release，也不推荐降级。旧 `metadata/updates/channels.json` 保留 reader 1 发现合同，不向旧 reader 投放新配方。

reader 2 将 Setup、Portable、Update 三个公开资产绑定到同一次 Release 的资产 ID、尺寸和 GitHub digest。Portable 与 Update 内的 schema 1 配方冻结 included/reused/removed 文件及目标 payload schema 2 清单。当前安装的精确基线匹配且差异包更小时使用 Update；传输不可用或合法配方的基线不匹配只允许一次 Portable 回退。摘要、路径、文件集合、能力或迁移路线错误直接阻断。重建后完整核对 PE、ASAR、前端构建身份及 payload manifest；Portable 的预装插件仅核验，不覆盖已有插件目录。reader 1 的组件解析保留给明确的旧协议目标。

当前已登记 g0170 无业务数据变化路线；目标 Host 的专用 runner 在普通服务初始化之前处理握手，父协调器验证真实进程与管道身份、一次性随机数、构建、能力和序号，确认结束后才交换应用。普通新事务使用 schema 3 journal；需要移除已确认的旧空格命名入口时使用 schema 4 journal，冻结发行资产、运输方式、配方摘要、基线清单、实际下载与复用字节及 runner 收据；已知 schema 1/2 现场仍按原 reader 恢复。跨 schema 的编译迁移器框架由模块 owner 提供明确存储根和 schema，通过隔离转换、备份、可恢复切换和精确文件收据保护数据。未知路线、schema 和恢复现场继续阻断。契约见[更新协议](../../contracts/updates/README.md)。

发现入口对未知目标能力先规划有界桥接：要求源 reader 与完整能力集合匹配、桥接入口可由当前 reader 验证、目标能力覆盖最终要求，且版本严格处于本机与最终目标之间。最多核验八个桥接，使用同一 Release/资产摘要验证，不下载未知协议目标清单，不执行入口声明的命令。状态分别报告 `bridge-required` 和 `bridge-unavailable`；桥接应用后重新检查最终目标。

## 安装器交接和启动确认

Setup 要求本实例停止，原生安装器在写入期间持有实例互斥锁。已验证的候选 `NexusPipeline.update.exe` 复制到同根 `.nxp/runtime/workers/update-<事务随机ID>.exe`，通过 `installer-transition` 和 `--app-root` 进入专用协调器。交接时释放安装器互斥锁；协调器核对旧安装归属、旧载荷、独立工具身份及安装元数据检查点，再交由 `UpdateEngine` 和 apply worker 执行切换。若已有已核验的本实例服务，只请求其 installer-apply API，由 Host 执行业务准入。无法证明服务身份时拒绝交接。

`NexusPipeline.update.exe` 是独立 WinForms 程序，只引用有限的 `NexusPipeline.Maintenance` 库。无参数启动更新窗口，已验证的内部参数进入 worker；它不加载 Host、插件、调度、托盘、Vue 或桌面。当前版本来自安装根的已验证 Host。已有服务时窗口通过核验 PID、启动时间、映像、端口、版本及代际的本地控制面请求更新，无服务时取得原实例互斥锁后使用本地引擎。窗口固定“检查”和一个主按钮，按状态执行下载、取消或应用。取消保持 Cancelling，直到原操作 IO 和清理 finally 完成；期间不能启动新操作。关闭远程窗口仅取消窗口自身请求，本地窗口等待实际下载清理完成。

Host 内嵌本次构建的可信独立 updater 恢复资源。根更新入口缺失时仍可抽取专用 worker；目标 Host hash 与 worker hash 分开冻结。公共载荷不嵌入 uninstaller。

更新窗口使用自身可执行文件的产品图标；已知错误和安装工具修复说明同时显示本地化操作建议与稳定错误码，新的快照清除已消退的错误。

交换完成先写 `AwaitingStartup`，保留 immutable backup。新宿主在维护租约下初始化插件和 Control API 后提交回执；worker 核对事务 ID、真实程序集版本、实际 EXE SHA256、PID、完整映像路径及启动时间，收尾完成后才返回成功。新宿主等待成功结果后解除维护，执行正常启动检查。回执失败时只按已捕获身份停止候选并回滚；无法确认停止或清理时保留 journal、备份和错误结果，不称更新完成。缺失 Phase、CreatedAt 或身份的旧 journal 保留原字节并阻断更新；当前事务的 CreatedAt 不刷新。worker 在备份前冻结自身身份和目标映像，失败证明与已退出 worker 对应后才清理本次暂存，避免重复启动失败事务。

## 实例归属和卸载

`InstallationOwnership` 管当前用户安装归属。随机 instance ID、规范根目录、用户 SID 和当前应用文件 SHA256 清单通过 DPAPI 保护，并与 HKCU 的摘要锚点核对；注册表路径本身不足以接管旧目录。辅助程序位于用户级管理目录，调用前也按当前受保护清单验证。

schema 2 保护身份将共同应用文件与固定 `InstallerTools` 分开，分别记录根 `NexusPipeline.uninstall.exe`、manager `nexus-uninstall-worker.exe`、原生 Inno 引擎的路径、独立 hash 和版本。schema 1 继续按旧合同读取，迁移到独立工具必须通过 Setup，不能靠手动覆盖提交归属。

schema 2 手动覆盖首次启动将旧归属标为 `manual-adoption-pending`，保留原版本及每个文件摘要。联网核验官方完整包后才更新共同载荷归属；仅从已验证 staging 补回缺失的公共 updater 或 README。已有但摘要不同的字节保留并报错。安装工具只在两个登记副本之一仍可信时互相修复；两者都缺失返回需要 Setup 修复的提示，不从 Host 推断或复制。普通 Host 更新保留已登记的独立卸载工具版本。

公开 uninstaller 是 asInvoker 独立程序，核验保护身份后启动登记的原生 Inno 卸载器。原生确认保留，默认保留数据；真正移除经明确提权调用登记的独立 manager worker。原生引擎和日志仍位于原安装根，保留 Inno 自身生命周期。Portable 和通用 Update 不包含此程序或其恢复载荷。

更新提交刷新当前载荷清单，回滚恢复清单及辅助程序。卸载先完成归属、链接和恢复现场检查，再删除当前清单中仍匹配 SHA256 的应用文件；未知或已修改文件保留。默认保留数据并记录 retained-data 状态，再安装同一路径需明确确认保留数据重用。明确删除数据只遍历本实例固定数据目录，拒绝链接和未解决恢复现场，不触及游戏、脚本或系统运行时。

## 更新检查与闲时应用

`UpdateAutomationService` 是宿主级单例协调器，负责运行期间的更新检查周期和闲时更新编排；启动前更新由 `StartupUpdateCoordinator` 在启动恢复收尾完成后、宿主服务初始化前处理。`UpdateEngine` 拥有清单、下载、SHA256 校验、staging、应用 journal、恢复和回滚状态机；Host `UpdateService` 投影设置并保留维护租约、桌面停止和生命周期交接。定期检查开关开启时，运行期服务启动约 5 秒后首次检查，之后以自动检查完成时间为起点每 12 小时检查一次；人工检查不会重置自动周期。宿主和插件版本使用 `major.minor.patch`、`-beta.N` 或 `-rc.N` 的受限格式，比较顺序为 beta、rc、stable。

“闲时自动更新”默认关闭，且要求定期检查开关同时启用。两项开关均开启时，启动恢复完成后先检查宿主更新（检查最多 30 秒、下载最多 5 分钟）；可下载的新版本会在宿主服务初始化前暂存并应用，已有 `Ready` 暂存也会应用，然后由更新器重新启动程序。启动检查、下载或应用失败时继续运行当前版本；同一目标失败后冷却 12 小时，人工更新操作可重置冷却。策略屏障阻断下载。运行期间仍保留原有闲时更新：更新包可先下载并校验为 `Ready`，真正应用前必须通过 `AutoUpdateIdlePolicy` 取得 `HostMaintenanceLease`，再在同一准入协调域内确认没有活动运行、编辑会话、待执行系统操作、待准入/等待中的调度 occurrence、未触发的启动队列，并且未来 5 分钟内没有 scheduled occurrence。调度器的 occurrence 注册与队列变更共享该协调域，避免检查与维护租约之间的竞态。启动恢复无法证明文件安全时停止服务启动。

插件自动更新使用独立的 `PluginAutoUpdateEnabled` 开关。启动时在宿主更新恢复完成后、插件待处理事务应用及 `LoadAll` 前，检查并暂存所有符合官方 catalog、宿主兼容且有新版本的已安装插件，包括已禁用插件；更新必须有可信安装归属，且 artifact、通道和当前目录身份匹配；仅名称相同的手动安装目录不得接管。检查最多 30 秒、下载最多 5 分钟，失败不会阻断当前插件加载。运行期每 12 小时检查并下载，在取得同一维护租约后安全重启以应用；插件启用偏好在更新后保留。

运行期自动化等待信息通过更新状态 API 和 MCP `get_update_status` 的 additive `automation` 投影提供，包括检查开关、上次/下次自动检查、是否等待闲时和阻止原因。渠道或更新源变化会使发现结果进入 pending invalidation；正在进行或已 Ready 的事务保留给当前人工处理，自动化不会应用已失效的发现结果。

更新检查发现候选版本后，`UpdateEngine` 通过独立策略域读取 canonical `metadata/updates/legacy-policy.json`。策略必须通过 schema、仓库标识、版本顺序、屏障 code 和迁移 URL 校验；网络失败、响应超限或策略无效时保持发现结果并禁止内置下载。对当前版本到目标版本区间内的最早屏障，状态 API 和 MCP 返回 `manualUpdateRequired`、`updateBlockCode=breaking-update`、`barrierVersion` 与可选 `migrationUrl`，下载、启动前自动应用、下次启动应用和闲时自动应用均被拒绝。策略验证成功且未命中屏障时才允许下载；策略缓存只用于带 HTTP validator 的后续验证，网络失败不会授权旧缓存。

## 载荷交换与回滚

旧安装允许缺少根目录 `README.md`；已存在的说明文件仍按清单校验。备份清单固定说明文件的存在状态，回滚恢复原字节或原先缺失状态。候选包、交换后的新安装及启动回执要求完整载荷。应用请求在停止桌面前校验旧安装，worker 在宿主退出后重新核验。

更新状态的 `lastApplyFailure` 返回最近一次有效失败的事务 ID、目标版本、错误代码和 UTC 时间，仅在目标高于当前运行版本时显示。结果读取不改变未知或无效记录；更新卡按 `automation.nextCheckAt` 刷新首次及后续检查结果，检查、下载和应用期间每秒刷新。

主程序更新事务从已验证清单推导交换范围；schema 2 载荷包括 `NexusPipeline.exe`、`NexusPipeline.update.exe`、`resources/desktop/`、`README.md` 和 `resources/payload-manifest.json`。Vue 以资源形式内嵌在 Host 中。用户 `plugins/`、`config/`、`data/`、`history/`、`logs/`、`.nxp/state/desktop/` 和未知同级文件均不参与交换；官方插件仓库不参与宿主自动更新流程。

停止桌面前，当前事务冻结 schema 1 的 `DesktopResumeIntent`，Mode 仅接受 `show` 或 `background`。尚未创建窗口的根启动 Show 请求也进入冻结意图；恢复不依赖历史 handoff 或被删除的 session。明确未启动 worker 的失败由同事务 Desktop owner 撤销停止状态并恢复该意图；已启动或无法确认的 worker 保留租约、journal 与停止状态。普通重启未启动子进程时只撤销匹配的 handoff，当前 renderer 恢复 ready 并保留草稿。

journal 记录 staging、backup 的文件和目录精确清单、每文件长度及 SHA256，以及 worker 与已下载资产的摘要。新协议冻结 GitHub Release 及 asset ID、名称、长度和服务端 digest，恢复和应用继续使用该发行身份。清理先核验所有资源，再通过独占文件句柄复核和删除对应字节；目录仅允许非递归空目录删除。中断后可继续清理已缺失的已确认资源，新增、变形、链接或变化的未知字节保留现场。交换和回滚共用这些清单；已确认路径上的损坏候选先写保护记录并移入 `.nxp/state/updates/<事务>.preserved/`，随后恢复完整旧载荷。未知新增候选文件阻断回滚并保留 immutable backup 与 journal。

下载和解压只创建新文件，拒绝覆盖已有 staging。网络操作按实际写入的字节记录长度与摘要，并记录由本操作创建的目录；取消或失败先核验完整创建集合，出现未知子项或改变字节时保留现场。就绪后的暂存清单和 checksum 摘要沿用创建收据，申请应用时重新核验，不重新认领后来加入的内容。

候选下载及 staging 先按 SHA256、精确文件清单、规范构建身份、原始前端 index 摘要和 ASAR 内的身份记录执行只读校验，不运行候选 EXE。journal 冻结目标清单摘要、buildId、frontendHash、代际和原包摘要；交换前重新核对，交换后及启动回执提交前再次核对完整应用。每项资产交换进度单调落盘；回滚恢复同一冻结备份并核对旧载荷。桌面退出通知和已捕获的完整 Electron 家族退出证明必须先完成，文件锁或未知进程身份均保留现场并阻断替换。

v0.17.0 使用独立安装代际 `g0170`，不接管 `g01615` 的目录、注册表登记及管理辅助程序。`update-policy.json` 的 `0.17.0` 独立屏障要求旧版用户按新目录安装指南迁移；旧目录和配置原地保留。同路径 Setup 升级只接收当前代际、本 Windows 用户已登记且身份匹配的安装目录。文件归属清单包含 Host、桌面完整运行时及载荷清单自身；安装、更新和卸载使用同一应用边界。

## 发行输入与迁移路线

候选构建对同一 production staging 生成 `NexusPipeline-v<V>-win-x64-Setup.exe`、`-Portable.zip` 和 `-Update.zip`。`candidate.json` 记录这三项公开资产与两份内部构建元数据；公开发行不包含 SHA 侧文件或独立配方资产，发行说明末尾列出资产摘要表。publisher 从原成功 run 下载服务端指定 artifact，核对来源、tree、依赖与编译器锁、完整资产字节，再发布并远端逐项回读；不从发行页按 latest 重新取包。

target manifest 按完整文件清单与构建身份重建 staging。未变组件仅复用当前安装中逐文件核验通过的内容；组件传输失败允许一次完整 ZIP 回退，身份、摘要或载荷错误直接失败。下载前同时计算新 staging、旧载荷备份、下载和工作空间的卷上峰值，应用仍执行同一维护租约与恢复事务。

当前迁移路线为 `g0170-no-store-change-v1`，数据 store 集合为空，保留用户数据原地不变。候选的 target runner 在正常宿主、插件及桌面初始化前通过私有管道完成实际映像身份、nonce、协议、能力和路线核验。`UpdateDataMigration` 提供独立副本、跨 schema 路线、输出清单、切换、恢复和提交框架；实际业务 schema 变更须先由对应 owner 登记并验证，不从无数据变更路线推定支持其他格式。

## 中断恢复与配置门禁

启动恢复发现未完成的 apply journal 且 immutable backup 包含宿主 exe 时，当前启动实例不会直接覆盖自己的映像；它会拉起独立 recovery worker，等待当前实例释放单实例互斥体后还原 backup、写入 `RollbackConfirmed` 并重拉宿主，旧版本启动收尾再删除 backup 与 journal。回滚失败时现场继续保留并由下一次启动重试。

应用准入、下次启动应用、apply worker 与 recovery worker 在切换前由配置模块只读检查 `data/` 内的会话标记及非空恢复工作区。当前或未知格式的任务选择 journal、编辑隔离、配置交换/快照事务均返回 `configuration-recovery-pending` 或保留启动事务并拒绝切换；不解析未知 journal，也不删除现场。重解析点及无法读取的现场同样阻断。配置恢复完成后重新检查即可继续，普通空目录和可重建的脚本工作区不永久阻断更新。

## Setup 元数据与空间

`InstallerMetadataCheckpoint` 新写 schema 3，记录 manager、应用根、可信快捷方式和独立工具的精确字节及原生 Setup 进程身份；schema 1/2 现场保留原 reader。活跃 Setup 期间普通 Host 启动和恢复拒绝介入，仅允许匹配同事务 AwaitingStartup 回执的已验证候选启动。预置前冻结备份，先记录可恢复的 Prepositioning，再复制完整 EXE/DAT/MSG 文件组。Inno 后观察实际 ARP 和卸载文件，凭当前应用身份与更新回执判定提交或回滚；只删除已确认属于本事务的字节，第三方变化保留现场并阻断。提交清理可以在下次恢复时继续。

`tools/installer-space.iss` 从构建时冻结的载荷清单计算核心与首次插件选择，并按目标、manager、临时卷合并新增占用，包含新 staging、真实旧资产复制、卸载预置、记录和已知下载。永久 helper 按独立 uninstaller 的真实长度计入 manager 卷，临时元数据程序单独计入临时卷。分配粒度使用实际卷信息，元数据余量随清单路径与记录数增长。预览复用已核对的保护身份，向导和互斥下最终检查共用原生计算；最终检查重新解封归属、检测依赖并读取各卷可用空间。ARP EstimatedSize 沿用 Inno 的实际安装计数，不能写入临时峰值。

准备数据副本前必须通过调用方持久回调保存 Preparing 状态、输入清单和预期工作区。独占创建目录后记录物理目录身份与 owner token，完成文件写入后更新精确清单，最终发布 Prepared。恢复核对原始 store 和已记录工作区；创建后尚未取得身份、转换中未入清单的字节或其他未知内容保留现场，不递归删除。该框架的受控强杀恢复测试不代表真实业务 schema 迁移已经登记或验收。

空间检查按实际卷的分配单位计入解压、重建、备份、worker、安装 helper 与 journal 余量；应用与每用户安装管理器跨卷时逐卷检查，空间不足在写入或交换之前拒绝。

手动覆盖维护暂停期间，若用户完整还原原登记版本的应用载荷，新 Setup 先复核原 DPAPI/HKCU 锚点和每个受管文件摘要，再通过可恢复的双 seal 检查点恢复旧维护状态。旧载荷未完整还原、未知 helper 或归属变化时保留暂停与现场。

旧 `NexusPipeline Update.exe` 只在来源清单证明其归属时加入 schema 4 的不可变移除收据。交换后不保留旧入口，回滚恢复原字节或原缺失状态；未知同名文件不认领。官方 v0.17.3 首跳使用 Setup 或全新 Portable；需要工具迁移的配方 `manualCoverSupported=false`，同版本自动发现继续拒绝。

## 安装提交与元数据补偿

Setup 的文件解压完成不等于安装成功。新装须完成实例归属登记；升级须等独立 worker 返回成功，且新 Host 启动核对及事务提交完成。登记或交接异常时完成页显示“安装未完成”，即使 Inno 已结束文件安装，进程仍返回非零退出码 12，并保留诊断和恢复现场。

Setup 在 Inno 写入 ARP 与原生 `unins*.exe/.dat/.msg` 之前保存当前用户实例的窄元数据检查点（DPAPI 状态、原字节备份和摘要）。写入后先观察 ARP 与原生卸载器，再启动独立 worker。未启动或确认回滚时，按检查点恢复原登记和原卸载器；事务与新程序身份均确认提交时保留新登记。提交后仅清理失败时仍按提交处理，Setup 继续返回失败供诊断。阶段不明、外部字节改动或补偿失败时保留检查点，拒绝覆盖；下次可信 Setup 会先恢复可判定的检查点。初装登记失败同样撤回 Inno 成功登记，程序与用户数据保留供诊断。测试入口及原始证据要求见[测试命令](../testing/commands.md)。

登记检查点调用 Setup 内嵌的 asInvoker 辅助程序，其入口限于安装元数据和旧实例后台服务交接。覆盖安装在写入 Inno 元数据之前核对当前用户归属与旧载荷哈希，确认该根目录记录的进程与本机控制端口已经就绪；未运行的旧实例通过 `runas` 启动后台服务，不打开管理页面。实际维护准入、任务结束与更新退出仍由旧服务执行。正式 Host 保持 `requireAdministrator`，独立更新 worker 仍由 `runas` 启动。worker 的授权请求被拒后，补偿不再发出第二次提权请求。安装元数据辅助程序不进入 Portable 或通用 Update；共同载荷包含可见 Update 入口。

删除数据卸载解除当前实例的归属登记和保护身份，Inno 在 helper 退出后按其已核验摘要移除该 helper。保留数据卸载继续绑定原根。仅有已卸载的残留登记、原根不存在或为空、管理目录不存在或为空且无 ARP 时，可安装到新的空目录或原空根；开始安装前先以当前用户 DPAPI 保全原登记，再解除这条失效登记。未知登记值、子键、文件、链接或有效身份不适用此恢复，安装数据和未知文件不会被接管。

卸载先检查当前确权清单中的全部应用路径，任一路径包含链接就整体拒绝，不能先删除其他应用文件再发现链接。管理身份核对使用本次安装器的临时结果文件，仅接受 DPAPI 解封、当前用户、目录和 helper 摘要全部核验后的状态；不明非空目录在静默模式下直接失败并记日志。内置更新刷新当前载荷确权清单，旧卸载器据此移除新程序，保留不在清单中的用户文件。

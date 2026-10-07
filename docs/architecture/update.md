# 自动更新

## 安装器交接和启动确认

Setup 使用本实例事务互斥体，将现役 Host 复制到同根 `.nxp/runtime/workers/update-<事务随机ID>.exe` 作为独立 worker，通过 `--app-root` 固定执行根目录；worker 摘要必须与现役 Host 一致。已有服务时通过本地 `installer-apply` API 取得现役维护租约；无服务时经同一 `UpdateService` 准入。配置恢复、运行、编辑和其他更新事务阻断切换。

交换完成先写 `AwaitingStartup`，保留 immutable backup。新宿主在维护租约下初始化插件和 Control API 后提交回执；worker 核对事务 ID、真实程序集版本、实际 EXE SHA256、PID、完整映像路径及启动时间，收尾完成后才返回成功。新宿主等待成功结果后解除维护，执行正常启动检查。回执失败时只按已捕获身份停止候选并回滚；无法确认停止或清理时保留 journal、备份和错误结果，不称更新完成。缺失 Phase、CreatedAt 或身份的旧 journal 保留原字节并阻断更新；当前事务的 CreatedAt 不刷新。worker 在备份前冻结自身身份和目标映像，失败证明与已退出 worker 对应后才清理本次暂存，避免重复启动失败事务。

## 实例归属和卸载

`InstallationOwnership` 管当前用户安装归属。随机 instance ID、规范根目录、用户 SID 和当前应用文件 SHA256 清单通过 DPAPI 保护，并与 HKCU 的摘要锚点核对；注册表路径本身不足以接管旧目录。辅助程序位于用户级管理目录，调用前也按当前受保护清单验证。

更新提交刷新当前载荷清单，回滚恢复清单及辅助程序。卸载先完成归属、链接和恢复现场检查，再删除当前清单中仍匹配 SHA256 的应用文件；未知或已修改文件保留。默认保留数据并记录 retained-data 状态，再安装同一路径需明确确认保留数据重用。明确删除数据只遍历本实例固定数据目录，拒绝链接和未解决恢复现场，不触及游戏、脚本或系统运行时。

## 更新检查与闲时应用

`UpdateAutomationService` 是宿主级单例协调器，负责运行期间的更新检查周期和闲时更新编排；启动前更新由 `StartupUpdateCoordinator` 在启动恢复收尾完成后、宿主服务初始化前处理。`UpdateService` 继续拥有清单、下载、SHA256 校验、staging、应用 journal、恢复和回滚状态机。定期检查开关开启时，运行期服务启动约 5 秒后首次检查，之后以自动检查完成时间为起点每 12 小时检查一次；人工检查不会重置自动周期。宿主和插件版本使用 `major.minor.patch`、`-beta.N` 或 `-rc.N` 的受限格式，比较顺序为 beta、rc、stable。

“闲时自动更新”默认关闭，且要求定期检查开关同时启用。两项开关均开启时，启动恢复完成后先检查宿主更新（检查最多 30 秒、下载最多 5 分钟）；可下载的新版本会在宿主服务初始化前暂存并应用，已有 `Ready` 暂存也会应用，然后由更新器重新启动程序。启动检查、下载或应用失败时继续运行当前版本；同一目标失败后冷却 12 小时，人工更新操作可重置冷却。策略屏障阻断下载。运行期间仍保留原有闲时更新：更新包可先下载并校验为 `Ready`，真正应用前必须通过 `AutoUpdateIdlePolicy` 取得 `HostMaintenanceLease`，再在同一准入协调域内确认没有活动运行、编辑会话、待执行系统操作、待准入/等待中的调度 occurrence、未触发的启动队列，并且未来 5 分钟内没有 scheduled occurrence。调度器的 occurrence 注册与队列变更共享该协调域，避免检查与维护租约之间的竞态。启动恢复无法证明文件安全时停止服务启动。

插件自动更新使用独立的 `PluginAutoUpdateEnabled` 开关。启动时在宿主更新恢复完成后、插件待处理事务应用及 `LoadAll` 前，检查并暂存所有符合官方 catalog、宿主兼容且有新版本的已安装插件，包括已禁用插件；手动安装的插件仅在 artifact 与 catalog 精确匹配时参与。检查最多 30 秒、下载最多 5 分钟，失败不会阻断当前插件加载。运行期每 12 小时检查并下载，在取得同一维护租约后安全重启以应用；插件启用偏好在更新后保留。

运行期自动化等待信息通过更新状态 API 和 MCP `get_update_status` 的 additive `automation` 投影提供，包括检查开关、上次/下次自动检查、是否等待闲时和阻止原因。渠道或更新源变化会使发现结果进入 pending invalidation；正在进行或已 Ready 的事务保留给当前人工处理，自动化不会应用已失效的发现结果。

更新检查发现候选版本后，`UpdateService` 通过固定更新源读取根目录 `update-policy.json`。策略必须通过 schema、仓库标识、版本顺序、屏障 code 和迁移 URL 校验；网络失败、响应超限或策略无效时保持发现结果并禁止内置下载。对当前版本到目标版本区间内的最早屏障，状态 API 和 MCP 返回 `manualUpdateRequired`、`updateBlockCode=breaking-update`、`barrierVersion` 与可选 `migrationUrl`，下载、启动前自动应用、下次启动应用和闲时自动应用均被拒绝。策略验证成功且未命中屏障时才允许下载；策略缓存只用于带 HTTP validator 的后续验证，网络失败不会授权旧缓存。

主程序更新事务固定交换四项应用资产：`NexusPipeline.exe`、`resources/desktop/`、`README.md` 和 `resources/payload-manifest.json`。Vue 以资源形式内嵌在 Host 中。用户 `plugins/`、`config/`、`data/`、`history/`、`logs/`、`.nxp/state/desktop/` 和未知同级文件均不参与交换；官方插件仓库不参与宿主自动更新流程。

停止桌面前，当前事务冻结 schema 1 的 `DesktopResumeIntent`，Mode 仅接受 `show` 或 `background`。尚未创建窗口的根启动 Show 请求也进入冻结意图；恢复不依赖历史 handoff 或被删除的 session。明确未启动 worker 的失败由同事务 Desktop owner 撤销停止状态并恢复该意图；已启动或无法确认的 worker 保留租约、journal 与停止状态。普通重启未启动子进程时只撤销匹配的 handoff，当前 renderer 恢复 ready 并保留草稿。

journal 记录 staging、backup 的文件和目录精确清单、每文件长度及 SHA256，以及 worker、原包和 checksum 侧文件摘要。清理先核验所有资源，再通过独占文件句柄复核和删除对应字节；目录仅允许非递归空目录删除。中断后可继续清理已缺失的已确认资源，新增、变形、链接或变化的未知字节保留现场。交换和回滚共用这些清单；已确认路径上的损坏候选先写保护记录并移入 `.nxp/state/updates/<事务>.preserved/`，随后恢复完整旧载荷。未知新增候选文件阻断回滚并保留 immutable backup 与 journal。

下载和解压只创建新文件，拒绝覆盖已有 staging。网络操作按实际写入的字节记录长度与摘要，并记录由本操作创建的目录；取消或失败先核验完整创建集合，出现未知子项或改变字节时保留现场。就绪后的暂存清单和 checksum 摘要沿用创建收据，申请应用时重新核验，不重新认领后来加入的内容。

候选下载及 staging 先按 SHA256、精确文件清单、规范构建身份、原始前端 index 摘要和 ASAR 内的身份记录执行只读校验，不运行候选 EXE。journal 冻结目标清单摘要、buildId、frontendHash、代际和原包摘要；交换前重新核对，交换后及启动回执提交前再次核对完整应用。每项资产交换进度单调落盘；回滚恢复同一冻结备份并核对旧载荷。桌面退出通知和已捕获的完整 Electron 家族退出证明必须先完成，文件锁或未知进程身份均保留现场并阻断替换。

v0.17.0 使用独立安装代际 `g0170`，不接管 `g01615` 的目录、注册表登记及管理辅助程序。`update-policy.json` 的 `0.17.0` 独立屏障要求旧版用户按新目录安装指南迁移；旧目录和配置原地保留。同路径 Setup 升级只接收当前代际、本 Windows 用户已登记且身份匹配的安装目录。文件归属清单包含 Host、桌面完整运行时及载荷清单自身；安装、更新和卸载使用同一应用边界。

候选构建对同一 production staging 生成 ZIP 和 Setup；`candidate.json` 固定列出两份资产、纯 SHA 侧文件和两份构建元数据。publisher 从原成功 run 下载服务端指定 artifact，核对来源、tree、依赖与编译器锁、四项分发资产的字节，再发布并远端逐项回读；不从发行页按 latest 重新取包。

启动恢复发现未完成的 apply journal 且 immutable backup 包含宿主 exe 时，当前启动实例不会直接覆盖自己的映像；它会拉起独立 recovery worker，等待当前实例释放单实例互斥体后还原 backup、写入 `RollbackConfirmed` 并重拉宿主，旧版本启动收尾再删除 backup 与 journal。回滚失败时现场继续保留并由下一次启动重试。

应用准入、下次启动应用、apply worker 与 recovery worker 在切换前由配置模块只读检查 `data/` 内的会话标记及非空恢复工作区。当前或未知格式的任务选择 journal、编辑隔离、配置交换/快照事务均返回 `configuration-recovery-pending` 或保留启动事务并拒绝切换；不解析未知 journal，也不删除现场。重解析点及无法读取的现场同样阻断。配置恢复完成后重新检查即可继续，普通空目录和可重建的脚本工作区不永久阻断更新。

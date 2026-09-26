# 自动更新

## 安装器交接和启动确认

Setup 使用本实例事务互斥体，将已核验的新 EXE 复制为独立 worker；正在被替换的原 EXE 不充当 worker。已有服务时通过本地 `installer-apply` API 取得现役维护租约；无服务时经同一 `UpdateService` 准入。配置恢复、运行、编辑和其他更新事务阻断切换。

交换完成先写 `AwaitingStartup`，保留 immutable backup。新宿主在维护租约下初始化插件和 Control API 后提交回执；worker 核对事务 ID、真实程序集版本、实际 EXE SHA256、PID、完整映像路径及启动时间，收尾完成后才返回成功。新宿主等待成功结果后解除维护，执行正常启动检查。回执失败时只按已捕获身份停止候选并回滚；无法确认停止或清理时保留 journal、备份和错误结果，不称更新完成。兼容旧 journal 缺少时间戳时，worker 在发出启动挑战前冻结一次时间。

## 实例归属和卸载

`InstallationOwnership` 管当前用户安装归属。随机 instance ID、规范根目录、用户 SID 和当前应用文件 SHA256 清单通过 DPAPI 保护，并与 HKCU 的摘要锚点核对；注册表路径本身不足以接管旧目录。辅助程序位于用户级管理目录，调用前也按当前受保护清单验证。

更新提交刷新当前载荷清单，回滚恢复清单及辅助程序。卸载先完成归属、链接和恢复现场检查，再删除当前清单中仍匹配 SHA256 的应用文件；未知或已修改文件保留。默认保留数据并记录 retained-data 状态，再安装同一路径需明确确认保留数据重用。明确删除数据只遍历本实例固定数据目录，拒绝链接和未解决恢复现场，不触及游戏、脚本或系统运行时。

## 更新检查与闲时应用

`UpdateAutomationService` 是宿主级单例协调器，负责运行期间的更新检查周期和闲时更新编排；启动前更新由 `StartupUpdateCoordinator` 在启动恢复收尾完成后、宿主服务初始化前处理。`UpdateService` 继续拥有清单、下载、SHA256 校验、staging、应用 journal、恢复和回滚状态机。定期检查开关开启时，运行期服务启动约 5 秒后首次检查，之后以自动检查完成时间为起点每 12 小时检查一次；人工检查不会重置自动周期。宿主和插件版本使用 `major.minor.patch`、`-beta.N` 或 `-rc.N` 的受限格式，比较顺序为 beta、rc、stable。

“闲时自动更新”默认关闭，且要求定期检查开关同时启用。两项开关均开启时，启动恢复完成后先检查宿主更新（检查最多 30 秒、下载最多 5 分钟）；可下载的新版本会在宿主服务初始化前暂存并应用，已有 `Ready` 暂存也会应用，然后由更新器重新启动程序。启动检查、下载或应用失败时继续运行当前版本；同一目标失败后冷却 12 小时，人工更新操作可重置冷却。策略屏障阻断下载。运行期间仍保留原有闲时更新：更新包可先下载并校验为 `Ready`，真正应用前必须通过 `AutoUpdateIdlePolicy` 取得 `HostMaintenanceLease`，再在同一准入协调域内确认没有活动运行、编辑会话、待执行系统操作、待准入/等待中的调度 occurrence、未触发的启动队列，并且未来 5 分钟内没有 scheduled occurrence。调度器的 occurrence 注册与队列变更共享该协调域，避免检查与维护租约之间的竞态。启动恢复无法证明文件安全时停止服务启动。

插件自动更新使用独立的 `PluginAutoUpdateEnabled` 开关。启动时在宿主更新恢复完成后、插件待处理事务应用及 `LoadAll` 前，检查并暂存所有符合官方 catalog、宿主兼容且有新版本的已安装插件，包括已禁用插件；手动安装的插件仅在 artifact 与 catalog 精确匹配时参与。检查最多 30 秒、下载最多 5 分钟，失败不会阻断当前插件加载。运行期每 12 小时检查并下载，在取得同一维护租约后安全重启以应用；插件启用偏好在更新后保留。

运行期自动化等待信息通过更新状态 API 和 MCP `get_update_status` 的 additive `automation` 投影提供，包括检查开关、上次/下次自动检查、是否等待闲时和阻止原因。渠道或更新源变化会使发现结果进入 pending invalidation；正在进行或已 Ready 的事务保留给当前人工处理，自动化不会应用已失效的发现结果。

更新检查发现候选版本后，`UpdateService` 通过固定更新源读取根目录 `update-policy.json`。策略必须通过 schema、仓库标识、版本顺序、屏障 code 和迁移 URL 校验；网络失败、响应超限或策略无效时保持发现结果并禁止内置下载。对当前版本到目标版本区间内的最早屏障，状态 API 和 MCP 返回 `manualUpdateRequired`、`updateBlockCode=breaking-update`、`barrierVersion` 与可选 `migrationUrl`，下载、启动前自动应用、下次启动应用和闲时自动应用均被拒绝。策略验证成功且未命中屏障时才允许下载；策略缓存只用于带 HTTP validator 的后续验证，网络失败不会授权旧缓存。

主程序更新事务交换 `nexus-pipeline.exe`、`wwwroot/`，若新包含 `README.md` 也交换该应用资产；用户 `plugins/`、`config/`、`data/`、`history/` 和 `logs/` 保持不变；官方插件仓库不参与宿主自动更新流程。`update-policy.json` 的屏障记录在破坏性布局发布前按版本递增追加，启动时按当前版本与目标版本区间执行策略检查。

已发布 v0.16.8 的 worker 只交换 EXE 和 `wwwroot/`。v0.16.9 首次启动收尾会在已提交的同版本 journal 和 staging 存在时，仅对缺失的 `README.md` 创建文件；若用户目录已有 README，则保留，绝不覆盖未知修改。安装器同路径升级只接收本 Windows 用户已登记且身份匹配的安装目录，应用文件先按冻结 SHA256 写入 `.nxp-update/staging/`，再调用现有 `apply-update` worker；不明便携目录必须走内置更新或手动替换指导。

候选构建对同一 production staging 生成 ZIP 和 Setup；`candidate.json` 固定列出两份资产、纯 SHA 侧文件和两份构建元数据。publisher 从原成功 run 下载服务端指定 artifact，核对来源、tree、依赖与编译器锁、四项分发资产的字节，再发布并远端逐项回读；不从发行页按 latest 重新取包。当前未授权创建 tag 或发布，源码中的链路不表示已有 v0.16.9 候选。

启动恢复发现未完成的 apply journal 且 immutable backup 包含宿主 exe 时，当前启动实例不会直接覆盖自己的映像；它会拉起独立 recovery worker，等待当前实例释放单实例互斥体后还原 backup、写入 `RollbackConfirmed` 并重拉宿主，旧版本启动收尾再删除 backup 与 journal。回滚失败时现场继续保留并由下一次启动重试。

应用准入、下次启动应用、apply worker 与 recovery worker 在切换前由配置模块只读检查 `data/` 内的会话标记及非空恢复工作区。当前或未知格式的任务选择 journal、编辑隔离、配置交换/快照事务均返回 `configuration-recovery-pending` 或保留启动事务并拒绝切换；不解析未知 journal，也不删除现场。重解析点及无法读取的现场同样阻断。配置恢复完成后重新检查即可继续，普通空目录和可重建的脚本工作区不永久阻断更新。

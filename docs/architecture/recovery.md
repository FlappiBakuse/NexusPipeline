# 恢复与现场保全

## 可识别现场与崩溃恢复

`ConfigRecoveryService.SnapshotIsolation` 只读投影当前 journal，不另建隔离数据库。完整的恢复失败范围包括原 config、extraConfigPaths、working directory 与 writable root；不可读、旧字段缺失或范围无法确认时保持保守阻断。`RecoveryIsolation` 保存起因、运行／记录／尝试身份、范围质量和是否可继续独立项，配置恢复成功后随原 journal 一起解除。

provider 使用同一 `.session`／冗余 `.session.bak`，`SessionPhase=provider_run` 不交换项目配置；它记录已授权项目根和 Host 的必要进程停止确认。`ProviderWorkersStopped` 在同一个完整 journal 身份中只从 false 推进为 true。冗余副本已经保存停止确认但主文件替换被锁时，读者仅在其余全部会话字段相同的条件下接受该停止证明；另一执行的副本不能覆盖主文件。清理失败保留局部恢复隔离。启动恢复不能把未确认停止的 provider journal 直接清空。

队列后继和独立运行读取同一投影与租约准入；受阻项记录未执行并继续检查独立项。未知桌面输入／共享设备服务或写入范围不能自行放行。未解决隔离期间自动完成操作和自更新受原恢复门禁保护。手动维护仍需明确操作，不能通过删除 journal 清除警告。

- 附加快照事务在启动恢复和运行收尾前先处理未提交的 manifest；已提交事务只清理残留，未提交事务恢复旧 store。manifest 缺失、身份不匹配或现场状态无法判定时保留事务目录并阻断后续写入，等待人工处理。

- **启动恢复（RecoverInterrupted）**：扫描当前格式的 `.session` 标记、edit-isolation 与 swap-backup，自动还原；格式完整且原配置区为空时，fresh 编辑会话（原形态 Missing，config 位置为脚本生成物）由 `EditMode` 驱动 `DoRestore` 清理，其余会话只清除标记并保留未改变的现场。字段缺失、字段名不符或协议未知的现场保留并告警，等待人工处理。fresh 输入提交阶段使用 `edit-commit-pending` 标记，快照与现场恢复完成后再写入用户绑定。
- **后台延迟重试**：还原失败（文件被孤儿进程占用）时进入待办队列，每 10 秒重试直至成功或进程退出。
- 可自动恢复的前提是当前协议的 `.session`/manifest、归属身份和有效副本均可验证。移动配置前后由 `original` 保存原现场；字段缺失、身份不匹配、备份损坏或现场归属无法判断时，恢复器保留现场并阻断相关写入，交由人工处理。
- **Missing 形态还原**：`DoRestore` 在 original 为空且原形态为 Missing（运行/编辑前 config 位置不存在）时，删除会话期间在 config 位置产生的文件/目录，恢复为“不存在”；删除失败则保留标记交由自愈/后台重试。
- **收尾顺序**：运行收尾固定为「杀脚本进程并确认退出 → 按设置处理游戏进程 → 配置交换还原」，确保还原前进程已完全退出。

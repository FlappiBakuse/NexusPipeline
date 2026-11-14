# Windows 安装与维护生命周期

Inno 负责 Setup 的先决条件、原生安装登记和卸载交互。`apps/installer` 的 worker 调用 Platform Installation 处理应用载荷、实例归属和恢复。Host 负责签名更新的事务和重启交接。 [发布成品](release.md#setup-打包) · [验证入口](validation.md#发布产物与工具链验证)

## Setup 的运行时准入

Setup 在调用安装 worker 或写入应用前，检查 x64 注册位置或标准目录中的共享框架。它从将使用的 ASP.NET Core 的 runtimeconfig 读取所需的 Core 补丁下限，在同一安装根验证组合。满足要求时直接复用。补齐后重新检查组合，所需补丁高于锁定安装包时提示确切下限并停止准备。

交互安装在用户确认后，从锁定的官方来源下载，核对 SHA-256 和微软签名。签名验证器显式加载 Windows PowerShell 自带的安全模块。静默缺失、取消、校验或安装失败均在应用写入前停止。依赖安装返回 3010 时要求重启后重试。卸载 NexusPipeline 保留系统共享运行时。 [运行时准入实现](../../tooling/installer-runtime.iss)

## 新装与保留数据后重装

`check-new` 只接受空目标。`check-install` 接受空目标或通过只读数据库及恢复准入的保留实例。活动的同代安装进入覆盖事务。

写入前取得原生启动门禁。worker 校验固定摘要的目标清单，冻结目标文件集合、实际父 Setup、writer、目录身份及原生登记。`register` 核对完整应用、实际 bundle/profile、卸载登记与 runtime，再发布安装归属和私有 worker。保留实例的重装沿用安装 ID、创建时间、runtime ID 和目录身份。

部分发布只续接已记录的对象与注册表值，旧归属保全到提交完成。`cancel-prepared` 清理仍符合原证据且尚未写入应用的检查点。首次捕获快捷方式时以 Windows Shell Link 只读核对目标、空参数、摘要及对象身份，接续时复核原字节、文件和父目录身份。Test 入口和 Setup 共用显式 fixture、隔离注册表及快捷方式目录。

新装 register 接收安装器临时区的 `--initial-extensions`，在原 Setup 与实例租约内完成默认插件初始化后发布安装归属。
初始化原包先按摘要进入内容存储，中断恢复不依赖安装临时目录仍在；现有实例重装不重新初始化插件。

## 同代覆盖与中断恢复

覆盖准备在启动门禁、实例独占和原应用读取租约内核对原清单、程序 profile、物理身份、原生登记及完整恢复准入。事务冻结原安装和完整目标，重入核对两者及 worker 原字节。保全前检查暂存与安装卷的可用空间，同卷预留两份展开载荷，跨卷各预留一份。

原应用的两个入口、README 与 resources 保全至同一安装根内的事务目录，文件和目录保留物理身份。原生 Setup 将新载荷写入与本次 worker 同级的暂存树。worker 在源读取租约内按冻结清单核对成员、摘要、大小、profile 和物理对象，再逐文件复制到安装卷、落盘复核并冻结目标。事务交换四个应用根及发布安装登记，复用原 runtime、快捷方式和配对的卸载引擎。

Setup 在目的地检查前尝试恢复。恢复者取得安装门禁，核对原 worker、旧 Setup、全部 writer 的停止证据及 runtime 准入。未提交事务按已知移动前缀恢复原应用和登记，已观察提交的事务只核验目标并续做清理。旧进程存活、身份或来源不匹配及未知对象均保留检查点与现场。

## 更新窗口与 Host 交接

无参数打开 `NexusPipeline.update.exe` 显示版本、检查、下载进度、取消和应用后重启。窗口绑定同源 Host 的发布摘要。Host 已停止时，由已核验的 Host 映像组合受限维护会话。下载取消等待本次暂存清理完成。

交互应用等待实际窗口退出，再从签名候选暂存已核验的 Host 映像，以 `maintenance-apply` 接管事务。Job、writer 与管道绑定到该进程。交换前核对旧 Host 与 writer 的停止证据。更新保持安装根身份，交换两个入口、README 与 resources，并保留 runtime、已安装插件和其他根目录材料。

已安装版更新的准备阶段冻结安装归属、原生登记和文件身份。新 Host 通过启动交接后，提交回执冻结新归属，再发布清单摘要、版本和原生登记。Portable 更新只发布应用载荷。已记录提交按冻结对象续做，启动失败按原归属恢复旧载荷并清理已确权的暂存。维护诊断记录在 `runtime/diagnostics`，包含阶段、拒绝码、HRESULT 和调用位置。 [进程归属](process-ownership.md)

## 卸载与数据选择

Setup 在安装根和开始菜单提供 `NexusPipeline.uninstall.exe` 入口。它通过已绑定的 worker 核验实例归属并打开登记的 Inno 卸载器。原生引擎独占维护卸载日志，worker 负责实例租约、数据及安装归属，并通过持久化设置意图移除所拥有的开机启动任务。

交互卸载支持取消、默认保留和显式删除。静默卸载默认保留，已确认的待删除意图按原选择续做。删除前先通过完整只读恢复准入并冻结 runtime 的内容与物理身份，同卷退役后只删除匹配对象。删除意图及实际 writer 交接支持中断续做，并阻止 Host 重建 runtime。原生卸载完成后清除待删除的安装归属。正常 Host 在安装门禁、保留状态或待恢复检查点下拒绝启动。

## 安装 worker 的内部命令

Setup 和登记的卸载引擎按阶段调用 worker，诊断可使用其中的只读入口。写入操作依赖当前受保护格式和实际进程归属。`--work-root` 指向应用、runtime 和安装管理目录之外的隔离诊断目录。

```text
installer-state inspect|check-new|check-install --root <目录>
installer-state check-transition --root <目录> --work-root <诊断目录>
installer-state begin --root <目录> --transaction <GUID> --manifest <SHA256> --manifest-file <目标清单> [--work-root <诊断目录>]
installer-state register --root <目录> --transaction <GUID> [--initial-extensions <安装临时目录>]
installer-state cancel-prepared --root <目录> --transaction <GUID>
installer-state begin-transition --root <目录> --transaction <GUID> --manifest <SHA256> --manifest-file <目标清单> --work-root <诊断目录>
installer-state preserve-transition|cancel-transition --root <目录> --transaction <GUID> --work-root <诊断目录>
installer-state apply-transition --root <目录> --transaction <GUID> --payload-root <Setup 暂存树> --work-root <诊断目录>
installer-state recover-transition --root <目录> --work-root <诊断目录>
installer-state uninstall-check --root <目录>
installer-state uninstall-keep|uninstall-delete --root <目录> --transaction <GUID> --work-root <诊断目录>
installer-state uninstall-finish --root <目录> --transaction <GUID>
```

`check-transition` 只读返回已核对来源。`uninstall-check` 返回门禁名及 `choose` 或 `delete` 状态。三个卸载写入口均核对实际父进程、冻结 worker 与原生引擎，`uninstall-finish` 还要求原生移除已完成。 [worker 入口](../../apps/installer/InstallerEntry.cs) · [Inno 调用](../../tooling/installer-lifecycle.iss)
## 已提交插件事务的工作区

已提交插件旧树由服务就绪后的分页清理回收，当前载荷、活跃 scope、未决事务与未知对象保留。
安装/移除回执在工作区回收后仍可读取；分页、中断和删除证明统一见[扩展清理](extensions.md#废弃工作目录清理)。

## 历史插件 ZIP 退役

历史 ZIP 只从已知提交来源选取，保留现役安装、回滚、执行及共享引用。
候选、持有期与回执重放规则见[历史包退役](extensions.md#历史包与展开内容退役)。

## 插件展开内容与包输入

插件从持续受保护的已验证 ZIP 直接物化候选树，展开成员不另写 CAS 副本。
配置交换和宿主更新保留各自内容来源；完整包输入与交接规则见[下载与包验证](extensions.md#独占下载与包验证)。

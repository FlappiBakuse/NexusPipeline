# 受控执行 provider

provider 的授权来自已核验的包和当前 extension activation。
`IExecutionRegistrationPort.RegisterAsync` 不接收调用方提供的命令、角色或权限声明。
manifest 的 `execution` 描述入口、包内 worker、参数及可选的配置资源；
它必须声明 `execution.drive` 和 `owned-worker`、`selected-upstream` 进程权限。
注册只有在原 `ExtensionScope` 发布后可见；scope 撤销阻止新的获取、冻结和启动。
撤销不会解除原已启动进程族的停止与输出收集义务。

data-specialized 消费方通过 `specialized.executionProvider(extensionId,entryId)` 明确引用共享执行提供者。
编译器关联只用于来源编译，不授予执行权限。冻结步骤的 extensionId 保留消费方身份，
provider.extensionId 与入口、安装锁、包归档和激活分别绑定真正执行的提供者。
双方及独立编译器的引用均进入受理资源集合；控制通道和 launcher 继续绑定实际提供者。
共享 worker 配合专项 adapter 时，账号配置仍由专项资源契约校验；此组合不接受提供者另行声明配置资源覆盖同一目标。

当前受控包入口核验 ZIP 与部署目录的完整文件集合、内容摘要及物理身份，
拒绝链接、额外文件、缺失文件和包外 runtime 文件。
它用于本机受控包接入；生产签名信任与安装生命周期由扩展安装 owner 提供。
总计划保存原 manifest、包归档、activation、worker、上游镜像、参数和 runtime 文件身份。
输入准备持有原文件租约，历史读取沿用不可变引用，不依赖当前部署目录或 activation 存活。

worker 复用主步骤的实际 Native Job 与配置 controller。
根进程暂停创建、完整身份先持久登记、随后 Resume；配置父子作用域共享真实进程族，分别保存停止闭包。
退出码、worker terminal、业务判定、生命周期和配置恢复事实分别保存。
退出码零不能补出缺失的 terminal，terminal 成功也不能补出业务成功或完整停止证明。

## 控制通道

stdin 传递一次 `nexus.execution.provider-bootstrap`/1；stdout/stderr 保留为原始输出。
worker 使用 SDK 的 `ExecutionProviderChannel` 连接 bootstrap 指定的本机管道。
Host 核对实际管道客户端 PID 与本次持有的根进程身份，确认准入后才发送 ACK。
SDK 自动发送首条 `started`，后续 `progress` 与首个 `terminal` 使用连续序号。
每条消息绑定实例、操作、父 journal、run、attempt、步骤、入口、activation、计划摘要及随机 nonce。
另一 attempt、nonce、入口、序号或超长消息不能发布 terminal。

```csharp
await using var channel = await ExecutionProviderChannel.ConnectAsync(
    Console.OpenStandardInput(), cancellationToken);
await channel.PublishProgressAsync("running", cancellationToken);
await channel.PublishTerminalAsync("succeeded", "provider_completed", cancellationToken);
```

bootstrap 与控制帧记录各有 2 MiB 上限，单条事件至多 16 KiB，最多 10002 条事件。
进度记录预留最后 terminal 的字节与序号；计划冻结前按同一编码规则检查 bootstrap 大小。
原始输出仍使用绑定级共享额度，控制帧不能作为 stdout reader 或业务判定的替身。

terminal 通过原 Native owner 的 opaque proof、同一实例发布队列及最新 SQL 分派门发布。
取消先受理时，后续 terminal 只能保留在诊断记录中；已发布 terminal 不被稍后的取消改写。
物理 journal 领先 SQL 时只对账原不可变前缀。
SQL 发布失败不改变通道消息的身份，显式收集阶段仍须核验原证明并完成对账；持续失败不能生成成功收据。

`execution.gracefulStop=true` 声明当前 worker 接收同一认证通道的停止帧。
SDK 的 `Stopping` 在 Host 请求停止或原通道失联时取消；worker 应关闭新输入、停止 native 任务并排空回调及 Agent，随后退出。
Host 仅对已冻结且与原 manifest 相符的声明提供协作窗口，最长为停止预算的一半且不超过十秒。
协作等待消耗原停止预算，之后仍由同一 Native owner 停止并核验完整进程族。
声明、取消 token 或 worker 自报清理均不能代替这个证明；未声明的 worker 直接沿用原停止流程。

## 冻结项目读取

已受理步骤含有编译快照时，bootstrap 的可选 `project` 绑定该快照 SHA 和独立本机管道。
`ExecutionProjectReader` 只读取原计划中的完整资产及资源树；不重新编译，也不接受任意磁盘路径或 blob 地址。
Host 在 ACK 和每次请求前后核对实际管道客户端属于本次仍存活的根 worker、原 Native owner、当前提供者激活及实例租约。
同进程族中的另一个进程也不能借用此通道；terminal 后关闭新的读取准入。

manifest 返回执行 view、根资产名和资源树元数据；资产与树项每页最多16项。
内容按精确资产名，或树名加原相对路径读取，每块最多128 KiB；返回完整文件大小、SHA 和原始字节的 Base64。
请求绑定快照指纹及连续序号，最多100000次、累计内容4 GiB，并受本次运行期限约束。
Host 首次打开内容时验证完整摘要，保持最多32个只读文件句柄；关闭通道不提交配置或制造业务成功。
适配器可为任务声明 `executionTaskId`，将业务 ID 绑定到编译器的独立 occurrence；
Host 冻结并校验映射唯一性，从实际选中任务产生同序的 `selection.executionTaskIds`。
worker 不从显示名、Entry 或当前配置猜测映射；未声明时沿用该任务自己的 ID。
该通道本身不授予目标输入、原生输出目录或 Agent 启动权限。

## Worker 工作目录

带冻结项目的执行由 Host 在当前 runtime 的 `work/<nonce的128位前缀>`
独占创建根及 `inputs`、`outputs`，bootstrap 绑定三者原路径、卷号和文件身份。
Host 持有祖先及三个目录租约；SDK 仅从已认证的 `ExecutionProviderChannel.OpenWorkspace()`
借用并验证原物理身份。链接、替换目录或重复借用均拒绝。
分配前校验输出 CWD 的 Windows 路径预算；nonce 独占创建，原控制记录仍绑定完整执行身份。

worker 在每次文件操作前检查借用与停止信号。输入物化和 native 输出分开，不能把原脚本
目录当临时目录。释放只关闭句柄，保留原字节；终结证据重读校验原 grant，不重新打开
当前可变路径。工作目录本身不构成 native 输出已收集或可以恢复配置的证明。

完整 writer 进程族已停止后，Host 才归档原 outputs，最多256项/64MiB，拒绝链接和多重
硬链接。独立输出 artifact 绑定原控制记录、目录身份与 Job closure，进入 collected
证据及 GC 引用链；重读只使用不可变归档，不打开当前工作目录。

## 配置与恢复

`execution.configurationResources` 为每个配置声明稳定的 resourceId、声明序号和定义修订。
专项配置契约绑定 extension、入口及资源声明，同地址的通用快照不能被借用。
预览、冻结、受理及运行配置准备使用同一声明 owner，核对当前修订、归属和完整引用。
bootstrap 仅传递冻结计划内的配置目标。
配置概览和本地导入读取当前已发布 provider 的已验证资源声明，要求声明序号完整覆盖绑定脚本的配置路径。
导入复用配置批次、路径独占、快照预算与修订 CAS；已有快照须匹配 provider/入口/资源契约，发布前重验激活权限。已接受操作的同主体重放仍返回原结果，不重新读取现场。
输出快照、差异同步和原现场还原复用配置交换 owner；全部必要 writer 停止前不能还原。

重启不会重放未知的 worker 副作用。
原 Job 的完整停止证明缺失时，即使根进程已死，也拒绝可写启动并保留现场。
已有完整持久收集证明时，恢复沿用原事实与输出，保留 Interrupted，再完成配置归还。
控制帧、bootstrap、原始输出和配置输出通过同一引用发现链受 GC 保护；
引用发现不重新打开当前 worker、部署目录或待删除的原始 content。

## 目标控制通道

`execution.targetControl=true` 同时要求 `gracefulStop=true` 和实际冻结的目标。bootstrap 中的
device grant 只指向本次 nonce 对应的本机管道；连接及每条请求均核对原 worker 根进程，
它的子进程也不能借用该权限。请求严格校验所需字段和连续序号，拒绝混入其他动作参数。

`ExecutionDeviceClient` 取得原尺寸 PNG，分块读取均绑定新 frameId、长度和 SHA；每连接只
保留一帧，最多100000条请求和4GiB传输。捕获复用已有 target owner、独立只读预览域、
受管 helper 及完整停止证明，未经过 UI 的360像素展示转换。媒体订阅和 worker 捕获共用
同一物理窗口的有界借用，每次动作仍重验原进程、窗口、尺寸及执行预算。

当前 PC 后端支持点击、拖动、单触点、键盘、文本和滚轮。按下状态由原借用持有，完成或
取消时只释放本会话持有的键和触点，随后关闭捕获域与目标。PC 应用启动和 shell
尚未接入。截图方法可选 PrintWindow、GDI、ScreenDC、FramePool；鼠标、键盘分别可选
SendMessage、PostMessage、SendMessageWithCursorPos 或 Seize。`configure` 必须在首个设备操作前
完成，随后仅接受相同选择。ScreenDC 由原目标输入 owner 提升所选窗口；只读 helper 在捕获前后
核对客户区位置、可见范围和遮挡情况，拒绝捕获其他窗口的画面。FramePool 从同一窗口的合成帧
裁出客户区，并检查捕获前后尺寸与位置。桌面合成圆角保留在 ScreenDC、FramePool 图像中。
Seize 验证目标已取得前台焦点后发送原生输入，绝对移动映射整个虚拟桌面；按键与按钮释放
不依赖目标仍处于前台。带光标定位的输入在手势结束时恢复原位置，用户另行移动光标时保留
用户位置。提供 PC 设备通道的执行占用桌面互斥资源，避免不同目标同时争用焦点和光标。
该通道供已受理的执行 worker 使用，不增加浏览器远程控制入口。

Android 选择 `adb` 截图、鼠标和键盘方法，使用冻结的通用 ADB endpoint 或 MuMu index。
支持连接、设备状态、点击、滑动、完整按键、ASCII 文本、有限时 shell，以及仅针对冻结
package 的启动和停止。文本不接受百分号；持续按键、触点、滚轮及其他输入后端返回不支持。
截图沿用原始 PNG 捕获 owner。输入命令作为原 worker Native Job 内的受管子进程执行，
启动时核验工具身份，完成、超时或取消均证明整个命令进程族停止后才归还控制权。
单个输出流上限64KiB，会话输出总量上限64MiB；输出超限或设备映射变化关闭通道，不重放命令。
MuMu 每条命令前后核验原 index、endpoint 和在线状态；设备输入不借用只读预览权限。

## 独立 launcher

可选的 `execution.launcher` 只能来自同一已验证包，并声明不写受管配置、使用 worker terminal 协议。
Host 在配置安装后、主 worker 启动前，为它建立独立 Native Job；launcher 的启动事实不替代主 worker 的业务判定。
只有主 worker 的持久 terminal 与全部必要 writer 的完整停止、输出收集均已确认后，才允许实际转交该 Job 的生命周期。
后置钩子仍须停止并完成收集，随后才可还原配置。

保留生命周期只延续原计划内精确的包和扩展 Reference 子集，不降级或延长 Mutation 授权。
原持有 Job 证明完整进程族结束后，Host 先发布独立的不可变结束记录及 SQL 索引，再释放引用；不追加已提交的父 journal。
重启只从已保存、已核验的保留意图恢复精确引用，不重新枚举部署目录。
缺少原 Job 的完整结束证明时继续保留引用，根进程死亡或同名 Job 的状态不能生成结束记录。
交接已生效但缺少持久保留证明、且原 Job 不可验证时，保持配置与相关资源隔离，不重放外部效果。

专项组 `storage-provider` 覆盖受控包、冻结输入、实际 worker、取消、SQL 故障、配置还原、强杀恢复及 GC。
实际原生报告和完整后端收尾分别登记；独立 SDK 消费者及四个插件纵切不代表全部官方插件完成。

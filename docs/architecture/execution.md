# 执行与资源生命周期

## 核心运行流程

### 独立执行 provider

`ExecutionPlanBuilder` 经当前启用插件的 `PluginExecutionProviderRegistry` 读取并冻结 provider 计划，`ProviderPlanPolicy` 校验身份、顺序、授权指纹、资源与字节预算。它复用相同的 `ExecutionAdmissionProfile`、队列／用户集合、资源租约、`ExecutionRunner`、历史和 `ConfigRunSession`。具体扩展与 worker wire 契约见 [执行 provider](../reference/plugin-api/execution-provider.md)。不再为 provider 启动专项主程序或执行旧 judge；两条入口由脚本的显式 provider ID 区分。

Host 负责游戏启动时，在现有启动就绪流程之后传递完整映像、PID、创建时间和可见窗口。插件在同一进程身份内复核自己的控制器选择；它不能凭进程名选择另一安装目录的目标。worker 通过专用 IPC 报送引擎事件，`ProviderTaskProjection` 写入同一个任务报告。原生任务成功没有独立业务证据时为 `unverified`，不计入每日业务成功上限。

### 进程观测与保留启动器

`ProcessObservation` 保存 complete／partial／unavailable、原始 PID、可确权身份、不可读身份与原生错误。Job 查询扩容有上限，查询失败不是空集合；旧身份不因单次读取失败消失。安全结束与清理快路要求实际归属成功和完整必要进程观测。

专项冻结启动声明只在明确的纯 launcher 契约、无接管配置 writer 职责且根进程完整身份匹配时保留该根。声明 `writesManagedConfig: true` 的 launcher 按必要 writer 处理。纯 launcher 仍在原 Job 内，不启用 kill-on-close 或为清空 Job 全杀。必要 automation 子进程仍阻塞结束和恢复；纯 launcher 驻留不改变已有业务结果。初始空 Job 不能提前判启动完成，需可信协议边界或已观察到必要子进程结束。OK 单实例启动还复核精确安装目录中的解释器活动；转交给预存 GUI 的外部 worker 活跃或不可确认时保持相关隔离。

游戏收尾只使用 Host 本轮启动时捕获的身份，或本次脚本 Job 中匹配完整游戏映像的身份。PID、创建时间及映像在停止时再次核对；预存进程、不同安装目录的同名程序和无法确权的外部实例保留。必要脚本清理按完整映像排除需保留游戏；缺少 Job 时的名称快照不足以确认该排除，返回未确认并保留隔离。窗口最小化轮询使用异步延迟，精确目标退出即结束，不在线程池中堆积 30 秒同步等待。

可信自动化边界后有界等待 stdout／stderr EOF，未 EOF 标记 `OutputIncomplete` 并保留已有事实。它不会凭尾流缺失补成功，也不因纯伴随进程保留而覆写已核验业务结果。

### 四维结果与队列继续

`RunOutcomeProjector` 分开持久化 engineStatus、businessVerification、executionOutcome 和 recoveryOutcome。恢复故障保留已观察业务事实，并附隔离警告；旧历史缺少这些字段时保守投影。整队人工取消先冻结派发／重试，停止本轮必要进程、收拢 Host 写入 worker，再恢复或保留隔离；计时使用单调时钟与显式阶段，不解析展示文案。

局部恢复故障由原 journal 投影到受影响资源；后继逐项按既有准入检查。共享资源项记为未执行，有显式失败依赖的项同样未执行，然后继续检查后面的独立项；不自动回头补跑。活动独立运行不被一项故障取消。未解决恢复时抑制会中断恢复的自动系统动作和宿主自更新；人工整队取消停止全部后继。

专项任务的每次主执行尝试在前置脚本之后再次检查当前配置。若首次检查阻断，任务报告没有执行尝试；若第一次已实际执行、准备重试也已通过，但下一次检查才阻断，历史保留第一次的任务事实和最终业务状态，并附上本次准入停止信息。被阻断的轮次没有第二次主程序执行；该轮若运行了用户前置脚本，其日志与停止记录仍保留。



### 脚本运行完整链路

一次「脚本实例 × 用户」的运行由 `ExecutionRunner` 驱动。入口先由 `ExecutionPlanBuilder` 从仓储快照构建计划，再经 `ExecutionValidator` 完成运行前校验；`DispatchCenter` 将计划 profile 交给 `ExecutionStateStore`，由 `ExecutionAdmissionPolicy` 在同一临界区完成资格矩阵、资源租约和完成操作兼容性判断。通过后由 `ExecutionCoordinator.RunAsync` 编排，队列、手动和 CLI 入口均直接汇聚到 `DispatchCenter`；`RunSession` 只保存状态，单次尝试由协调器直接执行：

```mermaid
sequenceDiagram
    participant DC as DispatchCenter
    participant P as ExecutionPlanBuilder
    participant V as ExecutionValidator
    participant Q as ExecutionAdmissionPolicy
    participant E as ExecutionStateStore
    participant R as ExecutionRunner
    participant S as ExecutionCoordinator.RunAsync
    participant M as LogMonitor
    participant J as SessionJudge/判断脚本

    DC->>DC: StartScript / StartQueue / Cancel
    DC->>P: 读取仓储快照并构建冻结计划
    P->>V: 执行计划前置校验
    P-->>DC: 返回计划与 AdmissionProfile
    DC->>Q: 比较资格矩阵/资源/完成操作
    Q->>E: 在同一临界区检查并登记活动运行
    E-->>DC: 接受或返回准入失败码
    DC->>R: 启动后台任务
    R->>S: 编排该用户运行
    S->>S: 在脚本级门禁内读取当天成功次数
    alt 达到绑定的每日成功上限
        S-->>R: 写入 skipped 历史（0 次尝试），不发布用户运行开始事件
    else 未达到上限
    loop 尝试 1..MaxAttempts
        S->>S: 执行本次尝试
        S->>S: 前置脚本、游戏/脚本启动、日志监控、判定和清理
        S->>S: 启动游戏（可选，在 GameWaitSeconds 内确认目标就绪）
        S->>S: 启动本次拥有的主程序并记录进程身份
        S->>M: 解析日志路径 → 创建监控（严格 fresh：本次尝试写过才从头读，否则末尾读忽略残留）
        loop 1 秒间隔
            M->>M: 解析路径/FileId 替换/截断检测 → ReadNew 读新增
            S->>J: 逐行 HandleLine（关键字）
            S->>J: 判断脚本批次/周期/最终触发
            J-->>S: success/partial/failed/replaceConfigs
        end
        S->>S: 判定成功/部分完成/失败/超时/取消 → 杀进程树 → 按结果处理游戏
        S->>S: 后置脚本（用户配置，可选）→ 记录尝试结果
        alt 成功或部分完成或达到最大次数
            S-->>DC: 返回 RunRecord
        else 失败且未达上限
            S->>S: 进程确认退出后复用当前 config → 应用 replaceConfigs → 下一次尝试
        end
    end
    end
    S->>S: 还原替换配置 → 清空脚本区 → 配置交换还原现场
    R->>R: 历史落盘（.json 纯状态 + 按尝试分批日志）→ 通知分发
    R->>E: 提交完成意图并释放资源租约
    E-->>E: 活动运行数为 0 时原子预留 pending 系统操作
```

**分步细节（ExecutionCoordinator 单次尝试流程内）：**

1. **前置检查**：其他运行遗留的同路径进程不能只凭名称或路径推定为本次拥有；本次尝试的进程树以已记录的身份与 Job 为清理依据。无法确认归属时拒绝误杀，保留诊断现场。
2. **启动游戏（可选）**：`LaunchGame=true` 且已填游戏路径时，校验可执行。PC 模式以可见目标窗口确认，cloud 模式以目标进程确认；已就绪时不重复启动，否则在 `GameWaitSeconds` 上限内轮询。命令文件启动目前仍使用有界等待，不能视为已确认窗口就绪。未填写路径则跳过并提示。
3. **启动主程序**：`ResolveLaunchTarget` 解析运行时启动目标（Args 以显式路径开头时=管理端/执行端分离场景，`?` 后为参数）→ CreateProcess 重定向 stdio（无窗口）→ bat 自动 `cmd /d /s /c` 包装（规避 0x800700E8）→ 740（要求管理员）明确报错、禁止降级提权。
4. **日志监控初始化**：脚本启动后按 `LogPath` 格式严格解析（`LogPattern.ResolveFile`，文件不存在返回 null）；文件存在时按**尝试开始前长度快照**判定：尝试开始前不存在的文件从头读，已有残留从尝试开始时长度续读；残留被启动后追加写也不会进入判定输入——无松弛窗口，忽略运行前已有内容。
5. **监控循环（每 1 秒）**：
   - 重新解析日志路径；路径变化（日期轮换/通配取新）→ 重新监控；
   - 同路径文件被**替换**（move 归档后重建/删除重建，`LogMonitor.FileReplaced` 对比卷序列号+文件索引）→ 重开从头读；
   - 文件被**截断**（`ReadNew` 检测 `Length < position`）→ 部分截断（缩短未归零）从新文件尾续读，避免已读旧行重复进入判定；长度归零从头重读；
    - 读取新增内容 → 逐行送入判定（关键字）→ 追加运行日志与 UI 日志。
6. **判定分支**：
   - 失败关键字命中 → 立即终止本次尝试（杀进程树）；
   - 成功关键字命中 → 等待脚本自行退出（最多 60 秒，超时杀进程仍判成功）；
   - 判断脚本模式 → 批次触发/周期触发/最终触发（见[完成判定](judgement-logs.md#完成判定)），可得到 success 或 partial；
   - 无任何判定且进程退出 → 按「进程自行退出」判定成功（未配置判定时）；配置了判定但无命中 → 失败。
7. **超时与启动失败**：`LogStallTimeoutMinutes=-1` 时跳过无日志超时；其余值使用单调时钟观察文件新增内容及 stdout/stderr 输入，报告心跳不重置时钟。首次输出后无新增输入也会超时。输出在字节边界按冻结 resolve 的 `outputEncoding` 声明解码；MaaEnd／MaaStellaSora 的 MXU 声明 UTF-8，未声明保持原系统默认值。当前尝试的 stdout/stderr 或新增文件日志明确输出“任务启动失败: 未搜索到任何窗口”（也接受中文冒号）时记录启动失败。MXU 报告 `[MXU_LAUNCH] Failed to spawn/run program`、参数 JSON 无效或启动程序缺失时也立即失败；清理确认后才进入原有专项安全重试判断。`RunBudget` 的总时间跨全部重试和前后置脚本计算；判断脚本仍有独立 30 秒上限。
8. **尝试结束清理**：`RunAttemptFinalizer` 统一承载进程树清理和游戏/模拟器策略（Toolhelp 快照 + BFS 逐进程强杀，**与 `GameExe` 同名的进程树排除在外**、生杀归游戏管理）；**任务失败时无条件强制结束游戏进程**；成功或部分完成时按 `ForceCloseGame` 设置决定是否关闭游戏。
9. **重试**：失败且未达 `MaxAttempts` → 进程确认退出后继续复用当前活动 `config`，在下一轮开始前应用判断脚本返回的 `replaceConfigs`；每次尝试仍独立 LogMonitor 与 SessionJudge。
10. **运行收尾（finally）**：`ConfigRunSession` 固定执行自动更新配置收尾同步（按文件差异写入 store，仅开关开时）→ 还原配置替换（swap-backup → config）→ 清空判断脚本目录 → 配置交换还原现场（original → config）。同步先于插队还原与配置交换还原，确保 store 看到脚本最终态，同时避免恢复动作覆盖用户快照。



### 手动执行脚本

- 指定用户：只运行该用户；未指定：按启用用户顺序全部运行一次。
- 冲突检查：脚本启动目标已在运行时沿用既有进程检测；脚本和队列入口统一走资源租约准入，队列计划中的已运行脚本或与活动执行共享脚本/进程/配置/模拟器端点资源时返回准入错误。
- 取消：`Cancel` 接受后先冻结下一项派发，再向本次拥有的脚本进程发停止请求；配置写入者退出后才恢复配置并提交终态。重复取消保持同一请求，迟到的输出不能进入下一用户的日志段。
- 当前日志：每个实际用户执行记录先有准备段，每次主执行尝试再开独立段。切换时在同一状态快照中更新尝试号、段标识、代际并清空旧尾部和截断标记；进程与前后置 hook 的输出携带其启动时的尝试号，迟到行不进入新段。API 和实时事件同时提供兼容的 `logSegmentId`/`logSegmentSequence` 与可选的 `logSegment`（记录 ID、尝试号、代际），历史记录及其原 ID 不因分段改变。

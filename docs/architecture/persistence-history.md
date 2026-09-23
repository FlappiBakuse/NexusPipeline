# 持久化与历史

## 历史与运行时数据



### 历史与日志落盘

任务摘要维护最近实际运行与最近准入停止两条索引。仅有准入证据、且任务报告未产生执行尝试和最终任务结果的整次运行才进入纯准入索引；已执行过任务的运行即使重试时被阻断，仍是最近实际运行，同时记录准入停止。加载旧索引时先按保留的运行记录重新分类再比较时间；已删除的索引项继续作为 tombstone，不能因重建而恢复更旧的成功记录。

- 每次「脚本实例 × 全局用户绑定」运行结束保存到 `history/YYYY-MM-DD/<用户昵称>/<本轮运行任务>/`：
  - `<HH-mm-ss>.json`：**纯运行状态**（PascalCase，包含 `HistoryDirectory`、`Status`、Attempts、各 Attempt 的 `LogFile` 与截图元数据，不含日志正文和图片字节）；同一用户同一秒的运行目录追加 `-2`、`-3` 等后缀；
  - `<HH-mm-ss>-<尝试号>.log`：**每个 Attempt 一个独立日志文件**，保存脚本日志全文（20MB 截断；空日志写「（未配置日志路径或未监控到脚本日志）」兜底）；
  - `<HH-mm-ss>-<尝试号>-s<序号>.jpg`：按该 Attempt 当前 FIFO 保留顺序编号，序号范围为 1–8。
- 配置了 `LogPath` 时，业务日志以日志文件监控结果为单一来源；未配置 `LogPath` 时，业务日志来自脚本 stdout/stderr。实时显示和历史详情沿用相同的等级解析。
- 每个 Attempt 最终保留的截图写入同一运行目录，JSON 保存元数据；通知发送完成后释放运行期内存截图池。
- `Status` 是一次运行唯一的最终状态：`success / partial / failed / cancelled / skipped`；其中 `partial` 只能来自判断脚本显式结果，不由重试次数、退出码或日志关键字派生。
- 历史 Web、CLI 和 MCP 查询可按上述最终状态筛选；历史返回投影按 `EndTime - StartTime` 计算 `durationMs`，结束时间缺失返回空值，负差值归零。运行 JSON 继续保持纯运行状态，不落盘 `DurationMs`。
- 历史摘要按范围、脚本、队列、用户和状态聚合总运行数、五种状态计数、已结束运行的累计/平均耗时、成功率和每日趋势；没有结束时间的运行不参与耗时统计，成功率为成功运行数占筛选结果总数的百分比。
- `PluginHistory`：运行落盘前由已注册插件生成的纯文本展示快照；单贡献 16 KiB、单次运行总量 64 KiB，插件异常不会影响运行结果，卸载插件后历史仍保留快照。
- 保留天数 `HistoryRetentionDays`（默认 7）每日清理一次（启动时 + 调度器每日首次 tick）；上限固定为 180 天；管理器日志 `logs/nexus-pipeline-YYYY-MM-DD.log` 同样按保留天数清理。
- 审计行 `[审计] 来源 | 操作（详情）`，来源 web/manage/cli/scheduler/system；`GET /api/status` 轮询豁免不记录。



### 运行状态目录

正常服务运行产生的三类内部状态集中在安装目录下的 `.nxp/`：

```text
.nxp/
├── runtime/
│   ├── service.pid
│   ├── web.port
│   └── staging/          可重建暂存区（上传/校验临时文件，启动时整体清扫）
└── state/
    ├── scheduler-state.json
    ├── appearance-migration.json   旧外观数据搬迁标记
    ├── update-policy-cache.json     经验证的更新策略响应缓存
    └── plugins/          catalog-cache.json、ownership.json、pending.json、staging/、backup/
```

`service.pid` 与 `web.port` 是可重建的 ephemeral runtime metadata；服务正常退出时清理。`runtime/staging/` 同属 ephemeral：仅承载单次请求内的上传/校验临时文件，启动时无条件清扫残留。`scheduler-state.json` 保存定时 occurrence、重试状态、冻结队列计划及恢复所需快照，属于 internal durable runtime state，不按缓存处理；`appearance-migration.json` 记录一次性旧外观数据搬迁的完成状态，只在搬迁成功后写入；插件安装事务的 `staging/` 与 `backup/` 需要跨重启存活，因此位于 `state/` 而非 `runtime/`。

`RuntimeStateLayout` 在服务启动时创建当前目录；CLI 端口发现读取 `.nxp/runtime/web.port`，找不到时按设置端口范围探测。

`.nxp-update/`、`.nxp-backup/`、`.nxp-version` 与根目录 update worker 继续作为更新 crash-recovery protocol 的组成部分，保持原路径和生命周期。`scheduler-state.json` 中持久化的 `LastSchedulerCheck` 是崩溃恢复的 replay fence，调度器的实际定时扫描从当前分钟开始，启动或停顿期间错过的历史 occurrence 不会被补发。



### 文件布局治理规范

运行时文件系统的统一约定（新增或调整持久化路径时必须遵循，并同步更新本节、[配置专题](configuration.md#运行时目录与快照)和[运行状态目录](#运行状态目录)的布局树）：

1. **单一事实源**：全部路径常量集中在 `src/Platform/Storage/RuntimeStateLayout.cs` 与 `src/Modules/Configuration/Paths/ConfigSwapPaths.cs`，业务代码不得自行拼接安装根相对路径。
2. **目录分类归位**：目录按生命周期分四类——常驻持久（config/、user-assets/、plugins/、data 持久层、.nxp/state/）、常驻可重建（.nxp/runtime/、按保留期滚动的 logs/ 与 history/）、会话事务临时（data work/、.nxp/runtime/staging/）、隔离归档（data-trash/、judge-scripts/orphaned/）。**临时类必须有明确的清理路径**（收尾清理或启动清扫），隔离现场在确认提交前保留。
3. **命名约定**：目录与普通数据文件一律 kebab-case（`data-trash`、`swap-backup`、`store-txn`、`store-meta.json`），**禁止 dot 后缀命名**（`store.previous` 这类"目录带扩展名"的形式不允许出现，dot 后缀仅允许作为文件扩展名本身，如 `.json`、`.log`、`.jpg` 与临时文件的 `.tmp`）；进程内部隐藏标记用 dot 前缀（`.nxp/`、`.session`、`.session.bak` 与 swap-backup 内的 `.meta` 清单）；数据文件名为 `<名称>.json`（磁盘 JSON 一律 PascalCase 字段 + UTF-8 + 原子写）。隔离/归档条目命名 `<主名>-<yyyyMMddHHmmssfff>-<Guid:N>`，staging 子目录命名 `<名称>.<Guid:N>`。
4. **损坏保全**：JSON 解析失败时原文件改名为 `*.corrupt-<时间戳>-<guid>` 保留现场，等待人工处理，不被后续保存覆盖；快照事务 manifest/commit 损坏时写入阻断标记，拒绝继续猜测写入。
5. **持久化格式变更**：改变既有 API、字段或目录布局时，先明确当前协议和升级前备份要求；运行时只处理当前协议，未知现场保留并告警，版本发布说明提供用户可执行的备份提示。
6. **有意保留的复杂度**（经评估为必要，勿"简化"）：`.session`/`.session.bak` 双标记是主标记损坏时拒绝猜测恢复的安全兜底；`limits.json` 启动生成默认文件是既定行为；更新事务目录留在安装根是更新 crash-recovery 协议的一部分；`outputs/` 已无写入方，仅保留保留期清理与更新包白名单作为旧安装残留的自愈防御；history 运行目录内层 JSON 与目录同名（`<脚本名称>-HH-mm-ss/<HH-mm-ss>.json`）为当前布局。

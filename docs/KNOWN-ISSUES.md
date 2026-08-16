# 已知问题台账（Known Issues）

**建立日期**：2026-08-15（v0.6.9 全面代码评估产出）｜ **维护记录**：v0.6.10~v0.7.6 分批修复台账内全部可修复项（KN-01~77 等，修复详情见 [CHANGELOG.md](../CHANGELOG.md) 对应版本）｜ **2026-08-16（v0.7.7）**：已修复条目**彻底移除**，本台账仅登记**当前未修复**问题；后续新发现按版本登记。

> 本台账仅登记项目已确认但**尚未修复**的已知问题（潜在 BUG / 边界缺陷 / 语义待决）。已修复项不再保留（历史记录见 [CHANGELOG.md](../CHANGELOG.md)）；版本修复排期建议见 [ROADMAP.md](ROADMAP.md)；代码定位指引见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 分级说明

- **高**：数据丢失 / 配置损坏 / 重复执行 / 资源泄漏风险，建议近期版本优先修复；
- **中**：逻辑瑕疵 / 边界缺陷 / 一致性违规，按版本节奏修复；
- **低**：死代码 / 文档过时 / 样式与自约束相悖，随版顺手清理；
- **随版**：低概率/低影响边界项，不排期，修复随涉及版本顺带处理。

## 中优先级

| 编号 | 问题 | 位置 | 建议 |
|---|---|---|---|
| KN-09 | **日志截断后立即写入的内容漏判窗口**：`ReadNew` 长度检查在 `Length < position` 时把 position 置为新尾——若截断后、下次读取前已写入新内容，截断后新写内容不进入判定输入（失败关键字可能漏判） | `src/Services/LogMonitor.cs:138-143` | 随版（两难问题：补漏判需知截断点，文件系统不提供；改从头读复活旧行重复污染，实际影响小于已修问题，建议文档化保留） |

## 随版（低概率/低影响边界项，2026-08-16 评估登记）

| 编号 | 问题 | 位置 | 状态 |
|---|---|---|---|
| KN-73 | **Mutex「持有中销毁」窗口**：`RemoveMutex` 在 `WaitOne` 成功之后、action 执行期间 Dispose 时，finally 的 `ReleaseMutex` 异常被吞、互斥体所有权丢失，同进程重建同名 Mutex 后理论上双线程可同时进临界区（KN-36 修复仅覆盖「WaitOne 时已 Dispose」窗口） | `src/Services/ConfigSwapPrimitives.cs:106-152` | 随版（Web 删除路径有 `gate.Wait(0)` 前置保护，实际窗口极小） |
| KN-74 | **.meta 损坏 → 永久待办无限重试**：`RestoreConfigReplacements` 遇 .meta 损坏/缺 configPath 时保留备份现场 return → `HasBackupResidue` 恒真 → 后台恢复循环每 10 秒无限重试同一项直至进程退出（符合「保留现场」意图，但日志持续刷 Error，无退出机制） | `src/Services/ConfigSwapSession.cs:229-239`、`ConfigSwapSession.cs:539-582` | 随版（建议：损坏 .meta 时告警并改名保留以解除待办，或跳过该待办） |
| KN-75 | **MigrateLegacyLayout 用户目录名碰撞**：脚本级旧布局重命名（config→store 等）不校验目标是否为「用户目录」——用户恰巧名为 `config`/`cache`/`edit-hide`/`replace-backup` 时其数据目录被误当旧布局迁移改名，用户数据错位（低概率） | `src/Services/ConfigSwapPaths.cs:76-119` | 随版（建议：迁移前排除含数据目录子结构/加保留名单） |
| KN-76 | **PrepareForRun 失败回滚形态不一致**：`!prepared` 回滚分支固定按目录还原（未用 `RestoreKind(mark)`），单文件原配置在「复制成功、删源失败」窄窗口下被还原成「目录/同名文件」形态且立即清标记（无恢复标记；`prepared` 分支的 `DoRestore` 处理正确） | `src/Services/UserConfigManager.cs:158-168` | 随版（窄窗口：MoveAs 删源失败才触发） |
| KN-78 | **`ApplyToggle` 整树重写丢格式/BOM/编码**：`File.ReadAllText`（剥离 BOM）+ `JsonNode.ToJsonString`（2 空格重排）+ `WriteAllText`（UTF-8 无 BOM）——原文件带 BOM/GBK/UTF-16 编码或紧凑格式时重写后变化（MXU/BetterGI 用 Newtonsoft 无碍，自定义插件场景无编码保证） | `src/Services/ConfigSwapSession.cs:646-659` | 随版（建议：编码检测 + 最小 diff 替换而非整树重排） |
| KN-79 | **专项恒 true 仅前端保证**：`ApplyProfile` 不设 `AutoUpdateConfig`，curl/CLI 直连可构造专项脚本 `autoUpdateConfig=false`（与「专项恒开」声明不符；文档明示后端不强设是有意设计） | `src/Web/ApiScriptsHandler.cs:254-272` | 随版（防御性建议：后端同样强设 true） |
| KN-82 | **首次检测同步阻塞主监控循环**：`WithSwapLock`（30 秒锁上限）+ 800ms 双采样 + 全量镜像期间日志判定/游戏前置/超时检查全部延迟；跨进程锁竞争时最长阻塞 30 秒（日志行不丢但判定迟到） | `src/Services/ConfigSwapSession.cs:299-331` | 随版（文档化；锁竞争窗口极小） |
| KN-83 | **多轮替换后还原描述 index 漂移**：maaend judge.js 固化 `instances[instIndex]` 下标，重试轮中 MXU 若改变实例顺序则 `ApplyToggle` 可能复位另一实例的启停 | `plugins/maaend/data/judge.js:93-109` | 随版（低概率；建议 DSL 支持按 id 定位数组元素） |

## 语义文档化保留（2026-08-16 与用户对齐确认，非缺陷）

| 编号 | 语义 | 决策 |
|---|---|---|
| KN-80 | **「关」仍执行首次检测回写**：`AutoUpdateConfig=false` 时运行开始 15 秒后仍同步一次（捕获脚本启动后自行更新的任务配置）——与关闭开关的直觉（完全不回写）相悖 | 用户决策维持现状（关 = 仅首次检测同步，收尾不同步），语义以 AGENTS.md「自动更新配置」节为准 |
| KN-81 | **快速失败脚本首次检测永不触发**：首次检测条件 `attempt.Number==1` 且运行 15 秒后——第 1 次尝试 15 秒内结束进入重试轮后条件不再满足，开关关时 = 零同步 | 用户决策符合设想（可接受零同步；开关开时收尾同步兜底） |

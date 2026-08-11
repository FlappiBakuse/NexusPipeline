# Changelog

本仓库所有重要变更均按版本记录于此。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/)，版本遵循 [SemVer](https://semver.org/lang/zh-CN/)（v1.0.0 之前为 Pre-release）。

## [v0.5.2]（2026-08-11）

### 修复

- **BUG #1：多用户运行结束后 config 现场被配置替换备份污染**——`RunSession.RunAsync` 收尾顺序调整：先还原配置替换（replace-backup → config）并清空判断脚本目录，再执行配置交换还原（cache → config 恢复运行前现场），避免替换还原覆盖交换还原的现场。
- **BUG #2：重试轮（attempt≥2）日志监控失效与残留误判**——三层根因根治：
  - **日志文件替换检测改文件身份（FileId）**：`LogMonitor` 新增 `GetFileInformationByHandle` 对比卷序列号+文件索引（`FileReplaced`），脚本 move 归档旧日志后重建新文件时可靠检测并重新从头读取（原创建时间检测在新旧文件 CreationTime 相同时失效，导致监控句柄指向被改名的旧文件、ReadNew 恒空）；
  - **初始监控严格 fresh 判定**：仅当文件在本次尝试开始后写过（`LastWriteTime ≥ attemptStart`，无松弛窗口）才从头读，否则末尾读忽略残留——上一尝试残留不再被误判为本次新文件而重读进判定；
  - **判断脚本输入按尝试切片**：输入日志取本次尝试日志段（上次尝试的失败/成功行不再跨尝试污染判定），同时消除"部分日志批次提前触发"竞态。
- 混沌测试 bat 恢复 move 归档（真实覆盖日志重建场景）；新增混沌调度队列压力测试 `uitest/chaos-queue.mjs`（171 断言，固定/随机种子轮、多用户配置交换、五种干扰判定、崩溃注入、通知双模式、无残留）。

### 文档

- 新增 `docs/DESIGN.md`：核心设计理念（本地优先/配置交换/判定策略/日志监控/自愈）与核心运行流程分步说明（含 mermaid 图）。
- 新增 `CHANGELOG.md`（本文件）。
- 更新 `docs/ARCHITECTURE.md`（判断脚本执行器归位后端表、LogMonitor 职责、v0.5.1/v0.5.2 变更记录）、`README.md`（判断脚本输入口径、游戏语义、测试命令与断言数、文件结构）、`docs/DEVELOPMENT.md` 与 `CONTRIBUTING.md`（测试命令迁移 @playwright/test 与专项测试）。
- 版本号 0.5.2。

## [v0.5.1]（2026-08-11）

### 新增

- 插件级配置：`PluginContext.GetConfig<T>/SetConfig<T>` 落盘 `config/plugins/<插件名>.json`（PascalCase、原子写入）；`GetSecret/SetSecret` 密钥 DPAPI 加密（`enc:` 前缀，与普通配置同文件）；PluginManager 初始化注入插件名；NotifyPlugin 保持 AppSettings 配置不动（行为零变化）。

### 变更

- e2e 迁移 @playwright/test：`tests/` 按域 7 文件 46 用例（CI 核心集 45），移除旧 `test.mjs` 与 EXPECTED 计数机制；`run-uitest`/CI 改 `npx playwright test` + `NEXUS_CI=1`；global-setup/teardown PID 文件跨进程管理服务。
- 前端 `core/limits.js` 归位 `views/limits.js`；`extensions/README.md` 插件开发指南；`.gitignore` 增补 test-results/playwright-report。

## [v0.5.0]（2026-08-11）

### 变更

- 架构重构：核心域按子域重组 `Models/Services/Persistence/Utilities` 命名空间；壳式 DI（`RuntimeContext` 内建 `ServiceProvider` + `Resolve<T>()` 服务出口，插件 `PluginContext` 可解析宿主服务）；Web 特性路由 `[ApiRoute]` 反射扫描注册（新增 API 无需改路由表）；`ApiSettings` 约定自动绑定（camelCase↔PascalCase、密钥白名单与 DPAPI、historyRetentionDays 400 校验保留）。
- `RunSession` 判定策略拆出 `SessionJudge` 状态机；`UserConfigManager` 拆门面 + 原语/会话恢复/数据目录三层（`ConfigSwapPrimitives/Session/Paths`）。
- extensions 插件工程对齐；修复反射路由 cancel 适配与 e2e 历史页时序竞态；e2e 480 断言。

## [v0.4.4]（2026-08-11）

### 新增

- 远程访问（可选）：访问令牌（DPAPI 加密）、http.sys 强通配符绑定、自动防火墙入站规则、局域网地址提示；修复 401 状态码被默认参数覆盖与端口占用复用损坏 HttpListener 闪退。

### 变更

- 多通道通知并存；控制台输出按次完整保存三件套、每日清理；管理器日志命名统一；保留天数默认 7 上限 180；e2e 479 断言。

## [v0.4.3]（2026-08-11）

### 变更

- 运行总时长语义：`TotalTimeoutMinutes` 改为整个运行（含全部重试与前置/后置脚本）计时，前置/后置脚本按剩余时长；`PrepareForRun` 标记先行消除崩溃窗口，恢复失败后台延迟重试自动还原（孤儿进程退出后补还原）。
- 判断脚本输入日志 4MB 有界截断并置 `logTruncated`；专项测试新增配置交换崩溃恢复与零日志探测竞态修复（judge-scenarios 99 断言）。

## [v0.4.2]（2026-08-11）

### 修复

- 单文件配置替换（B-6）：`replaceConfigs` 项等于文件名时替换生效并还原；空 config 目录用户交换后目录消失修复。
- 运行前置：脚本必须至少一个启用用户，手动运行拒绝、队列跳过记录 failed 历史。
- 场景 D 崩溃链路：进程退出未找到日志文件路径补最终触发。

## [v0.4.1]（2026-08-11）

### 修复

- v0.4.0 自定义完成标志链路缺陷：配置替换新文件残留、路径前缀逃逸、成功判定后重复触发、通知文本被用户脚本覆盖、日志超时无最终触发、API 空判断脚本校验。

## [v0.4.0]（2026-08-11）

### 新增

- 自定义完成标志大更新：成功/失败关键字（行内 AND、行间 OR，失败立即终止，成功等待退出 60 秒）；判断脚本（JS 内置 Jint 引擎 / Python 系统解释器，config 只读 + script 目录可读写边界与防逃逸；日志批次/阻塞周期 30 秒/进程退出最终触发）；插队替换配置 `replaceConfigs` 失败后自动重试并在运行结束还原（含崩溃恢复）；`notifyText` 替换通知正文；专用插件脚本强制禁用自定义字段；前端关键字/脚本切换按钮模式与上传脚本；e2e 466/443。

## [v0.3.6]（2026-08-11）

### 变更

- 前端布局 Notion 风格化：设计令牌收敛（小圆角/轻阴影/实体面板、浅色米色系、深色保留品牌蓝）、去渐变去玻璃态、统一紧凑列表行、粒子弱化、移动端触控目标 40px。
- 插件配置页密钥并入保存设置（Webhook 地址/签名密钥同行、地址改 text、移除重复保存按钮）；前端约束文档同步（AGENTS.md Notion 风格基线/展示模式/密钥字段语义）。

## [v0.3.5]（2026-08-11）

### 新增

- 脚本用户上移/下移排序（顺序 API + 执行顺序保证）；定时列表完全一致合并与间隔 <10 分钟确认卡片；删除脚本/队列改用确认卡片；前端路径首尾引号去除。

### 变更

- 游戏路径必填解绑：任务失败无条件强制结束游戏进程，成功按「强制关闭」设置；版本号同步 0.3.5。

## [v0.3.4]（2026-08-11）

### 新增

- 脚本图标自动取主程序 PE 资源最高分辨率（含 256×256，无资源回退关联图标）；脚本路径存在性合规校验（Web/API/CLI 统一）；游戏配置卡弹窗内常驻（勾选启动游戏后路径必填且为可执行文件）。

### 修复

- e2e 占位脚本改用唯一命名（规避 `IsExeRunning` 按进程名误报导致调度中心执行被拒）。

## [v0.3.3]（2026-08-11）

### 新增

- ZenlessZoneZeroOneDragon 专用插件；完成标志重构（内置关键词取消、各插件固化自有关键词、通用脚本按进程退出判定）。

### 变更

- 强制管理员运行（非管理员启动拒绝并退出、移除脚本 740 runas 降级提权）；前端卡片 1/2 布局与长文本滚动、脚本卡片徽章改为插件提供的中文游戏名（`GameName`，主程序不写死）；e2e 新增 `--ci` 核心回归集。

## [v0.3.2]（2026-08-11）

### 新增

- 管理员提权（manifest requireAdministrator + 计划任务自启）；脚本启动参数显式路径启动目标与自动提权（740 runas）；March7thAssistant 专用插件。

## [v0.3.1]（2026-08-11）

### 变更

- 进程检测语义重做（未填游戏路径不检测、填路径确认脚本游戏双启动、自重启进程宽容判定与清理、跑完强制结束）；执行前检查（手动禁止/自动跳过并记录）；强制关闭游戏依赖启动游戏（前端联动+后端归一化）；配置 JSON 中文原字符输出；历史列表移除结果文字。

## [v0.3.0]（2026-08-11）

### 新增

- 插件分离：通用/专用契约（`IPlugin`/`ISpecializedScriptPlugin`）、BetterGI 专用插件、外部插件默认启用；日志路径格式（日期占位符/通配严格匹配、无条目超时、忽略已有日志、轮换跟踪）；脚本实例图标提取与卡片化；队列卡片同构与下次触发倒计时；新建实例选择卡片。

## [v0.2.3]（2026-08-11）

### 新增

- 用户数据保留与迁移；脚本已打开检测；队列多用户依次运行；路径引号清洗；队列不运行模式；协作规范以 v1.0.0 为界。

## [v0.2.2]（2026-08-11）

### 变更

- 通知复选框随插件显隐、generic 模板联动与样式统一；e2e 重构（`--quick` 模式）。

## [v0.2.1]（2026-08-11）

### 新增

- 约束/分页/告警/FATAL e2e 用例集（98 项）；前端分页组件与约束（pager、达上限禁用、警告卡片）；约束体系（`config/limits.json` 三层校验、Web/CLI 统一落点、FATAL 全模式拒启）；存储优化（WriteAtomic 原子替换、历史 jsonl 顺序索引、历史 API 服务端分页）。

## [v0.2.0]（2026-08-11）

### 变更

- 前端模块化（views 按域 7 文件、action 注册表、core 细分 dom/format/forms/modal）；后端分层解耦（Web/Cli/Plugins 命名空间、Handler 拆分、插件契约 `INotifyChannel`、DataStore/Bootstrap）；日志级别体系（DEBUG/INFO/WARN/ERROR/FATAL，阈值过滤与控制台着色）；架构文档与 AGENTS 模块地图更新。

## [v0.1.0]（2026-08-11）

### 新增

- 初始提交（枢链）：脚本实例/用户/队列管理、托盘服务、Web 管理界面、调度与通知的基础能力。

[v0.5.2]: https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.5.2

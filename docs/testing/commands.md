# 测试命令

## 默认命令

以下命令均在 `NexusPipeline/` 根目录执行：

方案 B 的本地阶段入口已可使用；线上 Required 仍按现行 ruleset 运行旧 Qualification，切换前不要删除旧工作流：

```text
node tests\run.mjs prepare
node tests\run.mjs fast
node tests\run.mjs integration
node tests\run.mjs all
```

`prepare` 安装前端和工具工作区的锁定依赖；`fast` 执行后端单测、前端类型检查与单测、官方插件契约、文档、工具、语法和架构检查，不构建生产包。非纯文档的 `fast` 必须通过 `NEXUS_OFFICIAL_PLUGINS_ROOT` 指定固定的官方插件 checkout。`integration` 在 Windows 上构建一次隔离 Test Host，执行 UI 与系统贯通、更新和执行真实计时；不构建生产包。`all` 在同一进程中依次执行 fast 和 integration，复用已准备依赖及 Test Host。生产构建由 `build` 单独执行。

本地 `build.cmd` 只清理程序自有的 `release/wwwroot` 前端输出；重新发布到 `release/` 时保留 `plugins/`、`config/`、`data/`、`history/` 与 `logs/` 等运行数据。正式候选仍在独立的 `.generated/candidate/` 中构建和验包，不读取本地 `release/` 的运行数据。

```text
dotnet test tests\NexusPipeline.Tests\NexusPipeline.Tests.csproj --nologo -m:1
npm run typecheck --prefix frontend
npm test --prefix frontend
npm run build --prefix frontend
node tests\run.mjs unit
node tests\run.mjs frontend
$env:NEXUS_OFFICIAL_PLUGINS_ROOT="..\NexusPipeline-Plugins"
node tests\run.mjs contract
node tests\run.mjs docs
node tests\run.mjs tooling
node tests\run.mjs syntax
node tests\run.mjs build
node tests\run.mjs release core
node tests\run.mjs release frontend-contract
node tests\run.mjs release ui-runtime
node tests\run.mjs release execution-emulator
node tests\run.mjs release update-acceptance
```

统一 runner 会在对应 Gate 首次运行前执行 `npm ci --no-audit --no-fund`，并要求每个工作区存在 `package-lock.json`；依赖准备失败会直接终止 Gate，不会降级为跳过。

`release core` 的最后一项是架构门禁：对 production 和 Test Host 真实加载 MSBuild/Roslyn 工程执行 `check`，随后生成 `.generated/architecture/backend-map.json`，校验 schema、非空源码集合、owner 路径和预期模式，重复生成结果必须一致；当前 run 的 artifact 中同时保存带候选 SHA 的地图副本。地图不是宿主运行时或 `release/` 的输入。

`tooling` 发现并运行现役 Node 工具测试（文档索引测试由 `docs` 独占）和 `tools/tests/test_*.py`，同时覆盖前端边界、源码编码、更新策略、资格控制与原生报告解析。插件源码布局测试只在 `tooling` 执行。

统一 runner 保留实时 stdout/stderr、退出码、超时信息和原生 TAP/TRX/Vitest/Playwright 报告。每次运行的报告位于 `tests/.artifacts/runs/<runId>/reports/`；失败时不删除唯一原始报告。

`unit` 与 `frontend` 支持重复指定 `--group <名称>`；`list --json` 返回本地 registry 的现役分组。旧的 `--affected`、`--dry` 和权限模式入口已退役；正式入口必须实际构建或测试。

`core` 的跨仓库文档检查、`frontend-contract` 与 `ui-runtime` 必须通过 `NEXUS_OFFICIAL_PLUGINS_ROOT` 显式指定同一官方插件源码根目录；不会根据相邻目录猜测来源。文档检查需保留链接引用的 Git 历史（CI 使用完整检出）。`contract` 加载官方插件构建模块并注册真实宿主公共元素；入口会在宿主 `frontend/`、宿主 `tools/` 与该插件根目录安装前端依赖，并执行插件仓库的 `npm run build:frontend`。

`frontend` 入口执行 Vitest 和类型检查；前端生产构建由 `build` 或 Test Host 集成入口按需执行。相同前端源码和锁文件的本地构建可由指纹复用，输入变化会使旧构建失效。

统一组合入口按功能范围选择；所有功能测试均使用隔离的 asInvoker Test Host：

```text
node tests\run.mjs dev default
node tests\run.mjs dev ui
node tests\run.mjs dev system --group runtime
node tests\run.mjs dev all
```

System Smoke 支持按影响域分组运行，便于 CI 与本地只跑受影响的 suite：

```text
node tests\run.mjs dev system [runtime|control|config|execution|judge|emulator|plugins|update] [--realtime]
```

可以列出多个分组，也可以用 `--group <名称>` 重复指定；省略分组等于全部 suite。未知分组会打印可用分组并以 exit code 2 退出。每个 suite 都会实际启动独立 Test Host，不提供 dry-run 替代测试。

Test Host 使用 `NexusTestHost=true` 的 `asInvoker` 清单和每次运行独立的 `tests/.artifacts/runs/<runId>/`；完整性等级只作为诊断信息，不决定是否跳过测试。

每次 Release Qualification 的 H5 固定执行两次独立的 Test Host Update 和一次 Execution 真实计时段：

```text
node tests\run.mjs release update-acceptance
```

该入口按 accelerated → update-realtime → execution-realtime 顺序执行；后两段不接受过滤，报告目录按 runId 和阶段分开保存。

System Smoke 的 `emulator` suite 覆盖宿主内置 Generic ADB、MuMuManager、无扩展时 Generic ADB 回退，以及通过真实 managed-code TestPlugin 注册的 API v1.7 provider 执行、截图和实例清理。雷电、夜神和 BlueStacks 的厂商命令与探测在 `NexusPipeline-Plugins/plugins/general/EmulatorSupport` 的插件测试中验证；真实设备矩阵由插件仓库维护。`update` suite 的 8 个用例覆盖启动前宿主更新检查、安装与恢复及既有运行期更新；启动失败冷却由 `StartupUpdateAttemptStore` 和 `StartupUpdateCoordinator` 单元测试覆盖。



## 质量门禁顺序

1. 修改宿主代码、测试或前端纯函数后运行 `node tests\run.mjs dev default` 的适用组合。
2. 涉及配置交换、Windows 进程、端口、解释器、插件、模拟器或更新事务时，追加 `node tests\run.mjs dev system`；宿主模拟器系统边界通过 TestPlugin 验证 provider 注册到执行及清理，厂商驱动行为由官方扩展插件测试覆盖。
3. 发布前由 Qualification workflow 独立执行 `release core`、`frontend-contract`、`ui-runtime`、`execution-emulator`、`update-acceptance` 五个 Gate；本地 `release all` 使用相同顺序。
4. 两仓库的宿主—插件契约发生变化时，在插件仓库执行下方固定 P1/P2/P3，并核对两仓库文档、manifest 和测试。
5. 修改 `frontend/src/plugin-bridge/**`、`frontend/src/platform/appearance.ts`、公开 `nxp-*` 元素或 Frontend API 契约时，必须执行 `NexusPipeline-Plugins/tools/Test-FrontendPlugins.mjs` 和宿主 `node tests/run.mjs contract`。前者使用 mock host 验证插件业务与生命周期，从实际宿主检出的 `NEXUS_PUBLIC_ELEMENTS` 读取公开清单；后者加载真实公共元素完成装配交互验证。两仓库 Qualification 各自固定一次对端官方完整 SHA；Plugins 的 `host.lock.json` 只保存 API/locale 兼容元数据，不再承载浮动或旧式 commit 锁。managed-code 构建与打包共享该次 SDK 来源。

在 Plugins 仓库根执行以下 PowerShell 命令。`HOST_ROOT` 指向实际干净 Host checkout；SDK SHA 从该目录取得，目标基线 B 从本次已同步的 main 取得。目录不要求相邻。

```powershell
$env:HOST_ROOT = (Resolve-Path '<实际 Host checkout 路径>').Path
$env:SDK_SHA = (git -C $env:HOST_ROOT rev-parse HEAD).Trim()
$targetBase = (git rev-parse main).Trim()
python tools/repository.py qualification --group source --base $targetBase --host-root "$env:HOST_ROOT" --sdk-sha $env:SDK_SHA
python tools/repository.py qualification --group frontend-managed --base $targetBase --host-root "$env:HOST_ROOT" --sdk-sha $env:SDK_SHA
python tools/repository.py qualification --group candidate --base $targetBase --host-root "$env:HOST_ROOT" --sdk-sha $env:SDK_SHA --output .generated/qualification-candidate
```

H1–H5 的 CI job 无论成功或失败都会保留本次 `tests/.artifacts/runs/*/reports/` 原生报告；不会上传整个 runtime、用户配置或日志目录。依赖安装阶段尚未产生报告时，job 日志保留命令及非零退出状态。

   脚本当前校验：`frontend.apiVersion` 必须为 1.5、宿主私有结构 class 拒绝、`nxp-*` 元素必须来自公开元素集合、插件不得复制 Nexus UI 组件，并按插件页面形态检查公开契约。slot 插件验证 renderer、所需公开控件和 cleanup；GameCheckIn route 页面验证导航/页面注册、任务 API 行为、离开后资源释放和再次进入。CustomWallpaper 额外覆盖插件级壁纸运行时：激活即应用背景与配色、离开设置页面保留全局外观、页面访问不触发随机轮换、按时间轮换在无设置页面时继续生效、只有插件停用才清理外观与计时器。

构建、测试和发布命令保持实时输出；失败时保留失败项、原因摘要和必要的 runtime 证据。长任务不使用无反馈的超长等待。

生产专项适配器的真实 Jint 联调：`dotnet run --project tools/NexusPipeline.TaskProtocolTests -- --plugin-root <Plugins>`。此命令使用显式插件 checkout 和合成夹具；插件 P1 也运行相同入口。

跨账号配置与恢复：`dotnet run --project tools/NexusPipeline.TaskProtocolTests -p:NexusTestHost=true -- --plugin-root <Plugins> --account-isolation <报告.json>`。八个生产适配器、两个账号、六种生命周期情景，共 96 项；使用新建目录和合成配置，不启动目标软件。失败时输出并保留该情景的隔离目录。

## 外部实例日志只读回放

`dotnet run --project tools/NexusPipeline.TaskProtocolTests -- --plugin-root <Plugins> --replay-manifest <外部清单.json>` 使用真实 Jint/发现/归并器读取显式声明的实例文件，不把旁边的历史 JSON 当成成功证据，不启动游戏或修改配置。清单和报告必须保留在仓库外。

清单字段：`report` 为外部报告路径；`scenarios` 每项包含 `artifact`、`resources`（每项 `id`、`path`、`format`）和 `logs`（每项 `path`、`source`）。资源 ID/日志来源须按插件实际启动契约映射：主配置为 `config:` 加相对文件名；额外资源使用 manifest 中声明的 ID；MXU 的任务回调来自 stdout，其他插件通常来自 file。报告按日志 SHA256 关联，记录发现覆盖、任务结果与运行边界。当前配置与历史配置可能不同，回放结果不能替代当时冻结的计划或真机运行验收。

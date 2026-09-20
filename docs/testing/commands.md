# 测试命令

## 默认命令

以下命令均在 `NexusPipeline/` 根目录执行：

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

`tooling` 递归发现并运行现役 Node 工具测试和 `tools/tests/test_*.py`，同时覆盖前端边界、源码编码、更新策略、资格控制与原生报告解析。

统一 runner 保留实时 stdout/stderr、退出码、超时信息和原生 TAP/TRX/Vitest/Playwright 报告。每次运行的报告位于 `tests/.artifacts/runs/<runId>/reports/`；失败时不删除唯一原始报告。

`unit` 与 `frontend` 支持重复指定 `--group <名称>`；`list --json` 返回本地 registry 的现役分组。旧的 `--affected`、`--dry` 和权限模式入口已退役；正式入口必须实际构建或测试。

`core` 的跨仓库文档检查、`frontend-contract` 与 `ui-runtime` 必须通过 `NEXUS_OFFICIAL_PLUGINS_ROOT` 显式指定同一官方插件源码根目录；不会根据相邻目录猜测来源。文档检查需保留链接引用的 Git 历史（CI 使用完整检出）。`contract` 加载官方插件构建模块并注册真实宿主公共元素；入口会在宿主 `frontend/`、宿主 `tools/` 与该插件根目录安装前端依赖，并执行插件仓库的 `npm run build:frontend`。

完整组合入口包括 frontend Vitest、类型检查和前端构建；`frontend` 入口执行完整前端验证。

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

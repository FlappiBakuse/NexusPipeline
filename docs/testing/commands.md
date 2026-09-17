# 测试命令

## 默认命令

以下命令均在 `NexusPipeline/` 根目录执行：

```text
dotnet test tests\NexusPipeline.Tests\NexusPipeline.Tests.csproj --nologo -m:1
npm run typecheck --prefix frontend
npm test --prefix frontend
npm run build --prefix frontend
node tests\run.mjs unit
node tests\run.mjs web
node tests\run.mjs contract
node tests\run.mjs docs
node tests\run.mjs tooling
node tests\run.mjs syntax
node tests\run.mjs build
```

`tooling` 校验 CI 影响域、结果汇总与计数、构建指纹、前端边界扫描器、源码编码和更新策略，并扫描实际宿主前端源码。首次使用时执行 `npm ci --prefix tools` 安装解析器依赖。`changes` 作业在判定影响域之前验证影响域映射。

`unit` 与 `frontend` 支持重复指定 `--group <名称>`；`list --json` 返回现役分组与测试文件。CI 使用 `--affected` 读取 `NEXUS_CI_EXECUTION_PLAN` 中已校验的分组，包含下游调用方；未知路径按全量处理。

`contract` 从相邻 `NexusPipeline-Plugins`（或 `NEXUS_OFFICIAL_PLUGINS_ROOT`）加载官方插件构建模块，并注册真实宿主公共元素。运行前在两仓库安装前端依赖；插件源码有变化时先执行插件仓库的 `npm run build:frontend`。

完整组合入口包括 frontend Vitest、类型检查和前端构建；`web` 入口执行完整前端验证。

统一组合入口必须显式选择运行模式：

```text
node tests\run.mjs codex default
node tests\run.mjs codex ui
node tests\run.mjs codex system
node tests\run.mjs codex all

node tests\run.mjs admin default
node tests\run.mjs admin ui
node tests\run.mjs admin system
node tests\run.mjs admin all
```

System Smoke 支持按影响域分组运行，便于 CI 与本地只跑受影响的 suite：

```text
node tests\run.mjs codex system [runtime|control|config|execution|judge|emulator|plugins|update] [--realtime] [--dry]
```

可以列出多个分组，也可以用 `--group <名称>` 重复指定；省略分组等于全部 suite。未知分组会打印可用分组并以 exit code 2 退出。`--dry` 只列出将要执行的 suite，不构建也不启动运行时。

`codex` 使用 `NexusTestHost=true` 的 `asInvoker` Test Host；`admin` 使用生产 release，并要求 Administrator / High Integrity 或 System Integrity。权限不足返回 exit code `2`，不降级运行。

System Smoke 的 `emulator` suite 覆盖宿主内置 Generic ADB、MuMuManager、无扩展时 Generic ADB 回退，以及通过真实 managed-code TestPlugin 注册的 API v1.7 provider 执行、截图和实例清理。雷电、夜神和 BlueStacks 的厂商命令与探测在 `NexusPipeline-Plugins/plugins/general/EmulatorSupport` 的插件测试中验证；真实设备矩阵由插件仓库维护。`update` suite 的 8 个用例覆盖启动前宿主更新检查、安装与恢复及既有运行期更新；启动失败冷却由 `StartupUpdateAttemptStore` 和 `StartupUpdateCoordinator` 单元测试覆盖。



## 质量门禁顺序

1. 修改宿主代码、测试或前端纯函数后运行 Unit/Component、Web Logic、Docs、Syntax、UI Smoke（适用时）和 `build.cmd` 的适用组合。
2. 涉及配置交换、Windows 进程、端口、解释器、插件、模拟器或更新事务时，追加 `node tests\run.mjs codex system`；宿主模拟器系统边界通过 TestPlugin 验证 provider 注册到执行及清理，厂商驱动行为由官方扩展插件测试覆盖。
3. 发布前在适用的 CI 触发路径中由管理员上下文执行 `node tests\run.mjs admin default`、`admin ui` 和适用的 `admin system`，并核对每项 exit code 为 `0`；计划任务与手动回归会执行完整组合。
4. 两仓库的宿主—插件契约发生变化时，在插件仓库执行 `python tools/repository.py validate-source`、`node tools/Test-FrontendPlugins.mjs` 和 `python -m unittest discover -s tools/tests -v`，并核对两仓库文档、manifest 和测试。
5. 修改 `frontend/src/plugin-bridge/**`、`frontend/src/platform/appearance.ts`、公开 `nxp-*` 元素或 Frontend API 契约时，必须执行 `NexusPipeline-Plugins/tools/Test-FrontendPlugins.mjs` 和宿主 `node tests/run.mjs contract`。前者使用 mock host 验证插件业务与生命周期，从实际宿主检出的 `NEXUS_PUBLIC_ELEMENTS` 读取公开清单；后者加载真实公共元素完成装配交互验证。两仓库 CI 分别通过 `plugins.lock.json` 和 `host.lock.json` 固定对方 commit；手动候选验证支持完整 SHA 输入。managed-code 构建与打包共享宿主锁定规则。

   脚本当前校验：`frontend.apiVersion` 必须为 1.5、宿主私有结构 class 拒绝、`nxp-*` 元素必须来自公开元素集合、插件不得复制 Nexus UI 组件，并按插件页面形态检查公开契约。slot 插件验证 renderer、所需公开控件和 cleanup；GameCheckIn route 页面验证导航/页面注册、任务 API 行为、离开后资源释放和再次进入。CustomWallpaper 额外覆盖插件级壁纸运行时：激活即应用背景与配色、离开设置页面保留全局外观、页面访问不触发随机轮换、按时间轮换在无设置页面时继续生效、只有插件停用才清理外观与计时器。

构建、测试和发布命令保持实时输出；失败时保留失败项、原因摘要和必要的 runtime 证据。长任务不使用无反馈的超长等待。

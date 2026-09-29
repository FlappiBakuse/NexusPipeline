# 测试命令

## 默认命令

以下命令均在仓库根目录执行：

```text
node tests/run.mjs list --json
node tests/run.mjs ci --group backend
node tests/run.mjs ci --group frontend
node tests/run.mjs smoke
node tests/run.mjs daily
node tests/run.mjs daily --group schedule
node tests/run.mjs diagnostic --group store
node tests/run.mjs integration
node tests/run.mjs release
```

`ci` 必须指定一个已知分组。backend 选择真实后端规则、文件事务、执行与调度窄测试；frontend 运行类型检查及核心状态、用户配置请求与公开桥接测试。`tests/policy.json` 中的预期用例与实际原生报告严格匹配。新增或更名选中用例时同步清单，不能删除失败用例以绕过验证。

`smoke` 与两条 `ci` 使用同一实现，并行执行 backend/frontend，共享 180 秒父预算，最后 10 秒预留收尾。输入识别、隔离复制、必要增量构建、依赖准备、测试、报告和清理均计入预算。失败保留原退出码；超时返回 5，清理失败返回 6，主动取消返回 130。报告不完整返回 4。未知命令或参数返回 2。

`daily` 使用两个独立 Host 槽位，先启动真实分钟调度，另一槽运行执行和配置，空闲槽接续控制面；共享准备、四组工作及清理合计 180 秒。支持 `--group execution|config|control|schedule` 进行明确的单组诊断。每组保留 `evidence.json`，父级 `daily-evidence.json` 只有全部预期组及清理通过才成功。

| 场景 | 真实证明 | 外部替代 |
|---|---|---|
| H-E01 execution | 浏览器选择队列并运行，6 脚本、3 队列、4 账号，12 次请求、4 次取消、64 次读取，进程退出和历史归属 | 拥有的外部目标程序 |
| H-E02 config | A/B 保存、取消、正常/失败执行，目录配置还原，落盘 journal 后异常终止与两次启动恢复 | 合成配置与拥有的目标进程 |
| H-E03 control | 真实 CLI、MCP HTTP 初始化与运行观察、合法包安装更新、坏 hash 阻断、两次重启交接 | 官方 HTTPS 响应和合成测试插件 |
| H-E04 schedule | 真实分钟计时产生队列执行、结果归属、持久化水位与重启去重 | 拥有的外部目标程序 |

`integration` 保留已有 UI Smoke/System Smoke 诊断，部分 UI 响应由夹具提供；它不是 `daily` 的替代。`release` 是生产构建入口。

`diagnostic --group store` 在同一180秒父预算内构建实际 Test Host 和合成 managed fixture，访问官方 HTTPS catalog/package 地址，由 Test Host 的传输夹具返回固定响应。它验证坏 hash 拒绝、安装事务、默认禁用、显式启用、三次真实重启交接、插件加载和卸载。原生 TAP 与 `store-evidence.json` 位于本次运行目录；这是 H-C09 的安装生命周期诊断，不代表浏览器交互、全部商店恢复矩阵或四组 daily 已完成。

受控 HTTP 实现 `tests/host/TestHostTransport.cs` 仅在 `NexusTestHost=true` 时编入宿主，生产程序不读取 `NEXUS_TEST_HTTP_PLAN`。测试计划必须是绝对路径且 `runId` 匹配 `NEXUS_TEST_RUN_ID`；每项按 HTTP 方法、完整 URI 及可选凭据头/请求体 hash 匹配，未知请求失败，不回退到真实外网。收据只包含场景标识和匹配结果，并在重启后继续消费剩余响应；不会记录凭据、URL或请求体。回环控制面保持真实网络通信。

## 隔离与运行输出

测试不提权或降权，使用当前终端权限。可设置 `NEXUS_TEST_ARTIFACT_ROOT` 为本次专用的绝对路径；目录内 `.nxp-test-artifact-root.json` 内容如下，`directory` 必须填写该目录的实际绝对路径：

```json
{"schemaVersion":1,"owner":"NexusPipeline.Tests","directory":"<absolute test root>"}
```

未显式设置时使用 runner 临时目录或系统临时目录下的 `NexusPipeline.Tests`，已有无归属目录、链接和非法 run ID 均拒绝执行。本地有目录管理要求时先设置进程级 `TEMP`、`TMP` 或上述显式根；不要改变全局环境或登记已有用户目录。

原字节隔离副本位于 `cache/<source-and-toolchain-fingerprint>/Host`。构建使用真实产品源和依赖，测试不修改产品工程。相同输入的缓存复用需匹配源文件和工具链；独占 lease 防止两个命令同时写同一构建图，未完成清理的 lease 保留供检查。后端与前端共享只读源副本，各自使用不同构建输出。

报告位于 `runs/<runId>/<backend|frontend>/`：`commands.log`、原生 `native.trx` 或 `native.json`、`native-counts.json` 和 `summary.json`。只有预期场景/用例、原生计数、源码/输入身份、预算和清理全部一致才给 PASS；报告明确实际被测实现、外部替代及未证明范围。该 PASS 只覆盖所选组，不宣称 CLI、MCP、商店下载、真实调度和 native 设备已通过。

## 质量门禁顺序

`.github/workflows/ci.yml` 并行运行 backend/frontend 两个核心 job；旧 `Host / Required` 名称保留为轻量汇总，检查当前输入、用例/场景、原生计数、清理和 Actions API 返回的完整前置 job 时长。测试 job 限制三分钟，汇总不构建或运行产品。命令通过不等于远端 job 已实测达标；API 缺失或超时使汇总失败。

工具自测与文档检查按改动显式运行，例如：

```text
node --test tests/support/budget.test.mjs
node tools/check-doc-links.mjs
python -m unittest discover -s tools/tests -p test_pe_manifest.py
```

`tools/host_release.py` 继续负责生产构建、清单、打包、来源与发行完整性检查；不改变发行程序管理员清单或稳定包事实。

## 插件与任务协议联调

生产专项适配器的真实 Jint 联调：`dotnet run --project tools/NexusPipeline.TaskProtocolTests -- --plugin-root <Plugins>`。此命令使用显式插件 checkout 和合成夹具；插件仓库的 `python tools/repository.py verify --scope all` 运行相同入口。

跨账号配置与恢复：`dotnet run --project tools/NexusPipeline.TaskProtocolTests -p:NexusTestHost=true -- --plugin-root <Plugins> --account-isolation <报告.json>`。八个生产适配器、两个账号、六种生命周期情景，共 96 项；使用新建目录和合成配置，不启动目标软件。失败时输出并保留该情景的隔离目录。

修改 `frontend/src/plugin-bridge/**`、`frontend/src/platform/appearance.ts`、公开 `nxp-*` 元素或 Frontend API 契约时，在插件仓库执行 `NexusPipeline-Plugins/tools/Test-FrontendPlugins.mjs`，用 mock host 验证插件业务与生命周期；两仓库各自固定一次对端官方完整 SHA。

## 外部实例日志只读回放

`dotnet run --project tools/NexusPipeline.TaskProtocolTests -- --plugin-root <Plugins> --replay-manifest <外部清单.json>` 使用真实 Jint/发现/归并器读取显式声明的实例文件，不把旁边的历史 JSON 当成成功证据，不启动游戏或修改配置。清单和报告必须保留在仓库外。

清单字段：`report` 为外部报告路径；`scenarios` 每项包含 `artifact`、`resources`（每项 `id`、`path`、`format`）和 `logs`（每项 `path`、`source`）。资源 ID/日志来源须按插件实际启动契约映射：主配置为 `config:` 加相对文件名；额外资源使用 manifest 中声明的 ID；MXU 的任务回调来自 stdout，其他插件通常来自 file。报告按日志 SHA256 关联，记录发现覆盖、任务结果与运行边界。当前配置与历史配置可能不同，回放结果不能替代当时冻结的计划或真机运行验收。

## 逐插件 Jint 选择

`tools/NexusPipeline.TaskProtocolTests` 支持 `--plugin-root <Plugins> --plugin <已知专项> --report <新报告路径>`，可附加 `--scenario <fixture文件名去掉.json>`。未知插件、重复参数、空选择和已有报告路径均失败；不通过删除生产数据选择场景。

使用 `NexusTestHost=true` 时先 `dotnet build tools/NexusPipeline.TaskProtocolTests -p:NexusTestHost=true`，再运行实际输出 `bin/test-host/NexusPipeline.TaskProtocolTests/Debug/net8.0-windows/NexusPipeline.TaskProtocolTests.dll` 并传上述参数。全部命令应在外部隔离副本中运行；该工具报告仅证明选中的真实 Jint/发现/归并/配置事务，不表示上游真实设备已经验证。

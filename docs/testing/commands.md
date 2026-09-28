# 测试命令

## 默认命令

以下命令均在仓库根目录执行：

```text
node tests\run.mjs smoke
node tests\run.mjs integration
node tests\run.mjs release
```

| 命令 | 内容 |
|---|---|
| `smoke` | `tests/` 与 `tools/` 下全部 `.mjs` 的逐文件语法检查；核心 xUnit（`tests/NexusPipeline.Tests/`，`NexusTestHost=true`）；前端 `typecheck` 与 Vitest；`tools/check-doc-links.mjs` 文档内链检查 |
| `integration` | 构建前端生产包，发布 `NexusTestHost=true` 的 asInvoker Test Host，再运行 UI Smoke 与 System Smoke |
| `release` | 生产 `requireAdministrator` 构建到 `release/`，并用 `tools/pe_manifest.py` 校验内嵌清单 |

`smoke` 按顺序执行各步骤，任一步非零退出即中止并保留该步原始输出与退出码。未知命令或多余参数打印用法并以 exit code 2 退出。

## 隔离与运行输出

托管层使用 `NexusTestHost=true` 的 asInvoker Test Host：不要求提权、不触发 UAC、不降权，服务、API、进程与系统操作按本地反馈语义运行；每个 suite 使用独立可用端口与独立 runtime 目录，不读写用户实例的进程、端口或数据。

运行输出位于 `tests/.artifacts/runs/<runId>/`，run ID 取自 `NEXUS_TEST_RUN_ID`，缺省按本次进程生成。`integration` 发布的 Test Host 二进制在 `test-host/`，各 suite 的隔离 runtime 在同一 run 目录的独立子目录（UI Smoke 为 `ui/`，System Smoke 为 `runtime/`、`config-runtime/`、`judge-runtime/`、`mcp-runtime/`）。报告写到标准输出：System Smoke 使用 Node 内置 test runner 的 TAP 输出，xUnit 使用 `dotnet test` 控制台输出；UI Smoke 的 Playwright 失败工件留在 `tests/e2e/test-results/`。

嵌套检出过深时，Windows 批处理 fixture 的工作目录可能超出系统启动限制。可设置 `NEXUS_TEST_ARTIFACT_ROOT` 指向本次新建的短路径普通目录；其中 `.nxp-test-artifact-root.json` 必须声明 `schemaVersion: 1`、`owner: "NexusPipeline.Tests"`、`directory` 为该目录的完整路径。runner 与 UI／系统 helper 将运行目录和 Test Host 产物放在其 `runs/<runId>/` 下。缺归属、相对路径、链接或非法 run ID 拒绝执行；默认仍使用仓内 `tests/.artifacts/runs/`。不要把已有用户目录登记为测试目录。

## 质量门禁顺序

1. PR 的唯一自动门禁是 `.github/workflows/ci.yml` 的 `Host / Required` 单作业：`windows-latest`、`timeout-minutes: 20`，检出后安装 .NET 8 与 Node 24，执行 `node tests/run.mjs smoke`。作业不按 diff 选择范围、不条件准备工具链、不上传产物。
2. push 到 `main` 后，`.github/workflows/release.yml` 的 candidate 作业执行 `node tests/run.mjs integration`，再用 `python tools/host_release.py candidate` 构建并校验生产候选；候选要求源码工作树干净且可从受保护 `main` 到达。
3. 本地生产验收执行 `node tests\run.mjs release` 或等价的 `build.cmd`，两者都以发行 `requireAdministrator` 清单为准。`integration` 与 `release` 都会写出 `.generated/frontend-build.hash`；候选的 `--frontend-ready` 按该指纹确认 `frontend/dist` 与前端源码一致。
4. `tools/host_release.py` 承担生产发布边界，子命令为 `candidate`（构建并校验候选）、`release`（默认，构建生产包）、`verify-package`、`verify-installer`、`extract-candidate`、`inspect-candidate` 和 `validate-candidate`（后三者解包并独立校验已产出的候选，不重新编译）。
5. `tools/tests/` 下的 `test_host_release.py`、`test_host_installer.py`、`test_pe_manifest.py` 和 `test_host_candidate_source.py` 不进入任何自动门禁，需要时手工执行 `python -m unittest`。

## 插件与任务协议联调

生产专项适配器的真实 Jint 联调：`dotnet run --project tools/NexusPipeline.TaskProtocolTests -- --plugin-root <Plugins>`。此命令使用显式插件 checkout 和合成夹具；插件仓库的 `python tools/repository.py verify --scope all` 运行相同入口。

跨账号配置与恢复：`dotnet run --project tools/NexusPipeline.TaskProtocolTests -p:NexusTestHost=true -- --plugin-root <Plugins> --account-isolation <报告.json>`。八个生产适配器、两个账号、六种生命周期情景，共 96 项；使用新建目录和合成配置，不启动目标软件。失败时输出并保留该情景的隔离目录。

修改 `frontend/src/plugin-bridge/**`、`frontend/src/platform/appearance.ts`、公开 `nxp-*` 元素或 Frontend API 契约时，在插件仓库执行 `NexusPipeline-Plugins/tools/Test-FrontendPlugins.mjs`，用 mock host 验证插件业务与生命周期；两仓库各自固定一次对端官方完整 SHA。

## 外部实例日志只读回放

`dotnet run --project tools/NexusPipeline.TaskProtocolTests -- --plugin-root <Plugins> --replay-manifest <外部清单.json>` 使用真实 Jint/发现/归并器读取显式声明的实例文件，不把旁边的历史 JSON 当成成功证据，不启动游戏或修改配置。清单和报告必须保留在仓库外。

清单字段：`report` 为外部报告路径；`scenarios` 每项包含 `artifact`、`resources`（每项 `id`、`path`、`format`）和 `logs`（每项 `path`、`source`）。资源 ID/日志来源须按插件实际启动契约映射：主配置为 `config:` 加相对文件名；额外资源使用 manifest 中声明的 ID；MXU 的任务回调来自 stdout，其他插件通常来自 file。报告按日志 SHA256 关联，记录发现覆盖、任务结果与运行边界。当前配置与历史配置可能不同，回放结果不能替代当时冻结的计划或真机运行验收。

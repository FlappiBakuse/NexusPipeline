# 版本发布

## 发布流程

> **发布权**：commit、push、tag、Pull Request 和 Release 由项目维护者按根目录 `AGENTS.md` 授权规则执行。未经明确授权不得发布。

### 版本号规则

- 采用受限 Nexus 版本 `X.Y.Z`、`X.Y.Z-beta.N` 或 `X.Y.Z-rc.N`，tag 为对应版本前加 `v`；版本比较遵循 `beta < rc < stable`。`fix`、`perf` 和文档/工程治理的补丁性变更使用 PATCH，`feat` 使用 MINOR，带 `!` 或 `BREAKING CHANGE` 的变更按项目当前阶段升级。
- GitHub Release 分类按宿主项目发布策略执行：`major=0` 的所有版本均为 Pre-release；`major>=1` 时，带 `-beta.N` 或 `-rc.N` 后缀的版本为 Pre-release；`major>=1` 且无预发布后缀的版本为正式 Release。
- 用户指定新版本并开始开发后，立即同步 `src/NexusPipeline.csproj` 的 `<Version>` 和版本展示所需配置；发布流程不重复 bump。
- 版本开发期间的本地 `backup/vX.Y.Z-*` 还原点只存在本地，不推送到 origin。

### 发布前置

1. 确认版本开发计划、CHANGELOG 与 `docs/STATUS.md` 已反映当前状态；
2. 按[测试命令](../testing/commands.md)执行默认质量门禁，并运行修改范围适用的 System Smoke、Stress 或 Soak；
3. 确认 `git diff --check` 通过，工作树中没有运行产物、用户配置、日志、密钥和测试 runtime；
4. 核对发布包只包含程序运行所需文件，用户配置和运行数据不进入资产；
5. 确认 Release Notes 使用当前版本的真实变更，SHA 资产与 zip 一一对应。

### 发布步骤

以下步骤需要维护者明确授权：

当前阶段的新候选路径在受保护 `main` 每次 push 后运行 Host Release 的 `candidate` job；也可从 `main` 显式选择 `operation=candidate` 和受保护 main 历史中的 `source_ref` 手动重建已过期候选。candidate 不需要 tag：它用固定源码构建隔离 Test Host，完成 UI、系统与独立真实计时检查，再用同一 production staging 生成 ZIP 和 Setup。Inno Setup 6.7.3 的下载包与编译器分别验固定 SHA256；两种分发物及各自纯 SHA 侧文件进入同一 `host-candidate-<runId>-<attempt>`，候选清单还记录 ZIP/Setup 构建元数据。失败报告使用独立 diagnostics artifact；候选不会创建或移动 tag，也不会自行发布。

已有 tag 指向候选源码且候选 job 真正成功时，发布者可从 `main` 手动执行同包恢复：

```text
gh workflow run release.yml --ref main -f operation=publish-only -f tag=<已有 tag> -f candidate_run_id=<原候选 run ID>
```

同一 run 出现多个成功候选 artifact 时，再传 `-f candidate_artifact_id=<服务端 artifact ID>`。独立 writer 核对原 attempt/job、服务端 artifact 摘要、候选清单、Git tree、现有 tag、ZIP 与 Setup 来源及摘要；只复用原候选，不重新编译。四个分发资产（ZIP、Setup 与各自 SHA 侧文件）采用固定白名单；同 tag 同资产不同字节拒绝覆盖，远端逐项读回通过后才公开 Release。仅在本地合成 run ID 打出的候选不能用于发布。

新版本需先在受保护 `main` 的对应合并提交完成候选验收；获授权后才能创建并推送指向该提交的 tag。已有 tag 不因发布工具修复而移动。Release Notes 依据该 tag 的真实变更写入 UTF-8 无 BOM 文件；独立下载 ZIP/SHA 复核后，再按授权更新 Release 正文并检查更新可见性。控制工具修复通过正常源码 PR 和 Required 检查；恢复始终使用原候选，已有资产必须字节一致。

### 资产与 SHA 规则

| 项目 | 规则 |
|---|---|
| tag | `vX.Y.Z`、`vX.Y.Z-beta.N` 或 `vX.Y.Z-rc.N` |
| Release 标题 | `vX.Y.Z` |
| Pre-release | `major=0` 或版本带 `-beta.N` / `-rc.N` 后缀时使用 `--prerelease` |
| zip 资产 | `NexusPipeline-vX.Y.Z[-beta.N|-rc.N]-win-x64.zip` |
| SHA 资产 | 对应 zip 文件名追加 `.sha256` |

发布包采用扁平根布局：

```text
nexus-pipeline.exe
wwwroot/
plugins/
README.md
```

主程序更新引擎交换 `nexus-pipeline.exe`、`wwwroot/` 和包内提供的 `README.md`，不会覆盖运行时 `plugins/`。包内排除 `config/`、`data/`、`history/` 和 `logs/`。更新引擎支持当前发布包布局，并拒绝绝对路径、`..` 路径和重复目录条目。已发行 v0.16.8 的旧 worker 及 README 收尾限制见[自动更新](../architecture/update.md)。

### 安装器运行时依赖

Setup 只认可标准 x64 安装路径下的 .NET 8 Desktop Runtime 和 ASP.NET Core Runtime；不以 SDK、PATH、x86 或其他主版本替代。每个缺失框架分别请求用户确认，再从 `tools/runtime-dependencies.json` 的固定微软 URL 下载，核验 SHA256 和微软有效数字签名后执行官方安装包。下载页显示进度并允许取消；取消或下载、摘要、签名错误阻止应用部署，用户可返回重试。静默安装缺依赖时退出并提示使用交互向导。

依赖安装退出码 0 后重新检测实际框架；3010 提示用户自行重启再运行 Setup，不强制重启，选择稍后重启时 Setup 返回 Inno 标准退出码 8，且不部署或登记应用；其他依赖失败使 Setup 返回 7。依赖包的退出码与 Setup 退出码分别记录。安装、升级与卸载不移除共享 .NET。实际 Windows 验收必须记录运行环境、依赖包摘要和签名、子进程退出码以及应用部署顺序；编译 Setup 不能代替该项验收。

Setup 的文件解压完成不等于安装成功。新装须完成实例归属登记；升级须等独立 worker 返回成功，且新 Host 启动核对及事务提交完成。登记或交接异常时完成页显示“安装未完成”，即使 Inno 已结束文件安装，进程仍返回非零退出码 12，并保留诊断和恢复现场。

交互卸载先选择保留或删除此实例的数据，默认保留；该选择和最终卸载确认都可以取消，确认之前不会删除文件。最终确认后再次核对归属、运行状态，由既有 helper 持有单实例锁执行删除；恢复现场或归属核对失败使卸载中止并保留登记。静默卸载始终保留数据。

卸载先检查当前确权清单中的全部应用路径，任一路径包含链接就整体拒绝，不能先删除其他应用文件再发现链接。管理身份核对使用本次安装器的临时结果文件，仅接受 DPAPI 解封、当前用户、目录和 helper 摘要全部核验后的状态；不明非空目录在静默模式下直接失败并记日志。内置更新刷新当前载荷确权清单，旧卸载器据此移除新程序，保留不在清单中的用户文件。

“闲时自动更新”与“定期检查更新”同时开启时，宿主在每次启动恢复完成后、初始化服务前检查更新；检查预算为 30 秒、下载预算为 5 分钟，若发现可安全应用的新版本或已有 `Ready` 暂存就先应用再启动。失败或超时会继续启动当前版本，同一目标自动失败后冷却 12 小时；无法安全完成更新恢复时停止服务启动。运行期原有等待闲时应用和手动检查、下载、应用流程继续生效。单独的“插件自动更新”开关会在插件事务恢复与加载前暂存所有符合 catalog 的已安装插件（包括已禁用项；手动安装项须与 catalog artifact 精确匹配），运行期每 12 小时检查并在维护租约空闲后安排安全重启；插件启用偏好保留。

SHA 文件内容为纯 hash，不含文件名和空格，使用 UTF-8 无 BOM。PowerShell 示例：

```powershell
$zip = "NexusPipeline-vX.Y.Z-win-x64.zip"
Get-FileHash $zip -Algorithm SHA256 | ForEach-Object { $_.Hash.ToLower() } |
    Set-Content -Path "$zip.sha256" -Encoding ascii -NoNewline
```

更新引擎可见性自检：

- Release 必须同时具备 zip 与 sha256 资产；缺少任一项时更新清单会跳过该版本；
- 上传后在本机设置页点击「检查更新」，或调用 `POST /api/update/check`，确认更新源识别到刚发布的 tag 与两项资产；
- 无法以管理员上下文启动宿主时，用 `python tools/update-visibility-check.py [vX.Y.Z[-beta.N|-rc.N]]` 按同一契约核对默认更新源的发布列表、tag 解析、宿主 Release 分类、资产命名、下载主机白名单与资产哈希；省略参数时读取当前 `src/NexusPipeline.csproj` 版本；
- 如果检查不到，先核对 `gh release view vX.Y.Z` 的资产列表、资产命名和 zip 根布局。

### 更新策略与破坏性版本屏障

`update-policy.json` 位于仓库根目录，桥接版本发布时保持有效。未来改变更新器无法安全处理的安装布局前，在该文件的 `barriers` 数组追加一条按版本递增的记录：

```json
{
  "version": "0.17.0",
  "code": "installation-layout-v2",
  "migrationUrl": "https://github.com/FlappiBakuse/NexusPipeline/releases/tag/v0.17.0"
}
```

版本必须使用当前受限格式，`code` 使用小写字母、数字、点、下划线或连字符，`migrationUrl` 使用 HTTPS。宿主从桥接版本开始检查当前版本到目标版本之间的所有屏障；命中后保留更新发现结果，禁止内置下载、启动前自动应用、下次启动应用和闲时自动应用，页面显示手动下载安装包与迁移配置的指引。策略文件使用独立的 policy URI 安全域：默认源固定为官方仓库 main 分支的 `update-policy.json`，自定义源使用同源地址，重定向继续按 policy 规则校验。策略文件无法验证时同样禁止内置下载，页面显示策略暂不可验证。仓库根目录策略文件由生产解析器校验，CI 另行校验 barrier 历史只能追加且既有记录不可删除、修改或重排。发布破坏性版本前需先提交策略文件，再发布对应版本，并在 Release Notes 写明迁移步骤。

### Release Notes 格式

```text
## vX.Y.Z[-beta.N|-rc.N]（按宿主发布策略决定是否附加「Pre-release」）

`major=0` 或带 `-beta.N` / `-rc.N` 后缀时，标题附加「Pre-release」；`major>=1` 且无后缀时不附加。

### 功能分组标题
- 要点一
- 要点二

### 另一个分组
- 要点一

SHA256：见附件对应版本的 `.sha256` 校验文件
```

按用户价值或工程主题分组，列出可核对的结果。版本历史的完整记录进入 [CHANGELOG.md](../../CHANGELOG.md)。

### gh 与 PowerShell 操作注意事项

1. 修改已发布 Release 的正文或资产前，先通过 `gh api` 备份原正文到本地文件；
2. 多行 gh 输出在 PowerShell 中可能成为字符串数组，写入文件前显式合并换行；
3. 含中文的 Release Notes 使用 UTF-8 无 BOM 文件和 `--notes-file`，避免命令行转义与编码转换；
4. 修改已发布 Release 属于外部状态变更，先确认授权和目标版本。

### 发布后收尾

- 将发布版本的已知问题状态同步到 [STATUS.md](../STATUS.md)，并移出已完成计划；
- 确认远端 Release 资产上传成功、下载复核和 SHA256 校验全部通过；
- 完成确认后，清理项目内本次发布的 zip、`.sha256`、Release Notes 临时文件和打包暂存目录；
- 清理仅针对当前项目内已核对的精确路径，不删除源码、测试、插件、用户运行数据或后续开发所需目录；
- 备份 tag 只保留最近三个版本的现存里程碑，删除旧 tag 前先核对保留清单和删除清单。

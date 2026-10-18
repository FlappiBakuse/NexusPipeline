# 版本发布

## 发布流程

> **发布权**：commit、push、tag、Pull Request 和 Release 由项目维护者按根目录 `AGENTS.md` 授权规则执行。未经明确授权不得发布。

### 版本号规则

- 采用受限 Nexus 版本 `X.Y.Z`、`X.Y.Z-beta.N` 或 `X.Y.Z-rc.N`，tag 为对应版本前加 `v`；版本比较遵循 `beta < rc < stable`。`fix`、`perf` 和文档/工程治理的补丁性变更使用 PATCH，`feat` 使用 MINOR，带 `!` 或 `BREAKING CHANGE` 的变更按项目当前阶段升级。
- GitHub Release 分类按宿主项目发布策略执行：`major=0` 的所有版本均为 Pre-release；`major>=1` 时，带 `-beta.N` 或 `-rc.N` 后缀的版本为 Pre-release；`major>=1` 且无预发布后缀的版本为正式 Release。
- 用户指定新版本并开始开发后，立即同步 `src/NexusPipeline.csproj` 的 `<Version>` 和版本展示所需配置；发布流程不重复 bump。
- 开发检查点保存仓库外原字节、HEAD 和 diff；标签另需授权。

### 更新日志保留

宿主更新日志只维护最近两个已发布的 `major.minor` 系列：根 `CHANGELOG.md` 保存当前系列，`docs/history/changelog-earlier.md` 保存上一系列。新增下一系列的首个发布记录时，将原当前系列完整移入 earlier，根文件改为新系列，删除更旧系列的文档记录并更新历史索引和引用。例如新增 v0.18.X 发布记录后，根文件保留 v0.18.X，earlier 保留 v0.17.X。

发布前人工核对两份文件的版本范围、顺序、记录完整性与导航链接，不改写保留版本的发布事实。该窗口仅用于宿主更新日志文档；Git 历史、GitHub Releases、只追加的更新屏障及插件发行记录不随之裁剪。

### 发布前置

1. 确认版本开发计划、CHANGELOG 与 `docs/status.md` 已反映当前状态；
2. 按[测试命令](../testing/commands.md)执行默认质量门禁，并运行修改范围适用的 System Smoke；
3. 确认 `git diff --check` 通过，工作树中没有运行产物、用户配置、日志、密钥和测试 runtime；
4. 核对发布包只包含程序运行所需文件，用户配置和运行数据不进入资产；
5. 确认 Release Notes 使用当前版本的真实变更，GitHub 资产 digest、尺寸与本地候选字节一致。

### 发布步骤

完整候选与实际发布均仅由 `workflow_dispatch` 发起，main push 不触发候选或 writer。

以下步骤需要维护者明确授权：

从受保护 `main` 手动选择 `operation=candidate` 的 Host 发布工作流按固定 source plan 依序执行 frontend、bundled、host、package-verify、installer 与 candidate 汇总作业；`source_ref` 必须是受保护 main 历史中的可达引用。candidate 不需要 tag：它用固定源码生成真实前端、生产 Host、桌面和预装插件，核验载荷并生成 Portable、Update 和 Setup。Inno Setup 6.7.3 的下载包与编译器分别验固定 SHA256；三个分发物进入同一 `host-candidate-<runId>-<attempt>`，候选清单还记录 ZIP/Setup 构建元数据。失败报告使用独立 diagnostics artifact；候选不会创建或移动 tag，也不会自行发布。

已有 tag 指向候选源码且候选 job 真正成功时，发布者可从 `main` 手动执行同包恢复：

```text
gh workflow run release.yml --ref main -f operation=publish-only -f tag=<已有 tag> -f candidate_run_id=<原候选 run ID>
```

同一 run 出现多个成功候选 artifact 时，再传 `-f candidate_artifact_id=<服务端 artifact ID>`。独立 writer 核对原 attempt/job、服务端 artifact 摘要、候选清单、Git tree、现有 tag、ZIP 与 Setup 来源及摘要；只复用原候选，不重新编译。公开资产白名单精确为 Setup.exe、Portable.zip、Update.zip；增量配方和覆盖说明位于包内，发现元数据在仓库 v2 端点维护；同 tag 同资产不同字节拒绝覆盖，远端逐项读回及 GitHub digest、尺寸、ID 核验通过后才公开 Release。仅在本地合成 run ID 打出的候选不能用于发布。

新版本需先在受保护 `main` 的对应合并提交完成候选验收；获授权后才能创建并推送指向该提交的 tag。已有 tag 不因发布工具修复而移动。Release Notes 依据该 tag 的真实变更写入 UTF-8 无 BOM 文件；独立下载 ZIP 并按服务端 digest 复核后，再按授权更新 Release 正文并检查更新可见性。控制工具修复通过正常源码 PR 和 `Host / 构建检查`；恢复始终使用原候选，已有资产必须字节一致。

### 预装插件解析

源码 `src/Modules/Plugins/Repository/BundledPlugins.json` 只选择 EmulatorSupport 和 LiveScreenshot，不固定版本。候选在一次 source plan 固定的 Plugins SHA 中读取稳定 catalog 与对应 ZIP，检查 catalog 投影、包摘要与大小、最低宿主版本、manifest 身份及安全条目，再生成含精确版本和逐文件摘要的 `BundledPlugins.json`。生成清单与插件载荷一起进入 bundled 阶段；生产编译通过 `NexusBundledPluginsManifest` 嵌入同一份清单，首次安装仍按精确字节确权。

本机验收及 PR 生产编译使用相同解析器。消费阶段核对固定两仓 SHA、完整阶段指纹和清单与载荷对应关系；writer 只复用已验证候选，不重新获取最新 catalog 或包。稳定 catalog 发布新插件版本后，下次 Host 候选自动选择该快照中的版本；不改变已构建候选或已公开发行物。

### 资产与 SHA 规则

| 项目 | 规则 |
|---|---|
| tag | `vX.Y.Z`、`vX.Y.Z-beta.N` 或 `vX.Y.Z-rc.N` |
| Release 标题 | `vX.Y.Z` |
| Pre-release | `major=0` 或版本带 `-beta.N` / `-rc.N` 后缀时使用 `--prerelease` |
| 便携资产 | `NexusPipeline-vX.Y.Z[-beta.N|-rc.N]-win-x64-Portable.zip` |
| 安装资产 | `NexusPipeline-vX.Y.Z[-beta.N|-rc.N]-win-x64-Setup.exe` |
| 增量资产 | `NexusPipeline-vX.Y.Z[-beta.N|-rc.N]-win-x64-Update.zip` |
| 资产完整性 | GitHub Release API 的 `asset.digest=sha256:<64 位摘要>`、资产 ID 和尺寸 |

发布包采用扁平根布局：

```text
NexusPipeline.exe
NexusPipeline.update.exe
README.md
resources/desktop/
resources/payload-manifest.json
plugins/
```

主程序更新引擎按配方与完整载荷清单交换 `NexusPipeline.exe`、`NexusPipeline.update.exe`、`resources/desktop/`、`resources/payload-manifest.json` 和包内提供的 `README.md`，不会覆盖运行时 `plugins/`。安装版的 `NexusPipeline.uninstall.exe` 单独归属安装工具，不放入公共 ZIP。包内排除 `config/`、`data/`、`history/` 和 `logs/`。更新引擎支持当前发布包布局，并拒绝绝对路径、`..` 路径和重复目录条目。应用载荷作为同一事务交换、校验和回滚；插件和用户数据不纳入应用事务。

### 安装器运行时依赖

Setup 只认可标准 x64 安装路径下的 .NET 10 Desktop Runtime 和 ASP.NET Core Runtime；不以 SDK、PATH、x86 或其他主版本替代。每个缺失框架分别请求用户确认，再从 `tools/runtime-dependencies.json` 的固定微软 URL 下载，核验 SHA256 和微软有效数字签名后执行官方安装包。下载页显示进度并允许取消；取消或下载、摘要、签名错误阻止应用部署，用户可返回重试。静默安装缺依赖时退出并提示使用交互向导。

依赖安装退出码 0 后重新检测实际框架；3010 提示用户自行重启再运行 Setup，不强制重启，选择稍后重启时 Setup 返回 Inno 标准退出码 8，且不部署或登记应用；其他依赖失败使 Setup 返回 7。依赖包的退出码与 Setup 退出码分别记录。安装、升级与卸载不移除共享 .NET。实际 Windows 验收必须记录运行环境、依赖包摘要和签名、子进程退出码以及应用部署顺序；编译 Setup 不能代替该项验收。

安装验收需确认实例归属登记、更新交接与启动回执、失败补偿及卸载数据选择，不能将 Inno 解压结束视为安装成功。交接、元数据与卸载的完整运行语义见[自动更新架构](../architecture/update.md)，验收入口见[测试命令](../testing/commands.md)。

从 v0.17.3 起，发布包与安装器不生成或上传独立 SHA 附件。候选清单与构建元数据保存本地摘要，writer 上传后逐项核对 GitHub 的资产 ID、唯一名称、`uploaded` 状态、尺寸、`digest`，再下载校验原字节。缺少服务端摘要时拒绝公开 Release。

更新器只接受服务端摘要完整的 ZIP，逐块检查尺寸和 SHA256。缺失、重复、类型错误或不匹配的资产不能下载应用。下载后生成的本地校验收据用于现役恢复事务，不能作为独立发行附件上传。

v0.17.2 及更早版本要求 SHA 附件，用户首次进入此更新格式需手动使用完整 ZIP 或 Setup 安装 v0.17.3。此桥接限制写入对应 Release Notes；不为旧客户端保留 SHA 附件，也不以移除屏障绕过安装代次限制。

更新可见性自检使用 GitHub Release API 核对 tag、Pre-release 分类与资产身份，再在设置页检查更新；本地隔离源必须提供相同资产字段，不能只提供下载 URL。

### 更新策略与破坏性版本屏障

`metadata/updates/legacy-policy.json` 是旧更新屏障的 canonical；根目录 `update-policy.json` 继续作为已发布客户端固定 URL 的真实 JSON 投影。编辑 canonical 后运行 `python tools/update_metadata.py generate`，通过正常 PR 同时提交两份字节一致的文件。未来改变更新器无法安全处理的安装布局前，在 canonical 的 `barriers` 数组追加一条按版本递增的记录：

```json
{
  "version": "0.17.0",
  "code": "desktop-shell-embedded-ui",
  "migrationUrl": "https://github.com/FlappiBakuse/NexusPipeline/blob/main/docs/user/README.md"
}
```

版本必须使用当前受限格式，`code` 使用小写字母、数字、点、下划线或连字符，`migrationUrl` 使用 HTTPS。宿主从桥接版本开始检查当前版本到目标版本之间的所有屏障；命中后保留更新发现结果，禁止内置下载、启动前自动应用、下次启动应用和闲时自动应用，页面显示手动下载安装包与迁移配置的指引。策略文件使用独立的 policy URI 安全域：默认源固定为官方仓库 main 分支的 `metadata/updates/legacy-policy.json`，已发布客户端继续读取字节一致的根投影，自定义源使用同源地址，重定向继续按 policy 规则校验。策略文件无法验证时同样禁止内置下载，页面显示策略暂不可验证。生产检查校验 canonical/投影一致性及屏障历史前缀。barrier 历史只追加，既有记录不删除、不修改、不重排。发布破坏性版本前需先提交策略文件，再发布对应版本，并在 Release Notes 写明迁移步骤。

v0.17.0 的 `desktop-shell-embedded-ui` 屏障必须在公开 ZIP、Setup 资产前从官方 main 策略入口回读，记录实际 source SHA 和原文摘要。屏障和新目录安装指南经过受保护 PR 合入后，使用隔离的真实 v0.16.15 发布物确认跨入 v0.17.0、跳到 v0.17.1 均返回 `manualUpdateRequired`、`breaking-update`、`barrierVersion=0.17.0` 和迁移说明，并拒绝下载和应用。保留旧目录及原始用户数据；v0.17.0 到同代后续补丁仍支持普通更新。候选构建、tag 和资产发布依此顺序推进，不把本地策略测试当作公开入口已生效。

### Release Notes 格式

```text
## vX.Y.Z[-beta.N|-rc.N]（按宿主发布策略决定是否附加「Pre-release」）

`major=0` 或带 `-beta.N` / `-rc.N` 后缀时，标题附加「Pre-release」；`major>=1` 且无后缀时不附加。

### 功能分组标题
- 要点一
- 要点二

### 另一个分组
- 要点一

完整性：见 GitHub Release API 对应资产的 SHA256 digest；旧客户端首次安装 v0.17.3 请使用完整 ZIP 或 Setup。
```

按用户价值或工程主题分组，列出可核对的结果。版本历史的完整记录进入 [CHANGELOG.md](../../CHANGELOG.md)。

### gh 与 PowerShell 操作注意事项

1. 修改已发布 Release 的正文或资产前，先通过 `gh api` 备份原正文到本地文件；
2. 多行 gh 输出在 PowerShell 中可能成为字符串数组，写入文件前显式合并换行；
3. 含中文的 Release Notes 使用 UTF-8 无 BOM 文件和 `--notes-file`，避免命令行转义与编码转换；
4. 修改已发布 Release 属于外部状态变更，先确认授权和目标版本。

### 发布后收尾

- 将发布版本的已知问题状态同步到 [未完成问题](../status.md)，并移出已完成计划；
- 确认远端 Release 资产上传成功、下载复核和 SHA256 校验全部通过；
- 完成确认后，按实际验收与发布闭环清理外部临时目录中的可重建产物；
- 清理仅针对已核对归属和使用状态的精确路径，不删除源码、测试、插件、用户运行数据或后续开发所需目录；
- 仓库外原字节检查点和失败证据按实际发布闭环及目录归属处理；标签删除另需授权。

本机 `local-acceptance` receipt 的 `producer=local`、`publishable=false` 不具备候选资格，不传给 publish-only。维护者确认源与行为后，按实际授权提交干净源码，再从受保护 main 手动生成正式候选。

## 三资产升级链

当前发行只提供 Setup、Portable、Update 三个用户资产。v0.17.3 的旧 reader 保留原发现协议；进入 v0.17.4 使用新 Setup 首跳或包内精确基线说明，不能宣称旧 reader 原生支持新三资产。新 reader 认证同一 Release 的资产 ID、长度与服务端 digest 后读取包内配方，按精确基线复用，传输不可用或基线不匹配时最多回退一次完整包。摘要、结构、安全与恢复错误不触发盲目回退。

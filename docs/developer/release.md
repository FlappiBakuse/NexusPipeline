# 发布检查与源码来源

## PR 生产检查

`Host / 构建检查` 是 main PR 的固定检查，单 job、十分钟、仅 contents:read。
工作流固定 base、head 和实际 checkout，按完整 PR 差异选择生产输入；不运行项目测试。
文档与纯本机测试变化保留成功检查，空生产闭包不启动产品编译器。
未知共享输入扩大到 Host、Maintenance、Web、桌面及两个默认插件；SDK/契约变化覆盖真实预装消费者。
Host 编译隐含当前 Web 资源，桌面使用锁定 Electron 装配器；不会用语法检查替代原生产装配。
桌面应用由固定的官方 `@electron/asar` 生产者和原生 npm 锁文件生成 `resources/app.asar`。装配器、桌面维护测试和原生浏览器 runner 共用有界归档校验，核对全部成员、SHA256 integrity 与当前源码；构建依赖和原始生产记录保留在显式仓外工作区。

需要预装插件时，一次解析官方 Plugins develop 完整 SHA，后续只消费该 checkout。
SDK 从本次 Host 源码构建；独立 SDK 消费者只编译，不执行测试。
PR 调用 `sdk-packages --compile-only`，回执将运行消费者及 lifecycle 标为 NOT_RUN；默认 SDK 验证入口保持原行为。
插件在仓外隔离 checkout 中导入这一 SDK，再调用现役前后端构建和打包入口。
两个原始 checkout 保持干净；派生镜像、构建、依赖与回执位于显式仓外输出根。
PR 包及生产编译不具备正式候选或发布资格。
插件 PR 的受保护生成物包括 canonical catalog、根投影、发行 state 与 packages；两个源码入口均重算完整差异并拒绝手工变更。

工作流入口：

```text
python -B tooling/ci_check.py plan --base <完整baseSHA> --head <完整headSHA> --checkout <实际checkoutSHA> --report <仓外plan.json>
python -B tooling/ci_check.py run --plan <plan.json> --partner-root <固定Plugins checkout> --output <新仓外目录>
```

不需要对端时省略 `--partner-root`。plan 输出实际工具链需求，工作流仅准备相应工具。
网络输入使用仓内 Electron 和插件原生依赖的固定版本与摘要；缺失或不一致使检查失败。
报告包含固定两仓来源、所选闭包、原始命令、退出码、计时及原生日志。

未提交工作只准备离线计划：

```text
python -B tooling/ci_check.py plan --local --partner-root <Plugins源码根> --report <新仓外plan.json>
```

该计划记录 local-uncommitted 身份，不能传给 PR 的 run 入口；本地编译继续使用现役开发命令。

## 原字节来源

默认 `snapshot` 和本地构建保留 `sourceMode=local-uncommitted`、null commit/tree，包含逐文件摘要。
即使工作树干净，也不会因环境变量、run ID 或手填 SHA 获得其他来源身份。
`hasUncommittedChanges` 单独记录实际工作树状态。

```text
python -B tooling/nexus.py snapshot --git-commit <完整HEAD SHA> --output <新仓外目录>
```

显式 Git 导出要求 HEAD 精确一致、tracked/untracked 工作树干净、完整成员集合一致，
逐项对比 Git blob 与导出原字节，拒绝链接、子模块及检出时的换行转换；导出结束再次核对来源。
通过后记录真实 commit/tree 和 `sourceMode=git-commit`。
Windows 导出清单沿用常规文件模式 100644；Git tree 身份另保留仓库实际模式。
复用已有导出进行普通本地构建时重新生成本地身份，Git commit/tree 不随槽位继承。

Git 导出证明固定源码。正式 writer 还须核对 GitHub 服务端 run、attempt、job、artifact、tag 与字节事实。
当前 PR 报告始终 `publishable=false`，不能作为 publish-only 输入。

## 原候选校验与 Host writer

`tooling/release_check.py host-candidate` 从生产 assets receipt 创建纯数据候选，核对装配源码摘要和
两仓 clean Git 原字节来源。输出为 CANDIDATE_CREATED_NOT_QUALIFIED；环境中的 run ID 不授予发布资格。
输入使用 `--source-root`、`--source-commit`、`--partner-root`、`--partner-commit`、
`--assets-receipt` 和新的仓外 `--output`，控制入口为官方 main 的手动 release 工作流。

`tooling/release_check.py publish-only --kind host --run-id <原run> --writer-id <当前run>
--artifact-id <原artifact> --output <新仓外目录>` 默认只校验。
它从 GitHub 读取官方工作流、原 attempt 的成功 job、artifact 归属/时间/摘要以及两仓 commit/tree 和
受保护分支可达性；下载原 artifact 后核对完整文件清单，只读取候选数据。
同一次 run 不能同时作为原候选和 writer，Test/local 来源拒绝。

显式 `--publish` 还要求当前 writer 的服务端身份与实际 main 控制 checkout 原字节一致。
Host tag 必须已经指向候选源码，0.18 屏障须先从官方 main 固定 SHA 回读。
同 tag 同名不同字节、重复名称、额外资产和缺失的公开资产均拒绝；每个资产核对服务端 ID、
uploaded、size、digest 并下载回读，全组复核后才公开 Release。writer 不调用编译器和包生产者。
正式发布还需后续完整资产资格、候选工作流和生产信任；此入口的本地受控测试不替代这些验收。

## 旧客户端发现

`metadata/updates/legacy-policy.json` 为历史屏障 canonical，`update-policy.json` 保留同字节投影。
`tooling/legacy_updates.py check --base origin/main` 验证历史前缀、旧发现 JSON、0.18 屏障和新安装说明；
相关差异已接入 Host PR 检查。官方合入/固定地址回读及真实 0.17.6 客户端验收仍待执行。

## 插件列表元数据

插件的 i18n 字典投影成 manifest metadata，签名 catalog 和 Host 列表消费同一文本及语言集合。
本地列表的创建/更新时间来自已提交安装记录对应的接受时间：首次成功安装与最近成功安装/升级。
卸载后保留原安装记录，再次安装保留首次时间；重启、启用/禁用和仅目录刷新不改写这些时间。
正式 catalog 的日期须来自实际发行事实；未提供时保持空值，列表排序将其置后。

## 单文件发布

`product-publish` 将 Host 与 Maintenance 分别发布为 win-x64、framework-dependent 单文件，
沿用 ReadyToRun 并保持不裁剪。共享 SDK 的原生产 DLL 嵌入两份程序，不复制 pdb、xml 或整套 .NET 运行时。
Host 依赖 .NET 10 Core 与 ASP.NET Core x64 共享框架，Maintenance 依赖 .NET 10 Core。
原生依赖使用 SDK 的单文件提取机制；本机验收须将进程 TEMP/TMP 与 DOTNET_BUNDLE_EXTRACT_BASE_DIR 指向仓外验收缓存。

发布者核对实际 bundle 的框架声明及嵌入 SDK 原字节，再记录单文件输出。
更新准入从同一 bundle 提取 Host/Maintenance 的托管入口读取 Nexus.BuildProfile，
继续拒绝 Test 程序进入 production 入口，不依赖旁置 DLL 或调用候选代码。
仅接受锁定 SDK 产生的未压缩 bundle 6.0；外部格式说明见
[.NET 单文件部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)。

原资产验收使用 `tooling/asset_qualification.py --assets-receipt <assets-receipt.json>
--assembly-receipt <assembly.json> --output <外部新目录> --dotnet-root <已安装的 x64 .NET 10 根目录>`。
验收先将进程级运行时根指向空目录，确认两个可执行文件拒绝缺失的共享运行时，
再以显式安装根验证 Portable、Setup、默认插件和卸载保留用户数据。

## 公开应用与更新包

装配根直接包含 `NexusPipeline.exe`、`NexusPipeline.update.exe`、`README.md`、
`resources/desktop` 和 `resources/payload-manifest.json`。内部程序集身份保持 Host/Maintenance。
默认插件的原始签名包与分发元数据位于 `plugins/EmulatorSupport`、`plugins/LiveScreenshot`。
无参数启动使用装配根下的 `runtime`；本机装配回执与已有用户数据留在该根，公共 ZIP 排除这些内容。

公开清单 schemaVersion=2，installationGeneration=g0180，文件使用 path/sizeBytes/sha256/kind。
桌面清单绑定 ASAR、包锁和官方 Electron 归档；运行时选择 en-US、zh-CN 两个 locale，
并记录完整选中文件的摘要。生产源码使用实际 Git commit/tree；本地未提交 Test 清单保留 null，
生产入口拒绝该身份。SDK、profile、storeBinding 从受信准备上下文绑定，并核对实际程序的托管身份。

Portable 与 Update 都携带 `package-info/recipe.json` 及中英文说明。
配方 schemaVersion=1，包含、复用、删除文件互斥并精确覆盖目标清单；元数据不进入应用文件树。
首个 0.18 使用完整包。同代增量绑定原 manifest、官方 release/asset ID 与包摘要，
复用内容来自已校验的原应用；拒绝缺失、替换、未知文件和错误基线。

应用事务保持安装根的物理身份，仅交换两个入口、README 与 resources。
runtime、已安装插件及根目录其他材料保留。分组移动支持部分完成后的恢复，
全过程保留 writer/Job 停止证明、文件身份、内容、冻结声明和恢复门禁。

## 后续发布合同

安装器从两份单文件的 runtimeconfig 读取实际框架闭包，只接管 Microsoft.NETCore.App 与 Microsoft.AspNetCore.App x64。
`tooling/runtime-dependencies.json` 固定官方 10.0.14 安装包、10.0.0 最低要求、下载 URL、长度与摘要；
官方来源为 [微软 .NET 10 发行元数据](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json)。
Setup 检查 x64 注册位置及标准目录中的实际共享框架，保留已满足要求的安装。
交互安装先确认，再下载、核对摘要和微软签名；静默缺失、取消、下载或安装失败及安装后仍缺失均在应用文件写入前停止。
依赖返回 3010 时要求重启后重试；共享运行时由系统维护，卸载 NexusPipeline 保留它们。
签名验证显式使用验证器自身的 Windows PowerShell 安全模块，处理从其他 PowerShell 版本启动 Setup 的环境。

`apps/installer` 为 Setup 提供独立的 asInvoker 单文件 worker，复用 Maintenance 的安装命令入口及 Platform 生命周期。
Host/Update 的生产权限清单保持 requireAdministrator；worker 仅随 Setup 部署，不进入 Portable/Update ZIP。
发布者核对三个程序的实际 PE 权限清单、bundle/profile 与共享 SDK 原字节。

`installer-state inspect|check-new|check-install --root <目录>` 只读选择新目录、当前安装或保留数据的实例。
`check-new` 只接受空目标；`check-install` 接受空目标或完整归属的保留实例，当前活动安装仍需同代转换。
`check-transition --root <已有安装> --work-root <仓外诊断目录>` 只读核对活动安装来源：
在启动门禁、独占实例租约和原应用读取租约内，核对实际清单、程序 profile、物理对象、原生登记及完整恢复准入，返回冻结来源。
诊断副本须在应用、runtime 和安装管理目录之外；原始账号、配置和恢复数据保持。该命令不写入安装检查点或交换应用文件。
`begin-transition --root <已有安装> --transaction <GUID> --manifest <SHA256> --manifest-file <目标应用清单> --work-root <仓外诊断目录>`
在完整恢复准入和应用读取租约内，将原活动身份、原生元数据、应用文件与物理对象及完整目标共同冻结到受保护检查点。
同一事务重入核对原来源、目标和 worker 原字节；`cancel-transition --root <已有安装> --transaction <GUID> --work-root <仓外诊断目录>`
仅清理来源未变且尚未写入应用的检查点，用户数据和原安装登记保持；未知字节保留现场。
原生 Setup 根据 check-install 的同代安装结果进入转换入口，保留既有卸载引擎、快捷方式和 runtime。
`preserve-transition --root <已有安装> --transaction <GUID> --work-root <仓外诊断目录>`
通过原检查点保全四个应用根，事务目录位于同一安装根内，文件和目录保留原物理身份；runtime 与用户数据独立。
保全和恢复可接续已知移动前缀，所有路径先完成原字节／物理对象核对，再执行移动；未知内容保持现场。
`cancel-transition` 同时恢复已保全且尚未写入新应用的原程序，并清理本事务拥有的空目录及检查点。

`begin --root <目录> --transaction <GUID> --manifest <SHA256> --manifest-file <目标应用清单> --work-root <仓外检查目录>`
先校验目标清单原字节、摘要、当前公开格式与 profile，再建立受保护检查点，记录完整目标清单、实际父 Setup、写入进程、目录身份和原生元数据。
Setup 从同一签名安装载荷提取固定摘要的目标清单；检查点各阶段保留同一目标，注册依照冻结的完整文件集合核对实际应用。
`register --root <目录> --transaction <GUID>`
核对完整载荷、实际 bundle/profile、原生卸载登记和 runtime，再冻结目标身份与私有 worker 后发布。
保留实例在应用写入前执行完整只读数据库／恢复准入并核验实例独占；重装沿用安装 ID、创建时间、runtime ID 和目录身份。
注册重试保持安装身份；部分发布只续接已记录的文件对象和注册表值，旧归属文件保全到提交完成。
`cancel-prepared` 使用相同 root/transaction 参数，清理空目标或元数据未变且尚未写入应用的保留实例检查点；未知字节和未完成计划保留现场。
这些命令仅接受当前受保护格式。Test 入口和 Setup 使用同一显式 fixture、隔离注册表及快捷方式目录。
快捷方式初次捕获通过 Windows Shell Link 只读加载，核对原始目标、空参数及读取前后的文件摘要和对象身份。
持久接续逐项核对已验证快捷方式的完整字节摘要、文件身份和父目录身份；变化保留现场。元数据目录、全部文件及原生登记继续完整核对。
Setup 在应用写入前持有原生启动门禁并建立检查点，安装完成后登记。安装检查点完整冻结原生元数据；
卸载使用独立绑定的 ownership 范围，只发布安装归属，原生卸载日志由经过身份核验的原生引擎独占维护。
原生卸载器调用相同 worker 保留 runtime，
通过既有持久化设置意图移除已拥有的开机启动任务。正常 Host 拒绝进入安装门禁、保留状态及待恢复检查点。
交互卸载默认保留数据，支持取消和显式删除；静默卸载默认保留，已确认的待删除意图按原选择续做。
显式删除先通过完整只读恢复准入，再冻结 runtime 的内容和物理身份；同卷退役后仅删除匹配的剩余对象。
受保护的删除意图和实际 writer 交接支持中断续做，并阻止 Host 重建 runtime；原生卸载完成后才清除安装归属。
`uninstall-check --root <目录>` 返回门禁名及 `choose|delete` 状态；原生卸载器据此展示默认保留或继续删除的选择。
`uninstall-keep|uninstall-delete` 使用 root、transaction、work-root 参数；`uninstall-finish` 使用 root、transaction，
只接受已完成原生移除的调用方。三个写入口都验证实际父进程、冻结 worker 和原生引擎。
维护更新准备冻结已有安装归属、原生登记与文件身份，随已验签来源持久保存；Portable 更新不写安装归属。
新 Host 通过启动交接后，提交回执冻结新归属及其文件对象，再发布清单摘要、版本和原生登记；提交投影完成前清理受保护的暂存对象。
已记录提交按同一冻结对象完成发布及清理；启动失败时先验证原归属，恢复旧载荷并取消尚未发布的暂存。
未知字节、目录、登记值或替换对象保留。
`apply-transition --root <已有安装> --transaction <GUID> --archive <安装器内嵌应用ZIP> --work-root <诊断目录>`
在实例独占内验证固定清单、归档路径、全部应用字节和物理对象，持久冻结目标后交换四个应用根。
原生 Setup 在此路径跳过普通文件、快捷方式和卸载器写入；安装登记由同一检查点发布，原 worker 与卸载引擎继续保持配对。
失败取消移除已冻结的目标对象并恢复原应用与登记；提交标记持久化后只完成已提交事务清理。
新 Setup 在目的地检查前调用 `recover-transition --root <目录> --work-root <诊断目录>`。
恢复独占安装门禁，核对原 worker、旧 Setup 与全部 writer 的停止证据及 runtime 准入；
未提交事务恢复原应用对象和登记，已观察提交的事务完成目标核验与清理，然后进入正常安装检查。
旧进程仍存活、来源不匹配或未知对象均保留检查点；恢复不接管其他安装事务。
维护阶段诊断保存在 `runtime/diagnostics`；worker 在释放自身 Job 之前记录阶段、安全拒绝码、HRESULT 和项目调用位置。

`release.yml` 从 main 手动运行。candidate 输入固定 Host 与 Plugins main 完整 SHA，
`release_build.py` 顺序调用现役生产构建、插件包、签名商店、桌面、装配及三资产 owner，并上传唯一原候选。
Host 候选环境提供 NEXUS_SIGNING_KEY、NEXUS_SIGNING_KEY_ID、NEXUS_METADATA_ROOT 和 NEXUS_ARTIFACT_ROOT；
签名公钥、来源根及序号须与源码中的 production 信任一致。私钥只驻留本轮受控临时文件，结束时移除。
签名商店生产者必须提供 `--distribution-catalog`，逐包核对已发布摘要、版本、manifest 和本地化内容后投影发行日期。
publish-only 只下载指定原成功候选，核对服务端 job/run/attempt/artifact 及现有 tag，再按显式 publish 参数写入。
完整三资产资格、同代 Setup 与 stable/preview 分发仍需定向验证和成品验收。
生产信任未配置时保持 PENDING；Test 信任和本地回执不授予正式发行资格。
0.17 到 0.18 使用新目录、新实例，保留旧目录；不提供用户数据迁移。
提交、推送、PR、tag、workflow_dispatch 和正式发布分别需要维护者授权。

## 定向回归

来源与 PR 选择用例归 `tests/tooling/test_release_sources.py`、`test_ci_plans.py`。
工作流结构检查使用测试专用的 `tests/tooling/requirements.txt`，固定 PyYAML 6.0.3；
将该清单安装到显式仓外 Python 依赖目录，并通过进程 `PYTHONPATH` 提供给现役 runner。
按 tooling policy 选择精确方法；纯 Python 回归不构建 Host 或执行项目测试。

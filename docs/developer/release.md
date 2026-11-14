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
PR 调用 `sdk-packages --compile-only`，回执将运行消费者及 lifecycle 标为 NOT_RUN；默认 SDK 入口额外运行前端生命周期行为测试，消费者仅作类型检查。
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

`.github/workflows/release.yml` 从官方 main 手动运行。candidate 固定 Host 与 Plugins main 的完整 SHA，由
`release_build.py` 依次执行生产构建、插件打包、签名商店生成、桌面装配及三资产打包，并上传唯一原候选。
候选环境提供 `NEXUS_SIGNING_KEY`、`NEXUS_SIGNING_KEY_ID`、`NEXUS_METADATA_ROOT` 和 `NEXUS_ARTIFACT_ROOT`。
签名公钥、来源根及序号须与源码中的 production 信任一致。私钥仅驻留受控临时文件，结束时移除。
签名商店生产者通过 `--distribution-catalog` 逐包核对已发布摘要、版本、manifest 和本地化内容后投影发行日期。

`tooling/release_check.py host-candidate` 从生产 assets receipt 创建纯数据候选，核对装配源码摘要和
两仓 clean Git 原字节来源。输出为 CANDIDATE_CREATED_NOT_QUALIFIED；环境中的 run ID 不授予发布资格。
输入使用 `--source-root`、`--source-commit`、`--partner-root`、`--partner-commit`、
`--assets-receipt` 和新的仓外 `--output`。

`tooling/release_check.py publish-only --kind host --run-id <原run> --writer-id <当前run>
--artifact-id <原artifact> --output <新仓外目录>` 默认只校验。
它从 GitHub 读取官方工作流、原 attempt 的成功 job、artifact 归属/时间/摘要以及两仓 commit/tree 和
受保护分支可达性；下载原 artifact 后核对完整文件清单，只读取候选数据。
同一次 run 不能同时作为原候选和 writer，Test/local 来源拒绝。

显式 `--publish` 还要求当前 writer 的服务端身份与实际 main 控制 checkout 原字节一致。
Host tag 必须已经指向候选源码，0.18 屏障须先从官方 main 固定 SHA 回读。
同 tag 同名不同字节、重复名称、额外资产和缺失的公开资产均拒绝；每个资产核对服务端 ID、
uploaded、size、digest 并下载回读，全组复核后才公开 Release。writer 不调用编译器和包生产者。
生产信任未配置时状态为 PENDING。完整资产资格见[发布验收](#发布验收)。提交、推送、PR、tag、workflow_dispatch 和正式发布分别需要维护者授权。

## 旧客户端发现

`metadata/updates/legacy-policy.json` 为历史屏障 canonical，`update-policy.json` 保留同字节投影。
`tooling/legacy_updates.py check --base origin/main` 验证历史前缀、旧发现 JSON、0.18 屏障和新安装说明，相关差异纳入 Host PR 检查。
正式发布前须核对官方合入、固定地址回读及真实 0.17.6 客户端结果。
0.17 到 0.18 使用独立目录与实例，旧版本及其数据保留在原目录。 [安装与启动](../user/install.md)

## 插件目录元数据

插件的 i18n 字典投影成 manifest metadata，签名 catalog 和 Host 列表消费同一文本及语言集合。
正式 catalog 的日期来自实际发行事实，缺少发行日期时保留空值。 [插件列表时间](extensions.md#插件列表时间)

## 单文件发布

`product-publish` 生成 win-x64 框架依赖单文件。Host 与 Launcher 使用 ReadyToRun，更新窗口、安装 worker 与卸载入口使用普通 IL。各角色关闭裁剪，原生依赖由 .NET 单文件提取机制加载。
生产版 Host、Launcher 与更新窗口使用 `requireAdministrator`。安装 worker、卸载入口和 Test 产物使用 `asInvoker`。
Host 需要 .NET 10 Core 与 ASP.NET Core x64 共享框架，更新窗口需要 .NET 10 Core x64。

发布者核对实际 bundle 的框架声明、适用的 SDK 原字节与独立入口成员，再记录单文件输出。
更新准入读取 Host 和更新窗口的托管 profile，核对 Host 的 SDK 身份及窗口绑定的 Host 摘要。
production 只接收 production 入口，所有检查直接读取 bundle。
发布回执记录实际解析到的 .NET 10.0 稳定 SDK。生产者校验未压缩 bundle 6.0 的成员与原字节。 [.NET SDK 与依赖锁](command-reference.md#net-sdk-与依赖锁) · [.NET 单文件部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

## 公开应用与更新包

装配根直接包含 `NexusPipeline.exe`、`NexusPipeline.update.exe`、`README.md`、
`resources/desktop` 和 `resources/payload-manifest.json`。内部程序集身份保持 Host/Maintenance。
默认插件作为一次性初始化输入单独登记于装配回执的 `initialExtensions`，不属于应用文件清单。Portable 将签名目录、选项及两个包放入 `package-info/initial-extensions`；Setup 仅在安装器内部封装并释放到安装临时区。初始化成功后清理输入，更新包不携带这些材料。唯一插件加载目录为 `runtime/extensions`。
无参数启动使用装配根下的 `runtime`；本机装配回执与已有用户数据留在该根，公共 ZIP 排除这些内容。

公开清单 schemaVersion=2，installationGeneration=g0180，文件使用 path/sizeBytes/sha256/kind。
桌面清单绑定 ASAR、包锁和官方 Electron 归档；运行时选择 en-US、zh-CN 两个 locale，
并记录完整选中文件的摘要。生产源码使用实际 Git commit/tree；本地未提交 Test 清单保留 null，
生产入口拒绝该身份。SDK、profile、storeBinding 从受信准备上下文绑定，并核对实际程序的托管身份。

Portable 与 Update 都携带 `package-info/recipe.json` 及中英文说明。
配方 schemaVersion=1，包含、复用、删除文件互斥并精确覆盖目标清单；元数据不进入应用文件树。
首个 0.18 使用完整包。同代增量绑定原 manifest、官方 release/asset ID 与包摘要，
复用内容来自已校验的原应用；拒绝缺失、替换、未知文件和错误基线。

## Setup 打包

`product_assets.py` 用同一装配回执生成 Portable、Setup 与 Update。Setup 与 Portable 的共同应用文件及载荷清单逐字节一致。安装 worker、目标清单与公开卸载入口由 Setup 单独封装，卸载入口绑定同次发布的 worker 摘要。

新装及保留数据后重装的 Inno 目标为 `{app}`，同代覆盖的暂存目标为 `{tmp}\nexus-transition-payload`。两条安装分支引用同一 `Source`，由 `MergeDuplicateFiles=yes` 为每个共同文件保存一份物理载荷。0.18.0 候选验收时，Setup 须在 145 MiB 以内。 [安装与维护生命周期](installation-lifecycle.md)

打包器从本次发布的五个单文件读取实际框架声明，核对 `Microsoft.NETCore.App` 与 `Microsoft.AspNetCore.App` 的最低版本。`tooling/runtime-dependencies.json` 固定 x64 下载包的官方 URL、长度和摘要。 [微软 .NET 10 发行元数据](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json)

## 发布验收

正式发布核对同源三资产的完整清单、原字节、成品运行证据及相应 stable/preview 通道。Test 资产的运行结果按实际源码和包摘要记录，正式候选使用生产信任及服务端来源证明。 [发布产物与工具链验证](validation.md#发布产物与工具链验证)

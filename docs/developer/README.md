# 本地开发

要求 Windows 11 x64、.NET SDK 10.0.402、Python 3.12+。
所有依赖、构建输出、源码导出与报告必须位于显式指定的仓外目录。

显式 `install-bundle --runtime-root <目录> --bundle <已签名包目录>` 按已验签目录补装、重新安装或更新插件；
相同包摘要不生成新安装修订，账号、绑定及插件启用偏好继续保留。
正常启动只补装从未安装的两个默认插件，不恢复用户移除的插件，也不更新现有安装。
同版本不同内容仍遵循既有发布策略；稳定渠道的离线更新不绕过同版本内容保护。

数据库入口和未知输入保护见 [状态存储](storage.md)。
现役用例的入口、流程和恢复归属见 [Storage 用例归属](storage-owners.md)。
旧版业务保留及当前有限补修出口见 [业务保留](business-preservation.md)。
领域行为以架构及各领域文档为入口，短名规则见 [命名](naming.md)。
验证入口见 [Web 与桌面](frontend.md)、[独立工作流](workflow.md)；当前缺口见 [状态](../status.md)。
定义、绑定和新格式交换见 [自动化](automation.md)。
桌面桥接当前边界见 [桌面原生授权](desktop-authorization.md)。
交互管理入口见 [CLI 管理菜单](cli-menu.md)，排障事实及支持包见 [诊断](diagnostics.md)。
启动与公共请求入口见 [控制面边界](control-http.md)。
公共元素与扩展表面见 [前端 ABI](frontend-abi.md)。
共用 Web、页面状态与资源归属见 [Web 前端](frontend.md)。
秘密字段的持久化边界见 [Secrets](secrets.md)。
普通设置的修订与运行视图见 [设置](settings.md)。
绑定配置的不可变修订与当前边界见 [配置](configuration.md)。
冻结计划、共享资源预留与持久受理见 [执行准入](execution.md)。
厂商实例发现、Android 能力准入与启动 Job 交接见 [模拟器目标](emulator-targets.md)。
分钟调度、重试预算及系统动作见 [调度与完成动作](scheduling.md)。
脚本入口和 JavaScript 文件范围见 [判定脚本](judgement.md)。
受控 worker、独立控制帧与专项配置授权见 [执行 provider](execution-providers.md)。
可信目录、独占下载、安装和同一扩展运行时见 [扩展](extensions.md)。
鉴权浏览器模块、普通 JSON 与受限网络见 [插件浏览器入口](extension-frontend.md)。
公开 SDK、内部格式与实际兼容身份见 [SDK 导出与身份](sdk.md)。
选定项目只读来源、编译注册和完整内容图见 [项目编译](compilation.md)。
唯一桌面方向见 [Electron ADR](../decisions/desktop-shell.md)，资源约束见 [性能规范](performance.md)。

```text
python tooling/nexus.py snapshot --output <仓外快照目录>
python tooling/nexus.py check --scope source
python tooling/nexus.py check --scope contracts
python tooling/nexus.py build --profile test --output <仓外构建目录>
python tooling/nexus.py build --profile production --output <仓外构建目录>
python tooling/nexus.py sdk-packages --build-root <同源构建目录> --consumer-root <Plugins仓库/tests/consumers> --output <仓外SDK工作目录> --cache <共用缓存目录>
python tooling/nexus.py test --group contracts --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group core --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group windows --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group tooling --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group host-boundary --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group storage --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group storage-file-logs --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group storage-checkin --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group storage-config-cross-volume --fixture-volume-root <第二个真实卷的现有测试目录> --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py storage-experiment --build-root <仓外Test构建目录> --output <仓外报告根>
```

工具不提交源码或执行远端发布。Host/Update 的测试配置使用 asInvoker，生产清单保持 requireAdministrator。
Setup 专用 Installer worker 使用 asInvoker，与每用户安装器保持相同 SID 和权限级别；它仅提供安装生命周期命令，不进入公开 ZIP。
构建从当前源码原字节导出，输出包含逐文件摘要和真实退出码。
原生测试还保存应用产物逐文件摘要，并在运行结束核对文件集合和字节未变化。
未实现的命令明确返回非零，不生成成功回执。

每次验证保留独立原生报告，构建槽位和依赖缓存增量复用。
`contracts`、`core` 核对原生 TRX 中的预期方法及参数用例数量，拒绝零用例和跳过。
`host-boundary` 启动实际隔离 Kestrel 进程验证当前公开能力、实例保全和租约。
这些分组只证明当前已实现部分，尚未构成完整后端或产品验收。
原生分组可重复传入 `--method <已登记方法名>`，在同一次运行验证最多 64 个不同的已登记方法。
报告逐方法核对实际用例并标记部分覆盖，不能作为完整分组门槛；Python 分组仍只接受单个方法。
构建保留完整源码 manifest；Web 资产与 .NET 编译另记录实际输入和运行环境摘要。
项目闭包包括默认与显式源码、传递项目引用、资源、锁和构建配置；未知 MSBuild 评估禁止复用。
Web 源码、SDK 前端、生成器与原生依赖锁变化使浏览器资产失效；复用前核对原始回执与全部产物字节。
原生测试按已登记的用例组选择实际项目闭包，其他项目的测试源码变化不强制重编译该组；测试本身仍实际执行。
需要编译时仅构建该组项目、传递引用及边界检查工具；Core 测试不构建 Host 或 Web。
依赖 Host 的集成测试包含当前 Web 资产，未变化资产复用；普通测试不打包桌面程序或安装器。
显式完整构建仍检查整个解决方案；局部构建回执只证明列出的项目，不能作为完整构建或产品验收。
普通开发文档变化保留新的审计来源，不重编译未变化的组件。当前源码导出由 slot 最新拥有权核验，原编译回执身份不变。
SDK 生产者仍要求当前完整 Host 来源；插件构建与打包的输入范围由各自现役 runner 记录。
`storage-checkin` 要求显式设置 `NEXUS_PACKAGED_EXTENSION_ROOT` 与 `NEXUS_PACKAGED_CHECKIN_RECEIPT`，分别指向候选根和当前 SDK 构建的原包收据。
它实际安装原 ZIP，验证十四区域游戏的受控 HTTP、凭据轮换、跨任务去重、五平台原生捕获会话及分钟调度通知，不操作真实账号。
`windows` 直接运行锁定 SDK 内的 VSTest，避免 `dotnet vstest` 外层转发器额外加入 Job。
维护启动的无 Job 正向用例要求从实际不属于 Job 的进程启动，并由产品核验测试进程自身；已有 Job 不会被绕过。
跨卷分组需要显式提供第二个卷的专用目录；runner 在该目录中独占创建本轮子目录，并记录路径。
fixture 原生核验运行根与目标卷身份不同；缺少环境会失败，不跳过或以路径别名替代真实卷。

源码快照算法为 `nexus-source-manifest-sha256-v2-lf`：相对路径按 UTF-8 字节排序，
逐项记录原字节大小、SHA256 与模式；manifest 使用 UTF-8/LF、两空格缩进。
receipt 单独记录 manifest 原字节摘要，避免自引用。
每个源码 manifest 使用不可变路径，原始文件字节按 SHA256 去重保存在仓外构建槽位。
增量更新前核对并保全旧源码；证据字节改变时拒绝覆盖或继续替换源码导出。

依赖声明变更后，使用 `python tooling/nexus.py dependencies --update-locks --build-root <仓外构建目录>`。
命令从源码快照在仓外解析依赖，保留原生锁和日志，再将已完成解析的锁规范为 UTF-8/LF。
日常构建继续严格使用已保存的锁，不自动改动锁文件。

Web 依赖锁使用 `python tooling/web_build.py --update-lock --output <仓外构建目录>` 解析。
插件中心命令归属可用 `node tooling/web_contracts.mjs --root <源码根> --modules <该构建的Web node_modules> --output <仓外报告目录>` 验证。
该检查执行实际 Vue reactive owner 与 Control transport，范围为受控合同，不代表插件已安装或激活。

- [独立目录开发与验收](workflow.md)

## 最小选择与浏览器入口

`test --method` 在构建前确定实际工程；纯 Python 方法不构建 .NET。
完整 tooling 包含 NativeIdentity，仍需要 CoreTests。Python 方法集合在 `tests/policy` 独立声明，
发现、声明和实际执行逐项核对。Storage 数据测试保持原方法 ID，独立工程不引用 Host；
混合选择一次执行全部实际程序集，并核对合并报告。

```text
python tooling/web_build.py --prepare-only --output <仓外构建槽>
node tooling/web_contracts.mjs --root <源码根> --modules <构建槽/web-workspace/apps/web/node_modules> --suite automation --suite account-summary --output <新报告目录>
node tooling/web_browser.mjs --root <源码根> --modules <同一modules> --browser <浏览器绝对路径> --scenario controls --runtime <构建槽> --output <新报告目录>
```

完整参数和 Host 输入见 [浏览器验证](frontend-testing.md)。这些入口不代表完整产品验收。
生产发布使用独立候选和发布流程；本地 build receipt 不授予发行资格。
PR 生产闭包、离线计划及显式 Git 源码导出见 [发布检查与来源](release.md)。

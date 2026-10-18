# 本机验收构建

`tools/local_acceptance.py` 允许从未提交双仓工作树构建生产软件，保留正式候选的 clean/source/Actions 来源检查。

## 产物范围

`distribution/` 仅包含 Setup、Portable、Update 三个包，使用正式配方生成器验证增量重建与完整树一致。基线通过官方 Release API 独立认证；可用 `--baseline-cache <外部目录>` 复用已下载的官方包，每次仍重新核验资产身份和摘要。缓存中没有合适基线时生成完整内容的 Update，不伪造可复用文件。`local-acceptance-receipt.json` 保存包摘要、基线身份与复用字节，所有产物仍不可直接发布。

## 构建命令

```powershell
python tools/local_acceptance.py build --host-root "<Host源码绝对路径>" --plugins-root "<Plugins源码绝对路径>" --output "<仓库外独占验收目录>"
```

可选 `--cache <外部共享缓存>`、`--compiler <已固定SHA的ISCC.exe>`；省略时使用输出目录的缓存，下载并校验固定 Inno 6.7.3。使用项目锁定的 .NET、Node/npm、Python、Electron 输入，依赖缓存和全部构建输出留在外部目录，遵守本机临时目录策略。输出已有身份不匹配、源码快照被外部改动或已有完整验收软件时拒绝覆盖。

## 增量软件装配

日常人工验收可在同一入口加 `--assembly-only`，使用独立于完整分发打包的输出目录。它实际装配生产 Host、桌面、更新工具和全部现役插件，不生成 Setup、Portable 或 Update，不获取更新基线；安装与更新包验证仍使用默认完整打包模式。

`--cache` 下的 `local-components` 保存前端、桌面编译、Electron 运行时、应用与逐插件产物。输入指纹包含原始源码、构建规则和适用工具链；managed 插件还绑定真实 Host SDK 与共享工程输入。命中时逐文件复核摘要和文件集合，未知、残缺、被改动或正在占用的缓存拒绝复用。源快照只写变化字节，保留未变化文件时间戳。应用源树身份变化时仍重新生成共同身份和生产 Host、维护工具、桌面 ASAR；桌面编译和运行时可独立复用。

相同输入可以在同一输出目录重跑，核验既有软件而不覆盖配置和运行数据。不同产品字节或文件集合拒绝替换已有 `acceptance/`；先保全现有验收软件及用户数据，再进行新装配。输出根有独占锁，缓存按组件独占；中断后保留现场，确认原进程结束、核验归属后处理，不自动删除未知锁或不完整缓存。

回执记录每个实际访问组件的 `cacheHit`、输入键与耗时，以及完整命令的 `elapsedSeconds`。应用整体命中时不访问其桌面子组件。保留本轮仍会使用的有效缓存和构建工作区，按回执清理失效缓存及重复软件；不得把缓存命中当作测试执行或正式发行资格。

## 源码保全与身份

入口记录双仓实际基线、原始 diff、原始文件清单 SHA256，以及独立对象目录中的真实字节 Git tree。CRLF 等原始源字节不被正常 Git 过滤器转换。原仓库不创建 commit/ref，也不改 index。构建结束核对源、基线、index 和 refs 未变化。

## 装配与运行

真实生产 Host、桌面、Updater、Uninstaller 与元数据 helper 使用冻结源；Host 和 Updater 为 requireAdministrator，Uninstaller 与元数据 helper 为 asInvoker。工具只引用有限 Maintenance，不引用 Host。Host 只嵌入可信 updater 恢复资源。ZIP 和 Setup 继续使用同一载荷清单与两个按固定 Plugins 快照动态解析的稳定内置插件。所有现役插件另从同一冻结 Host SDK 构建，装配到 `acceptance/`，不改变正式分发的预装范围。软件普通运行不传 `--app-root`。GameCheckIn 可使用现有 `plugin enable game-checkin` 保存偏好，再从此实例托盘退出并启动；以插件页确认 configuredEnabled/runtimeEnabled。

## 结果与发行边界

`local-acceptance-receipt.json` 记录实际源树、共享 buildId、编译命令、日志、Setup/ZIP/插件哈希与版本，`status=BUILD_PASS` 只代表构建，不代替本机验证或人工验收。原始源清单、diff、尝试报告和日志可供审查。所有产物标识为 `mode=local-acceptance`、`producer=local`、`publishable=false`，不生成 candidate.json，不上传、发布、提交或启动安装器。正式 publisher 拒绝该来源。

验收摘要的 GameCheckIn 版本取自本次插件构建结果，交付前与 `pluginBuilds` 及验收目录内的 manifest 核对一致。

用户确认后若获得后续授权，从已提交的干净源码按现役手动 candidate/publish-only 流程发行；若行为源或影响行为的构建输入有变化，补相应验证。

## 独立工具验证

`maintenance/` 中每个角色的构建报告记录实际入口 hash、角色身份、MSBuild Compile/ProjectReference/EmbeddedResource 和依赖闭包；离线 PE/bundle 读取核对角色身份，不执行候选来识别角色。公共 production 与 Portable 只有 Host 和 updater，独立 uninstaller 仅作为安装器输入。

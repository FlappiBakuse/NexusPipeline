# 独立目录开发与验收

本流程只依赖源码、锁定工具链和显式输入。工程 owner 见 [架构](architecture.md)，
契约权威和 SDK 身份见 [契约](contracts.md) 与 [SDK](sdk.md)。不依赖聊天或个人脚本。

## 准备与小改闭环

使用 Windows 11 x64、.NET SDK 10.0.402、Python 3.12+ 及锁文件要求的 Node/npm。
将源码、运行实例和产物分开。以下仓外路径由操作者按本机政策选择：

```powershell
$workRoot = 'D:\NexusWork'
New-Item -ItemType Directory -Force "$workRoot\tmp" | Out-Null
$env:TEMP = "$workRoot\tmp"
$env:TMP = $env:TEMP
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:npm_config_cache = "$workRoot\cache\npm"
python -B tooling/nexus.py snapshot --output "$workRoot\clean-host"
```

进入回执的 exportRoot，执行 `git init -b develop` 初始化独立练习的本地源码清单；不需要提交或远端。
该导出专用于修改练习，修改后不再作为原导出回执的未变副本复用。
从 README 找到 Core.Execution.ExecutionTimeBudget，
阅读 tests/unit/RetryBudgetTests.cs。在 KeepsOriginalDeadline 中增加对推进时间后的剩余预算断言，
再执行已登记的一个方法：

```powershell
python -B tooling/nexus.py test --group core --method NexusPipeline.CoreTests.RetryBudgetTests.KeepsOriginalDeadline --build-root "$workRoot\backend" --output "$workRoot\reports"
```

报告应发现该方法的一个用例。核对原生 TRX、退出码、计时、来源和产物摘要；零用例、取消、缺报告、skip 均失败。
独立练习使用单独导出，不直接修改仍被增量构建回执拥有的槽。闭环只证明定位、修改和定向验证，不能代表整机通过。
同一原生分组可重复 --method 选择最多 64 个不同方法；Python 分组只接受一个完整方法 ID。

## 开发验证与复用

先集中实现，再按影响范围批量验证。Core 测试不构建 Host、Web 或桌面壳；Storage/Host 集成只构建实际依赖。
未变化 Web 资产可按闭包复用。重复使用 build-root 和依赖缓存，每次保留原生命令与来源回执。
新增测试登记真实方法和用例数。未知 MSBuild 输入、依赖锁变化或产物字节不一致会拒绝复用。
源码身份包含未提交字节；不能把旧 commit 或旧包标为当前源码。

## 最终软件

功能完成后冻结双方源码，执行适用分组，再统一构建。SDK 消费者回执交给 Plugins 的 import_sdk.py、
纵切构建及 package_extension.py，命令见 Plugins README。包内文档和前端变化也必须重新打包。

```powershell
python -B tooling/nexus.py product-publish --build-root "$workRoot\backend" --output "$workRoot\product" --runtime-archive '<锁定 Electron ZIP>' --profile test
python -B tooling/nexus.py product-assemble --publication-root '<本次发布目录>' --output "$workRoot\acceptance" --bundle-root '<本次已验签 bundle>'
```

目录取实际回执，不猜测最新文件名。核对 Host、SDK、Web、Electron 和全部现役插件身份。
完整软件不能由源码、ZIP 或命令代替。Setup/Portable/Update 继续由同一冻结输入生成，
验证安装、启动、卸载保全、更新、失败恢复和合法回退；Test 信任不能进入正式发行。

## 测量与排障

按 [性能规范](performance.md) 分别测空实例、日常、大历史、长日志和图片详情。
保存同输入基线与复测，记录峰值、稳态、关闭回落和趋势。启动测到关键首屏可操作，
资源包含 Host、renderer、GPU 和全部已拥有子进程。采样期间不编译、不跑测试、不扫描大目录。
原参考目标与 v2 场景结论分别保留，不合并场景或自动放宽门槛。
桌面原生启动诊断写入运行目录的 desktop-profile/logs，工作目录也属于该 profile；应用载荷不接收诊断写入。
失败先查当前 run 的原始日志、来源、输入闭包、身份与权限，不用旧包覆盖证据。
清理前确认精确路径、链接目标、进程退出及源码保全，保留用户数据和最小失败材料。

## 本机与局域网测试

真实 Host 夹具默认只监听 `127.0.0.1`，不会自动开放局域网端口。
明确验证远端 Bearer 的浏览器用例必须显式请求 remoteAccess，并在运行进程中设置
`NEXUS_TEST_REMOTE_ACCESS=1`；未设置时在启动 Host 前拒绝。
Windows 可能对新路径下的局域网监听程序弹出防火墙提示，不自动修改规则。
本机画面、安装和重启验证不需要该变量。本机验证不计入远端鉴权证据。

## 控件与前端局部工作

按 [浏览器验证](frontend-testing.md) 先 prepare-only，再选择合同 suite 或 browser scenario。
依赖和构建槽复用，报告使用新目录。只有所选闭包的实际通过记录可用于该变更，不外推全产品。
插件浏览器输入要求明确 extension、scenario 和 browser；共享 SDK/frontend-ui 改变时扩展到全部真实消费者。
BetterGI OwnedWriter 夹具由 `NEXUS_TEST_ROOT` 取得本轮独占目录，不接受链接、目录外文件或摘要不符的程序。

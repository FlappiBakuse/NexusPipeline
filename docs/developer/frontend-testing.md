# 按范围验证 Web 与桌面

执行顺序与选定范围以[统一验证规范](validation.md)为准；下面给出参数参考。

先在仓外构建槽运行 `python tooling/web_build.py --prepare-only --output <构建槽>`。
该模式固定当前源码和原生 npm lock、准备依赖，保留 source/prepare 回执；不运行 Vite、vue-tsc 或 .NET。
相同锁复用依赖；源码改变须重新准备。正常 Web build 继续执行类型、AST 边界及生产构建。

源码、依赖和输出参数均须使用显式绝对路径；目录及父目录不得经过 symlink 或 junction。
构建槽、测试 runtime 和报告目录须与源码互不包含。报告目录使用新目录或空目录；
原生测试 runner 仅复用具有本源码根与构建槽所有权记录的输出，拒绝占用未知非空目录。
换盘符的干净 checkout 仍使用同一命令；准备回执中的逐文件摘要须与该 checkout 的原始字节一致。

## 合同

```text
node tooling/web_contracts.mjs --root <源码根> --modules <构建槽/web-workspace/apps/web/node_modules> --suite automation --suite execution-plan --output <报告目录>
```

可重复 `--suite`，支持 plugin-command、automation、account-summary、plugin-list、control-models、execution-plan、history-formatting。
缺省仍为 plugin-command；未知或重复选择失败。只打包所选入口的实际依赖，使用真实 Vue reactive 和生产 owner。
每次保留命令、suite、case、传递模块摘要和计时；失败不把已登记但未执行的 case 标成通过。
这些小合同入口还需按改动补充独立行为回归，不能当作完整领域覆盖。

## Node 行为场景

同一 `web_contracts.mjs` 入口按 `tests/frontend/*.test.cjs` 的文件名选择 Node suite，例如：

```text
node tooling/web_contracts.mjs --root <源码根> --modules <上述modules> --suite frontend-session --suite native-close --output <新空报告目录>
```

去掉 `.test.cjs` 后的文件名就是 suite；不维护第二份方法数量清单。Node 场景独立进程执行，
不启动真实 Electron。TypeScript 来自显式 modules；临时文件进入本次报告子目录。
每个 suite 保留原生事件，统一回执记录实际结果；零测试、skip/todo、取消、超时或失败均拒绝 PASS。
源码输入包含所选测试、Web/桌面源码范围、入口和依赖锁；此范围用于绑定输入，不表示全部源码均有测试覆盖。
响应式 TypeScript suite 与 Node suite 可在同一命令中选择，各执行一次。

## 浏览器

运行中心与日志场景使用真实 `RunningExecution`、`LiveLogPanel` 和页面组件：

| 场景 | 可观察结果 |
|---|---|
| 启动布局 | 十项任务启动后两张卡片保持父级宽度，任务条在自身滚动区展示；桌面与窄屏操作完整可达 |
| 调用计数 | 冻结三次调用时随实际 writer 显示 1/3、2/3、3/3，任务重试及恢复前调用计入总数 |
| 增量日志 | 完整接收 500 帧并合并刷新；切换身份、隐藏、离开视口及卸载释放旧流，晚到回调保持原归属 |
| 依赖边界 | 现役 TS/Vue AST 门禁拒绝面板导入应用 store、宽端口、原始 Control client 和日志服务值，保留日志类型及页面组装 |

配置读取另按[配置存储](configuration-storage.md#配置读取验收)核对，前端通过结果不替代真实专项与原始配置字节验收。

```text
node tooling/web_browser.mjs --root <源码根> --modules <上述modules> --browser <实际浏览器exe> --scenario controls --runtime <构建槽> --output <新空报告目录>
```

`controls` 使用真实控件、键盘、IME 和 AX 夹具；`controls-painted` 选择绘制控件。
测试依赖仅来自 `tests/browser-tools/package.json` 及其原生 lock，安装进 runtime/browser-tools。
不会进入产品依赖。浏览器路径必须显式传入；不扫描个人 profile 或接管已有实例。

Host 场景为 host-automation、host-source、host-installed、host-checkin、host-activities、host-screenshot、host-plugin-list，
额外传 `--host-input <JSON>`，只连接操作者创建的隔离回环 Host。JSON 必须包含：

- address、instanceId、fixtureRoot、tokenEnvironment（仅环境变量名）。
- productReceipt：具有 path、sha256 的同源成功构建回执；pluginReceipts 是该场景真实插件回执数组。
- context：场景所需 fixture；automation 要求 queueId，source 要求 directory，均由操作者预先创建。
- plugin-list 要求 uninstalledExtensionId 和 searches，目录包含该未安装包及两个已安装包；使用原包的本地化与安装事实，不启动任务。

Host 身份通过 capabilities 核对，源、候选和插件均按摘要绑定。凭据值仅通过指定环境变量传入。
host-automation 通过实际编辑器连续保存同一队列两次；既有插件场景保留各自受控 I/O 与真实安装边界。
直接运行 Storage 浏览器集成还需 `NEXUS_TEST_BROWSER_EXE`、现役 NODE/MODULES/SCRIPT 与原包输入。

账号摘要成品方法为 `NexusPipeline.StorageTests.AccountBrowserTests.OpensWorstHistory`，
使用 `python tooling/nexus.py test --group storage --method <方法> --build-root <构建槽> --output <报告根>`。
`NEXUS_ACCEPTANCE_APPLICATION` 指定当前完整 Test 软件的应用目录；
`NEXUS_ACCOUNT_SUMMARY_BROWSER_SCRIPT` 指向本仓 `tests/integration/browser/account-summary-module.mjs`。
原包输入使用 `NEXUS_PACKAGED_EXTENSION_ROOT` 与 `NEXUS_FORMAL_EMULATOR_PACKAGE_RECEIPT`；
Node 和模块分别由 `NEXUS_BROWSER_NODE`、`NEXUS_BROWSER_MODULES` 指定。
该入口安装受控纯数据 adapter，实际执行隔离任务生成旧失败/新成功，验证账号汇总、活动、定时、
鼠标与键盘历史跳转、取消恢复及卸载后的冻结译文；不连接游戏或真实账号。

未知任务成品方法为 `NexusPipeline.StorageTests.UnknownBrowserTests.PreservesUnknownTasks`，使用同一原生命令。
原包收据使用 `NEXUS_FORMAL_BAAH_PACKAGE_RECEIPT`，浏览器脚本变量为 `NEXUS_UNKNOWN_TASK_BROWSER_SCRIPT`，
指向本仓 `tests/integration/browser/unknown-task-module.mjs`；其余完整软件、Node、模块及浏览器输入相同。
该用例覆盖未知项开/关、受控执行、已知失败任务的安全重试、原字节保全和卸载后的冻结译文。

八专项冻结标题使用 `NexusPipeline.StorageTests.SpecializedLocaleTests.PreservesFrozenTitles`。
Plugins 的 `node tests/specialized/locale-fixtures.mjs <源码绝对路径> <仓外目录>` 生成合成配置输入；
`NEXUS_SPECIALIZED_LOCALE_FIXTURES` 指向其 `fixtures.json`，`NEXUS_PACKAGED_SET` 指向八个原包的正式收据集合。
`NEXUS_SPECIALIZED_LOCALE_BROWSER_SCRIPT` 指向本仓 `tests/integration/browser/specialized-locale-module.mjs`；
完整软件、Node、模块与浏览器沿用上述变量。用例核对原包 adapter 摘要、受控程序实际执行、
配置原字节恢复以及卸载后中英文历史，保留自定义和未知任务标题，不启动游戏或真实上游。

desktop-native、desktop-browser 使用 `desktop-bundle` 生成的实际 Electron fixture；runtime 指向 bundle，
browser 必须是该 bundle 的 NexusPipeline.Desktop.exe。其 HTTP/菜单为夹具，完整 Host 桌面另行验收。
纯窗口 placement 不需要 Electron。

零 case、缺报告、崩溃、超时、非零退出或源码改变均失败，不自动重试。首个失败保留原日志。
没有 web/e2e test group；不要把这些命令写成 `nexus test --group web`。
完成定向回归后，对同源完整软件做原生 DPI、多屏、恢复、真实上游和人工视觉验收。

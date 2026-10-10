# 本机验收构建

`tools/local_acceptance.py` 允许从未提交双仓工作树构建生产软件，保留正式候选的 clean/source/Actions 来源检查。

```powershell
python tools/local_acceptance.py build --host-root "<Host源码绝对路径>" --plugins-root "<Plugins源码绝对路径>" --output "<仓库外独占验收目录>"
```

可选 `--cache <外部共享缓存>`、`--compiler <已固定SHA的ISCC.exe>`；省略时使用输出目录的缓存，下载并校验固定 Inno 6.7.3。使用项目锁定的 .NET、Node/npm、Python、Electron 输入，依赖缓存和全部构建输出留在外部目录，遵守本机临时目录策略。输出已有身份不匹配、源码快照被外部改动或已有完整验收软件时拒绝覆盖。

入口记录双仓实际基线、原始 diff、原始文件清单 SHA256，以及独立对象目录中的真实字节 Git tree。CRLF 等原始源字节不被正常 Git 过滤器转换。原仓库不创建 commit/ref，也不改 index。构建结束核对源、基线、index 和 refs 未变化。

真实生产 Host、桌面与元数据 helper 使用冻结源；Host manifest 为 requireAdministrator，helper 为 asInvoker。ZIP 和 Setup 继续使用同一载荷清单与两个固定稳定内置插件。所有现役插件（当前十四个）另从同一冻结 Host SDK 构建，装配到 `acceptance/`，不改变正式分发的预装范围。软件普通运行不传 `--app-root`。GameCheckIn 可使用现有 `plugin enable game-checkin` 保存偏好，再从此实例托盘退出并启动；以插件页确认 configuredEnabled/runtimeEnabled。

`local-acceptance-receipt.json` 记录实际源树、共享 buildId、编译命令、日志、Setup/ZIP/插件哈希与版本，`status=BUILD_PASS` 只代表构建，不代替本机验证或人工验收。原始源清单、diff、尝试报告和日志可供审查。所有产物标识为 `mode=local-acceptance`、`producer=local`、`publishable=false`，不生成 candidate.json，不上传、发布、提交或启动安装器。正式 publisher 拒绝该来源。

用户确认后若获得后续授权，从已提交的干净源码按现役手动 candidate/publish-only 流程发行；若行为源或影响行为的构建输入有变化，补相应验证。

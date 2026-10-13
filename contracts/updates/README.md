# 更新协议

`metadata/updates/channels.json` 是有界发现 envelope。schema 1 严格包含 `schemaVersion`、`channels` 和 `bridges`；channels 仅含 stable、prerelease，每个渠道仅含 previousPublishedTarget、pendingTarget。槽位可以为空；目标包含 releaseTag、manifestAssetName、readerProtocol、requiredCapabilities。字段、类型、重复键、对象深度和响应长度由 reader 校验。当前 reader 支持协议 1。bridges 最多 8 项；每项严格包含 sourceReaderProtocol、sourceCapabilities、targetReaderProtocol、targetCapabilities、entryTarget。sourceCapabilities 必须与本机完整能力集合相同，目标协议必须精确匹配所需 readerProtocol，targetCapabilities 必须覆盖目标要求；entryTarget 使用相同目标结构且必须能由当前 reader 读取。只有版本严格高于本机且低于最终目标的桥接才可选择，按版本从高到低核验，禁止递归桥接和循环。桥接 Release、渠道、完整资产、GitHub digest 与目标清单采用相同验证流程，未来清单不会交给旧 reader 解析；桥接安装后重新发现最终目标。未发布或校验失败的桥接保留上一可用目标。当前渠道未声明实际桥接发行。

入口不包含执行命令、下载脚本或自身源码提交。reader 按固定仓库的 tag 查询 Release，再分页读取该 Release 的 Assets API，最多 400 个资产、5 页；同名资产和重复 ID 均拒绝。GitHub 的 digest、尺寸、资产 ID 和 uploaded 状态是资产身份依据。目标清单的完整字节必须先通过该摘要，随后才解析其内容。默认源不枚举历史 Release；自定义源保持独立同源限制，旧数组格式只接受具有同等资产摘要与身份的完整包。

目标清单 schema 1 的字段为 schemaVersion、product、target、requirements、fullPackage、components、files、migration。target 固定版本、win-x64、安装代际、buildId、frontendHash 和 payloadManifestSha256。requirements 指定 readerProtocol、runnerHandshakeProtocol 及 requiredCapabilities。完整包和组件通过 assetName 绑定同一次 Release 中的唯一资产；三个组件 ID 为 host-core、desktop-app、desktop-runtime。files 固定路径、实际尺寸、SHA256 和组件归属，必须与完整应用清单一致。

当前已注册路线为 `g0170-no-store-change-v1`，源和目标均为 g0170，stores 和 migratorIds 均为空。该路线保持现有业务存储的字节与位置，不宣称支持未知业务 schema 或跨代际转换。数据变化必须先由模块 owner 定义输入/输出 schema、已编译迁移器及恢复规则，再登记对应路线；未知要求在写入前阻断。

runner 使用目标 Host 的 `--update-migration-runner` 专用入口，跳过普通 Host 初始化。父协调器通过当前用户命名管道核对真实客户端 PID、进程映像与开始时间、EXE 摘要、一次性随机数、事务 ID、构建身份、协议、能力和单调序号。runner 只处理已注册路线，返回输出清单；父协调器确认进程退出及输出后负责最终交换。此接口约束不表示 Windows 进程沙箱。

新协议 journal schema 2 在现役应用备份、交换、启动回执和回滚之外，冻结 Release ID、tag、全部资产 ID/尺寸/digest、路线及 runner 收据。当前无存储变化路线的输入/输出清单为空。恢复不重新解析 latest 或从同名 URL 取得新候选；未知 journal、活跃或无法确认结束的 runner、未知文件变化均保留现场。旧完整包和安装器继续使用 schema 1。

# 控制面启动与请求边界

Web 默认监听 IPv4 loopback；保存的远端访问开关可启用 IPv4 任意地址监听，远端请求仍须验证 Bearer。
端口可显式指定或读取保存值，端口 0 由 OS 分配，实际地址随 `host.ready` 输出。MCP 使用独立 loopback 监听。
status 的 accessAddresses 按本机已启用网卡投影 IPv4 内网与公网网卡地址，使用实际监听端口；
它不探测外部网络、不推测 NAT 映射，也不将网卡地址展示当作远端连通性证明。
未实现的业务资源不生成成功响应，现有入口以 `contracts/openapi.json` 为准。

启动顺序为实例格式与物理路径校验、实例独占、新状态库校验、恢复索引发布，然后启动 Kestrel。
未知根、旧格式和丢失的已初始化数据库不能被默认数据替代。
未知数据库保留原始字节，只在该隔离实例的诊断目录写受控检查副本。
恢复索引中的隔离仍约束后续执行；传输启动不表示隔离资源已可运行。

请求 Host 必须使用实际监听端口。本机入口要求该 socket 的地址或 localhost；远程可使用同源 DNS 名或实际网卡地址，始终根据真实 peer 判断远程身份并核验 Bearer。Host、Forwarded 不会提升为本机身份。
Origin 若存在，必须与这个已核实的 authority 同源；两个互相匹配的客户端头不能替代监听证据。
当前不信任 Forwarded 或 X-Forwarded-*，不由它们改变来源、协议和允许 authority。
控制响应使用 no-store。原生动作还须通过 [内核连接拥有权与签名](desktop-authorization.md) 校验。
普通本机权限还复核实际连接 peer；转发头不能授予 loopback 身份。
秘密状态、写入和明文读取是独立声明的能力，读取明文必须显式调用专用 POST。
接口、空正文约束与限额见 OpenAPI 及 [秘密字段](secrets.md)，错误只返回稳定代码和新的关联 ID。

生产 requireAdministrator 与 Test asInvoker 使用相同入口、业务和 HTTP 源码。
组合根持有实例租约和 WebApplication，退出时等待服务器停止并释放原生会话。
桌面退出观察任务拥有取消源、异常接收方和有界结束等待，不独立延长 Host 生命周期。
CLI 的固定命令调用同一 HTTP 入口，不接受通用 URL 或任意文件写入。JSON 结果为单个 envelope，诊断输出到 stderr；二进制输出必须显式选择 stdout。
MCP 工具列表与实际调用均复核当前启用和写权限，两个开关默认关闭。能力目录由 `ControlCapabilityPolicy` 定义。
普通本机 HTTP 使用该策略的固定本机能力集合，不为判定本机能力读取 MCP 开关。
远端访问开关、远端 Bearer 以及 MCP 启用和写权限仍在请求与持续订阅期间复核当前值。

## 已接入能力与客户端边界

下表描述当前接线，不代表所有能力已完成整体验收。Web 的本机与远端权限由真实连接来源决定，不能由客户端自报。

| 业务 | Web / CLI | MCP |
|---|---|---|
| 状态、诊断、操作查询 | status、doctor、support、operation、capabilities；操作结果绑定主体 | 对应只读工具；支持包不开放 |
| 自动化定义与日程 | automation、scheduling、resolve、automation-change；删除共用定义事务 | 只读定义和日程 |
| 新格式模板与交换 | automation-export、automation-attachment、automation-import-preview/apply | 不开放 |
| 执行预演、启动、取消 | execution-preview/start/cancel；受理返回原 operation/run/attempt 身份 | 预演只读；启动和取消须写权限 |
| 通用配置值与文件 | configuration-resources/files/original/value-preview/value-apply、configuration-import；binding resources 汇总只读资源身份与当前修订 | 仅资源清单 |
| 原生配置编辑与专项修复 | configuration-editor-begin/save/cancel、adapter-*；原生打开和修复仅本机 | 不开放 |
| 历史、日志、图片 | records、record-summary/detail、record-attempts/logs/images 及原文；内容退役仅本机 | 受限只读查询，不开放任意删除 |
| 通知任务 | notifications、notification-save/delete/reorder/limit/test；凭据限声明字段 | 写权限开启后允许对应通知操作；不开放通用秘密读取 |
| 宿主与服务设置 | settings、service-settings、interface preferences 普通读取和写入可由已认证远程调用 | 不开放 |
| 专用秘密与控制令牌 | secret-status/read/change、control-token-*，仅本机；明文使用专用接口 | 不开放 |
| 扩展与商店 | extensions/details、extension-catalog/refresh/operation/apply/reconcile；普通管理可由已认证远程调用 | 不开放 |
| 事件、实时预览 | events-snapshot/events；media-open/frame/close 绑定实际运行与授权主体，允许已认证远程预览 | 不开放实时预览 |
| 桌面与系统完成动作 | 固定本机授权入口；pending/cancel 提供本机倒计时与撤销；CLI 可撤销完成动作，不能伪造桌面确认 | 不开放 |
| 宿主维护更新 | maintenance-feed/refresh/prepare/operation/start/cancel；查询绑定原主体，写入仅本机 | 不开放 |

事件按数据库提交序号、恢复修订、活动执行修订和界面偏好变化生成共享快照，空闲客户端只发送心跳；显式状态/诊断查询仍读取当前状态。事件是有界失效通知；丢失序列返回 resync，客户端重新取快照。配置等持久事务提交推进失效序列，失败事务不发布成功变化。
事件连接到期或客户端断开按正常结束处理；客户端重连不授权重复执行写操作。
连续提交使事件快照暂时无法稳定时，已建立的连接发送 resync 后结束，不推进状态水位；客户端重新读取权威快照。
历史列表及汇总支持独立的 `business` 与 `lifecycle` 筛选；生命周期仅接受 completed/cancelled/interrupted/blocked/skipped，未传入时不限制。筛选不改变记录的原始四维事实。

`support` 复用 `control.doctor.read`，导出最多 64 KiB 的 ZIP，仅含固定 `manifest.json` 与 `doctor.json`。
清单记录版本和诊断身份；诊断包含运行时版本、准入/恢复解释、队列与已安装插件计数，以及最近至多 64 条类型化完成事实作为安全日志尾。导出不读取 worker 文本、私有原因码、配置、数据库原文或绝对路径，不在运行目录落盘。
CLI 必须显式提供 `--binary-stdout true`，错误写 stderr；MCP 继续只提供结构化 doctor。

定义、执行、配置、通知、历史及设置映射覆盖后端台账所需控制入口；共用 Web 页面、普通草稿与公共元素见 [前端](frontend.md)。
媒体正向 HTTP、通用和专项原生编辑及有限支持包 HTTP/CLI 已定向通过；维护更新仍须闭合真实桌面交接和窗口恢复。旧业务保留的实现和剩余检查见 [当前实施范围](../status.md)，不能由控制接线通过代替。

维护查询、启动和取消复用原待办 owner；`start` 的 202 只表示交接已启动，不表示候选已成功。
独立重启使用 `POST /control/v1/maintenance/restart/{operationId}`，要求本机维护权限，
不接受 body 或 query；CLI 为 `host-restart --id <uuid>`。它不要求更新包或 Release 信任，
复用闲置排空和 owned 桌面关闭检查。202 返回接管进程、相同实例及已保存端口，
只证明交接；固定端口重连后检查 status 的 `processId` 与实例身份才证明服务就绪。
保存端口为 0 时，接管前的回复仍为 0，实际端口以新 Host 的 `host.ready` 和桌面启动授权为准。
受控接管进程等待旧宿主实际退出，再按保存设置启动；不能用 PID 存在代替拥有权证明。
`completed` 查询须同时检查 `outcome`：`updated` 表示采用候选，`originalRestored` 表示已恢复旧载荷，`notExecuted` 表示未执行替换。
`maintenance-prepare` 根据精确签名源和版本准备不可变载荷，记录执行时机和窗口意图；其 202 只表示待办受理。
窗口模式 `headless` 保留纯后台服务，要求 `visible=false`、`route="/"`，更新及再次重启均不启动 Electron。`desktop` 和 `browser` 仍恢复其独立桌面进程；后台服务的自动更新采用 `headless`。
下载使用保存的代理快照、有限响应及可信跳转范围，完成后仍核验实际程序集的 production/test 身份。
取消只能发生在持久交接之前；进入交接后的停止与恢复必须核实真实 writer 和 Job 家族。
当前生产更新信任尚未配置；已接入的桌面交接仍要求完整 owned 家族停止，不能用 Test 信任或缺少家族证据替代。

自动检查、宿主闲时应用和插件自动更新分别读取各自开关；关闭自动应用不会撤销显式手工待办。下次启动待办在本次调度器开始前处理，本次新建待办不会被误当成下次启动。插件逐个沿原更新事务推进，复用已验签目录，并用原设置修订执行 CAS。`updates.sourceUrl` 为当前新格式 feed 的 HTTPS 根目录（以 `/` 结尾）；自定义元数据镜像仍必须提供可信签名，不能改变商店/载荷许可域，也不读取旧 GitHub Release 格式。

通用配置只读文档入口为 `GET /control/v1/configuration/generic/{bindingId}/{resourceId}/document`，
要求 `configuration.read`；查询包含 revision、sourceSha256、syntax，以及目录资源的 relativePath。
它核验当前声明、保存修订和不可变源字节，复用字段补丁的解析器，返回 revision、sourceSha256 与解析后的 value；
文档大小、节点和深度限制保持原有边界。此投影供界面生成 typed selector，不接受未解析路径的数字猜测。

代理密码变更响应包含原 operationId 与状态修订。结果未知时，本机可使用
`GET /control/v1/local/secrets/proxy-password/operations/{operationId}` 查询原字段操作；
此入口要求 secrets.status，按当前实例和字段限定主体，不返回秘密明文，也不重放变更。
令牌继续使用带原生确认的专用变更与操作查询入口。前端将原操作身份与明文寿命分开，
只有原操作完成证据和对应状态修订都核验成功才解除未知结果锁定。

历史详情的 `GET /control/v1/records/{runId}/display` 要求 `records.read`，只读取冻结定义及已核验运行预算。
响应包含冻结目标/用户名称、模式、重试上限及每次尝试的可用时间；缺失计时返回 null。
它不从当前目录名称或结束时间减累计耗时推断历史事实，不修改记录、启动任务或补写旧格式。

历史日志读取可用 invocationOrdinal=1..99 选择实际内部调用。description、tail、original 与 parts 必须使用相同序号和对应 snapshotSha256。
网络访问地址不把 IANA 特殊用途 IPv4（含 198.18.0.0/15 基准测试网段）列为公网；地址展示不证明公网可达。

历史列表按结束时间与 runId 升序分页；下一页使用 afterEndedAt 与 afterRunId。CLI 对应 --after-ended-at、--after-run-id，MCP 使用相同字段。游标两项必须同时提供，指向上一页末行，下一页严格排除该行。

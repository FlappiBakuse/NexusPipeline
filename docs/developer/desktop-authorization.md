# 桌面原生授权

Host 的 `desktop` 入口启动固定载荷中的唯一 Electron 壳，并在启动前挂接完整进程族观察。
也可由受信任的本机启动器显式传入 `--native-client-pid`；这种外部注册只提供原生动作身份，不提供维护所需的家族拥有权。
注册发生在打开 runtime 前，绑定该进程的 PID、创建时间、映像路径与 SHA256、用户 SID 和提升状态。
注册时核对完整映像 SHA256，并将进程与映像的只读句柄在 Host 生命周期内保持打开；跨用户注册拒绝。
进程句柄必须仍处于未结束状态；保留已退出进程的身份不代表原生拥有权继续有效。
该参数属于启动权限边界，不接受 HTTP 请求中的 PID 或客户端类型作为注册依据。

`GET /control/v1/desktop/session` 与 `POST /control/v1/desktop/authorize`
只接受已登记主进程实际拥有的 IPv4 loopback TCP 连接。
Platform 使用 Windows TCP 所属进程表核对完整连接四元组，再通过保留的进程句柄核对创建时间、映像路径、用户 SID 和提升状态，并确认当前映像路径仍指向注册时固定且禁止写入的同一卷与文件 ID。
查询失败、身份变化、进程退出或无法唯一归属时拒绝。
请求头、自报 PID、同源网页、同一用户的其他进程均不代替此证据。
没有登记原生进程时，Host 不映射这两个入口。

每次 Host 启动创建新的 clientId 和 sessionId，绑定持久 instanceId。
连接请求带新的 correlationId 挑战；会话响应必须回显该标识。
Host 为该次启动生成只驻留进程内存的 P-256 私钥；会话与动作授权响应使用
`nexus.desktop.proof` v1 包装，按 SHA256 和 IEEE-P1363 的 ES256 签名原始 payload 字节。
启动器必须先核实实际 Host 进程拥有权，再向壳提供 SPKI 公钥；不能从未经核验的 HTTP 服务自取信任锚。
壳在接受新会话或执行动作前校验固定公钥、当前 schema、实例、代际与请求关联。
旧挑战的签名响应不能完成新握手，端口占用者不能只靠同形 JSON 取得原生动作。
Core 只授权列举的文件选择、目录选择、无凭据 HTTPS 外部打开和关闭请求。
同一 correlationId 在该会话内最多批准一次；未确认结果不得自动重发。
请求上限 4096，达到上限后拒绝新动作，保留旧标识以防重放。
消息验证使用当前新格式 schema；动作请求最多 8192 字节，响应禁止缓存。

批准响应只表示 Host 已确认该动作，并不证明原生窗口已完成或外部副作用已发生。
壳还必须核对 frame/source、文档代际、会话与响应关联，再执行固定原生动作。
不提供任意路径读取、命令执行或通用 invoke。
文件内容不经过上述授权入口，取消选择不返回路径。

插件的 native 资格绑定原已鉴权 frontend client grant 与实际桌面会话。
只有登记主进程的真实 TCP 连接可以绑定这两个身份；客户端标识必须与当前 grant 完全一致。
Electron 主 frame 先验证 Host 签名，再发布该绑定；文档换代、退出和 grant 退役会撤销对应资格。
`Desktop.ReadClientAsync` 只读取该 client 的 `IsBound` 与捕获可用性，不授予交互权限。
`RequireClientAsync` 还要求当前注册的 Desktop 操作、原 client 及两端原始生命周期均有效。
普通只读调用、自报 `IsLocal`、payload 中的 clientId 或全局桌面在线状态不能代替上述授权。
`Desktop.CaptureAsync` 另要求 `desktopAccess: ["interactive-login"]` 与明确的 `loginOrigins`，不借用后台网络来源权限。
`CanCapture` 还依赖实际原生主进程近期的采集协议确认；只有在线或已绑定都不足以判定可采集。
Host 冻结有限来源、cookie/storage/被动 JSON 选择器及完成谓词；秘密结果只返回可销毁内存草稿，不能经普通 JSON 序列化。
每次采集使用独立内存分区；主窗口与受控弹窗关闭、网络连接结束、存储和缓存清理后才确认结果。
原请求、client 或 activation 退役后拒绝迟到候选，并继续保留 SDK 借用，直至原生清理确认或确切进程退出。
原生绑定被撤销不等于进程已经退出；无清理证明时不报告释放成功。
停止等待有明确上限；超时报告 `browser_login_cleanup_unconfirmed`，保留未释放声明并拒绝迟到秘密。
Desktop 插件调用的 HTTP 上限为六分钟，采集等待上限为五分钟；正常业务调用保留三十秒上限，原调用的取消会传播至采集 owner。

关闭请求由 Core 生命周期 owner 冻结新写入，等待写入租约结束、恢复索引就绪且无隔离资源，
再签发绑定当前会话、请求和恢复修订的 30 秒许可。原生进程退出后再次核对许可条件，Host 才结束。
启动时先验证新 SQLite 与物理 journal 的一致性并执行已登记恢复，再发布就绪。

生产桌面已选择 [Electron](../decisions/desktop-shell.md)，源码和锁定运行时的装配入口见 [桌面入口](../../apps/desktop/README.md)。
Host 向自己创建的壳传入本次启动公钥，原生动作仍核验真实 TCP 连接，不能从 HTTP 自取信任锚。
`GET /control/v1/desktop/maintenance` 只向当前原生会话签发已准备的维护退出许可；空轮询不占用动作关联标识额度。
壳须先收到 renderer 对普通草稿的明确准备答复，再自然退出；缺答复不强杀窗口以执行更新。
只读维护轮询不占用原生动作锁；用户关闭仍先准备草稿并取得新的签名许可。轮询收到维护许可后，等待正在执行的原生动作结束，并复核文档代际与许可期限。
维护等待根及全部 owned 子进程的原始句柄结束、完整清单与 Job 计数相符，之后释放旧映像句柄并启动维护程序。
维护程序的启动交接等待为 60 秒；本地载荷核验与替换使用独立的 5 分钟时限，成功后候选握手另起 60 秒。
候选在确认维护程序实际退出后提交更新，再启动普通 Host；维护程序退出不等于 HTTP 已就绪。
成功候选使用原请求中冻结的窗口模式、可见性与路由意图。固定网页登录、完整 UI 和异常重连接入仍属后续阶段。
普通 loopback 控制权限继续按公共入口策略处理；原生授权不改变同机用户的系统信任边界。

Windows 查询依据见 [GetExtendedTcpTable](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable)。
原生侧来源校验分别参考 [Electron 安全指导](https://www.electronjs.org/docs/latest/tutorial/security)
和 [WebView2 消息事件](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.webmessagereceived)。
签名编码见 [.NET DSASignatureFormat](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dsasignatureformat?view=net-10.0)
与 [Node crypto.verify](https://nodejs.org/docs/latest-v24.x/api/crypto.html#cryptoverifyalgorithm-data-key-signature-callback)。

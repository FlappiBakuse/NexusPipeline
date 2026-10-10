# 客户端会话与网页登录

Plugin API 2.2 的 `IPluginHostContext.BrowserLogin` 管固定 flow；Frontend API 1.7 提供 `host.clientSessions.get()` 和 `host.browserLogin.open(request, signal)`。Host 管身份、窗口与清理，插件管抽取、平台只读验证和凭据候选。

`PluginWebApiRequest.ClientSession` 由 Host 注入，包含 Host/客户端 ID、kind、nativeBrowserAvailable 与 Lifetime 撤销令牌。HTTP 只能创建 web；desktop 仅经核验的监督管道签发且限本机使用。认证轮换、Host 重启、明确撤销及桌面断连撤销相应会话。web token 仅存当前页面内存，新页签和刷新建立新客户端；桌面隐藏/显示保持客户端。`401 client_session_required` 与管理认证失效分别处理。

`BrowserLogin.Register(flow, complete, onTerminal, validateInvocation)` 返回可释放注册。flow 固定版本、HTTPS 起点、导航/弹窗 origin、精确 Cookie domain/path/name，或 storage origin/key/有限 JSON path。前端只传本插件局部 flow ID，Host 添加 owner 命名空间。不接受插件脚本或任意网络抓取。

`validateInvocation` 是开窗前的同步有限本地检查，验证 editor、field generation 和后端 nonce。`complete` 接收选定值及 invocation，规范化并只读验证，返回 provisional 候选；签到和领取奖励不属于验证。原始值只在桌面 main 与后端之间传递。

浏览器清理成功后，`onTerminal(Completed)` 激活候选；Host 等激活成功才向 renderer 发布结果。取消、清理/激活失败及激活期间撤销触发 `Cancelled`。插件须幂等撤销 provisional，补偿未公开激活。关闭编辑器调用 `CancelEditorAsync(client, editorSessionId, token)`，停止插件释放注册。

每个 operation 使用新的内存 partition，不含 persist。窗口标题为 `NexusPipeline Web`，可信工具栏在平台标题右侧显示验证提示，不展示网址。可信工具栏与外站 WebContentsView 分离，外站无 preload/Node/管理桥，启用 sandbox/context isolation；默认拒绝权限、下载、外部协议和未登记导航/弹窗。允许的弹窗归属同一临时 session。工具栏只接受正确窗口主 frame 的 IPC。

operation 最长 15 分钟，采集 10 秒，只读验证 30 秒，清理和终态回调各 10 秒。清理 Cookie、storage、缓存、HTTP 认证缓存和连接，销毁视图/窗口。关闭、取消、断连和 Host 停止均收敛到清理。

前端 open 返回 `{operationId, completed, cancel}`。完成结果仅有成功、候选句柄、配置状态、browser 来源、脱敏身份或稳定错误。轮询/取消绑定发起客户端，不返回原始值。仅真实平台完整流程通过且证据匹配当前 flow/提取器版本的平台注册 ready；合成凭据、受控响应或登录成功页面不足以证明资格。

GameCheckIn 管自身 editor/候选和任务事务。browser 来源的明文不进入普通 DTO 或编辑初始化；主动显示经受控眼睛事件、明确确认和专用读取。确认绑定当前 Host/客户端，按单调时长固定最长 24 小时，读取/续租/显隐不延长。修改 browser 值仍为 browser；明确清除后重新输入才转 manual。

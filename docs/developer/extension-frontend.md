# 插件浏览器入口与数据端口

已安装 managed 插件声明 `ui.contribute`、`frontend.entry`、`frontend.styles` 和前端 SDK 版本。
JS 必须是自包含 ESM bundle，导出 `createFrontend(transport)` 并返回当前 SDK 的 `FrontendExtension`。
包中程序集、浏览器资源、manifest 和目录条目分别核验原字节及兼容身份。

Host 的 `/control/v1/extensions/frontend` 返回当前 published 激活和已验证 manifest。
资源 URL 同时绑定 extensionId 与 activationId，只读取清单声明的 entry/styles。
目录、身份复核、资源读取和业务调用沿用控制面鉴权；远程未鉴权请求不能取得模块内容。
Web 使用鉴权 fetch 取得原模块，再通过 Blob URL 导入；CSP 的 `script-src` 为 `'self' blob:`。
模块 URL 在导入后释放，样式与路由随浏览器 scope 撤销。

加载前后核对当前激活身份。Host 事件的 `extensionGeneration` 表示 published 激活集合，
安装变化、停用与重新激活会使浏览器协调同一注册表；断开会取消旧连接的加载与表面。
旧激活的 URL、业务调用、晚返回结果与 SDK 端口均不能取得新激活的拥有权。

## 业务调用

程序集可实现 `IExtensionFrontend`，在激活结束时冻结至多 64 个明确命名的 operation。
`FrontendAccess` 区分读、写、秘密显示和桌面能力；秘密显示与桌面能力继续受本机授权限制。
写调用必须具有非空 operationId，读调用不能携带写身份。
Host 将原调用主体和当前权限复核传递到同一消费作用域，再调用插件处理器。
处理器不取得 Host 服务容器、私有 DTO 或任意文件访问。

浏览器通过已鉴权的 `POST /control/v1/extensions/clients` 取得独立内存会话令牌，
业务请求以 `X-Nexus-Frontend-Client` 携带令牌，Host 向 `FrontendCall.ClientId` 注入核验后的身份。
会话绑定原主体、控制面 origin 和原权限，最多保留 24 小时；每 Host 最多 128 个现役会话。
远程会话还绑定创建时的实际 Bearer 引用及 verifier；后续请求用当前已鉴权证明复核同一引用和双方当前权限，凭据轮换、跨实例或缺少凭据均撤销会话。
注销会撤销该页面的会话，在途回调返回前再次核验。请求 JSON 中的客户端标识不授予会话权限。
`FrontendCall.ClientLifetime` 随会话退役、到期或 Host 结束取消，用于释放该客户端拥有的秘密草稿和确认状态。
没有会话身份的调用仍可使用普通公开操作；秘密确认、登录草稿等客户端私有状态必须拒绝空身份。

`FrontendTransport.invoke` 处理 JSON 请求与响应。请求上限为 1 MiB，
返回 JSON 经过严格解析；写入响应丢失时保留原操作身份，不能自动重复副作用。
运行预览使用 `preview` 和既有媒体 grant；每次返回的图片租约必须释放。
关闭 grant 不代替释放图片，也不允许将已结束 attempt 的画面发布到新任务。

`FrontendTransport.image` 使用绑定激活的鉴权 GET，只可调用声明为 Read 的 operation。
处理器取得 `{ assetId }`，返回 PNG、JPEG 或 WebP；此入口不能执行写、秘密显示或桌面操作。
浏览器图片池核对 MIME、容器尺寸与图片预算，按租约共享 Blob；最后一个租约释放时归还编码字节预算。画布消费者直接解码 Blob，不创建对象 URL；需要 `<img>` 的插件自行拥有并在取消或卸载时撤销其展示 URL。

## 普通 JSON

`ExtensionActivationContext.Documents` 由 Host 提供插件命名空间中的普通 JSON。
清单必须声明 `permissions.fileAccess: ["instance-data"]`；每插件最多 64 个文档，单文档最多 1 MiB。
文档名只接受小写字母、数字、连字符、点与下划线，不作为任意路径解析。

保存将原主体、extensionId、文档名、原 revision、内容摘要和 operationId 绑定到同一个 SQL CAS。
失败事务不消耗操作身份；重复操作返回原 revision/hash，不把旧内容覆盖到较新的保存上。
`ReadCommitAsync` 仅允许原主体查询原提交，供丢失响应后的结果核验。
已保存内容先校验摘要与严格 JSON；未知、重复成员或损坏内容保留原字节并拒绝读取与覆盖。
凭据继续通过秘密端口管理，不写入普通文档。

`ExtensionActivationContext.SecretDocuments` 将一个普通文档与至多 32 个明确的凭据变更原子发布。
文档继续使用 `Documents.ReadAsync` 读取，凭据继续使用 `Secrets` 的加密存储和权限。
`WriteAsync` 消费全部 keep/set/clear 草稿并在失败时释放内存；预期文档和凭据 revision 均须匹配。
所有候选密文完成核验前，读者保留旧配置和全部旧引用；最终 SQL 提交同时切换配置与凭据并登记退役。
待恢复的子日志同时隔离插件数据和秘密字段；恢复须先核实全部旧 writer 已停止。
未进入的本机候选可从已冻结密文继续准备，部分写入且无完成证据的候选保留原字节并拒绝发布。
原操作响应丢失时可查询 `ReadCommitAsync`；重放返回原 revision，不覆盖更新后的配置。

`ExtensionActivationContext.NotificationTargets.ReadAsync` 读取现役通知目录中的 ID、revision、名称、启用状态、配置齐备状态和渠道种类。
配置状态与实际 transport 共用必需字段规则，在同一数据库快照核对通知目录及秘密引用；它不证明网络可达或外部送达。
清单须声明 `notification.submit`；此端口不返回收件人、渠道配置或凭据，也不代表已取得投递授权。

## 编辑草稿能力

`ExtensionSurface.drafts` 提供绑定实例、插件、编辑目标和表面的窄内存作用域。`retain` 保留普通状态，`protect` 另存秘密值并要求清除回调，保护对象拒绝普通 JSON 序列化；均不持久化。只保留实际编辑过的目标。关闭/卸载释放 DOM、读取、计时器和显示缓存，重开复用数据与原修订。秘密输入重开默认遮罩，已有披露/登录授权仍独立核验。

父取消先检查整个子树的 `guardCancel`，存在在途或未知写入时保留原操作；通过后递归 `settle('cancel')`。成功保存通过 `settle('saved')` 结清所属提交。父定义保存使用 `onParentSaved` 通知，独立配置草稿保留。插件退役、界面会话结束以 `disposed` 清理全部内容。视图信号用于只读资源，已发送写入使用草稿信号和原操作编号，结果未知时保持身份并按原协议核对。

即时设置的活动与壁纸不注册此能力；宿主与插件共用的交互规则见[编辑草稿](frontend-state.md#编辑草稿)。

## 激活就绪

`ExtensionActivationContext.Activation.WaitReadyAsync` 用于激活返回后的 scope 自有初始化。
就绪等待原安装 writer 及其所有借用释放，并由 Host 发布和核验当前恢复投影。
每次激活共享同一就绪任务；取消某个等待者不会撤销其他等待者，也不会提前发布安装。
激活停止会取消尚未完成的初始化。`ActivateAsync` 内只登记后台初始化，不能等待自身安装 writer 释放。
前端入口也等待同一就绪边界；后台读文档和网络请求通过有界资源准入，保留当前激活和原调用权限复核。

## 图片与桌面会话

`ExtensionActivationContext.Images` 保存在当前插件的 `instance-data` 命名空间。
`WriteAsync` 一次提交普通 JSON、完整图片引用与至多一张新图片，沿用文档 revision 和原 operationId。
新图按原字节 SHA-256 标识；每插件最多 32 张、单张最多 8 MiB、总量最多 256 MiB。
图片原字节和引用保存在同一 SQL 事务，提交失败不留下半份图片或消耗操作身份。
其他文档仍有引用的图片继续保留；最后一个引用删除时在同一事务退役。
已有孤立记录、损坏摘要或不认识的图片数据会阻断危险清理并保留原记录。

`FrontendTransport.upload` 使用单独的已鉴权原字节入口，支持 PNG、JPEG 与 WebP。
入口仅允许 Write operation，绑定原 operationId 和展示文件名；文件名不授予路径权限。
每 Host 最多两个在途上传，单次最多 8 MiB；其请求体边界不放宽其他控制面入口。
返回丢失或取消后的结果可能未知，不能自动分配新身份重发。

`ExtensionActivationContext.Desktop.ReadSessionAsync` 只读取当前原生桌面会话。
会话身份在 `NativeClientBinding` 验证实际 TCP 进程且完成握手后产生，原生会话结束时撤销。
本机浏览器和远程页面不能通过普通业务调用创建会话。
插件可据此让多个页面共享一次启动轮换，不从 loopback 地址猜测原生客户端。

## 网络

`ExtensionActivationContext.Http` 只接受已安装清单中 `networkOrigins` 的准确 origin。
Host 复用当前代理设置和受保护代理凭据，不向插件公开代理密码。
每激活至多四个在途请求，单请求最长 30 秒，请求体最多 1 MiB，响应最多 8 MiB。
重定向、编码转换、危险传输头和超过边界的响应不会扩大访问权限。
Cookie 与 Authorization 可由插件为明确业务请求提供，端口不建立共享 Cookie jar，也不记录请求或响应内容。
字段名接受 HTTP token 字符，包括上游协议使用的下划线；Host、代理、内容和传输控制头仍由 Host 管理，非法名称及换行值在传输前拒绝。
激活停止和原调用取消会取消传输；返回前再次核对原主体及激活仍有效。

## 验证

`storage-frontend` 清单登记真实安装资源、权限、回调排空、文档 CAS、网络边界和数据库保全用例。
浏览器安装测试消费实际确定性 ZIP，通过原安装子进程的完整停止证明后启动实际 Test Host，
再以隔离 Edge 读取生产 Web、鉴权模块并验证停用和重新激活。
这一范围不代替全部插件业务、设备、生产发行信任或整机性能验收。

## 准备与贡献元数据

`activate` 可异步准备默认只读数据和本地资源；注册完成前等待准备结果，随后由 `mount` 复用快照。宿主只汇总通用加载状态；插件自行决定缺失缓存的稳定占位。轮询和用户业务动作仍由可见性及交互入口控制。取消信号撤销所有迟到结果。

`overview.cards` 的贡献声明 `dashboard`：稳定键 `key`、标题、说明、默认显示和排序值。键最长 96 个字符，允许小写字母、数字、点及连字符；全局唯一，不能使用内置卡片键。标题/说明可以使用随语言变化的 getter。每个插件可贡献多张卡片。已存布局保留暂时不可用的键，恢复插件后继续使用原排序与隐藏选择。

manifest 可声明 `icon`，指向包内 PNG 或 ICO，最大 1 MiB。数据型插件同样可使用。宿主经 `extensions.read` 授权的静态图片接口核验安装证据、载荷成员、大小、摘要和图片签名后返回；无图标时返回空响应，前端显示通用图标。插件专属名称、说明和图标由插件包提供。

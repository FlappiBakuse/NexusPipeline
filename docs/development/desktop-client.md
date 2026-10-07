# 桌面管理与应用载荷

根 `NexusPipeline.exe` 是 Host、CLI 和启动入口。普通服务默认只运行后台与托盘；无参数启动、托盘显示、CLI ui 或前端显式打开都激活同一桌面窗口。`resources/desktop/NexusPipeline.Desktop.exe` 是固定 Electron 44.5.1 运行时；业务 Vue 从 Host 内嵌资源通过实际 localhost 端口提供。用户安装无需 Node、npm 或 SDK，.NET Desktop 与 ASP.NET Core Runtime 10 x64 仍为运行依赖。

窗口 X 隐藏；托盘退出或更新才停止桌面。服务行为中的界面语言、侧边栏的主题偏好与窗口位置分别持久化在当前实例私有运行目录，不改变 Host 通知语言。标准业务尺寸为 1280×720 DIP，另加 40 DIP 标题安全区；初始尺寸按工作区物理像素档位计算并夹紧，恢复时重新验证显示器、DPI 和最大化状态。工作区无法容纳最小界面时由用户选择继续打开、仅后台或取消打开。

监督 IPC 使用同 SID、同权限等级、NETWORK 显式拒绝的命名管道；内核提供 PID、映像路径与启动时间，双 nonce HMAC 验证两端身份。根入口只能发出激活请求，不读取桌面会话密钥。记录签名、私有 ACL 和完整进程家族约束重连、关闭和资源替换。未知或损坏记录保留原字节并拒绝接管。

进程映像通过 Windows `QueryFullProcessImageNameW` 查询，与启动时间一起固定身份；模块加载尚未完成的进程也使用其实际 EXE 路径。身份查询失败时拒绝确认该进程。

Host 重启保留桌面 renderer，准备状态暂停轮询和写操作，dirty guard 处理编辑取舍。重连候选固定实际端口、Host 实例、构建身份、当前 frame、路由和导航代次；只在当前候选获准后导航。意外断线保持当前 renderer 并显示恢复状态；有限重试后给出重试/退出操作。更新前刷新偏好并退出整个已确认桌面家族，剩余进程或文件锁使替换失败。

工作空间界面沿用八个业务页面、真实表单与状态；导航分为日常运行、配置管理和工具设置。详情与编辑进入公共二级表面，公开插件 slot/route 和 Native Custom Elements 名称保持稳定。网页保持自身浏览器行为，桌面桥提供版本化的窗口、客户端偏好、导航和纯文本复制接口。复制仅接受当前受信任业务主 frame 的字符串，最大 64 KiB；不提供剪贴板读取或其他格式访问。桌面继续拒绝浏览器权限请求。

桌面窗口、任务栏和桌面 EXE 图标统一来自 `src/NexusPipeline.ico`。构建使用 Windows 资源 API 替换桌面 EXE 的图标组与图像，保留其他资源及原执行权限清单；窗口使用同一 ICO 的独立载荷文件。运行时文件索引固定替换后的 EXE 和 ICO 字节。

生产 profile `win-x64-en-zh-v1` 保留 en-US、zh-CN 的运行时语言包及全部非语言运行文件、Electron/Chromium/Koffi 许可。ready 前读取首选系统语言：中文别名映射到 zh-CN，其余映射到 en-US；业务语言仍由现役服务设置管理。[Electron app 文档](https://www.electronjs.org/docs/latest/api/app)与[命令行开关](https://www.electronjs.org/docs/latest/api/command-line-switches)说明对应原生语言接口及 `--lang`。锁定运行时的实际行为须继续由桌面验收确认。

Koffi 依赖按 profile 的逐文件白名单装配；native 文件仍位于 `resources/app.asar.unpacked/node_modules/@koromix/koffi-win32-x64/win32_x64/koffi.node`。解压后先验证 runtime 的精确集合、长度和 SHA256，再装配 ASAR 和完整 payload。类型、测试、文档与构建脚本留在源工程，构建输出只收录运行时白名单。

## 构建与验证

Test Host 在现役 runner 设置 `NEXUS_SYSTEM_ACTION_DRYRUN=1` 时替代防火墙命令；原生适配器契约验证参数、有限的创建规则回退和未调用系统命令的效果。生产构建不读取该替代开关，实际入站连通性仍由生产环境验收。

所有本机产物应放在外部目录。设置 `NEXUS_TEST_ARTIFACT_ROOT` 和 `NEXUS_PARTNER_ROOT` 后执行 `node tests/run.mjs release`，现役 runner 隔离源码、构建 Vue、冻结输入、装配 Electron、内嵌 Host 身份并生成完整应用 manifest。`NEXUS_ELECTRON_ARCHIVE` 可指向已固定摘要的官方 ZIP；未指定时从精确官方地址下载并校验。构建输出路径由 runner 报告。

`node tests/run.mjs gate --id host.integration.restart-update` 使用 asInvoker 完整软件验证整包交换、启动凭据、恢复和用户字节保全。该 runner 构建隔离的 0.17.0 全清单与 0.17.1 精简清单，验证不同补丁及文件集合的更新、损坏回滚和未知文件保全；夹具不改变产品目标版本。`host.integration.desktop` 执行五项原生 TypeScript 用例、八项真实载荷只读校验以及真实 Electron 中的页面、偏好、重启草稿保留、明确刷新、renderer 崩溃恢复和隐藏行为。原生提示的确认动作在自动化中由受控替代记录；DPI、多屏和生产 UAC 继续人工验收。生产软件清单要求 requireAdministrator，自动化不触发 UAC。

载荷资源与 canonical buildInputs 见[构建身份](build-identity.md)，访问能力见[Plugin API](../reference/plugin-api/managed.md)，人工与远端未验证范围见[状态](../STATUS.md)。

# Electron 桌面入口

Host 侧原生阶段进度、隐藏启动和控制台边界见 [启动反馈](../../docs/developer/startup-feedback.md)。

根目录手动入口按保存模式打开界面，登录自启的 `desktop --runtime-root <绝对路径> --port saved` 遵循保存的可见性。维护恢复遵循已验证的窗口意图。

Host 提前启动唯一所属 Electron 托盘进程族，通过限定 PID 的本地管道推送真实启动阶段。桌面模式在主窗口的本地覆盖视图显示进度，服务与签名会话就绪、Web 首绘完成后切换为控制台。轻量模式仅显示托盘，按意图打开系统浏览器，不创建管理页面 renderer。

窗口加载绑定 Host 回环地址的统一 Web 产物；浏览器和 Electron 使用相同页面和控件。启动覆盖视图只有窗口操作与失败诊断能力；业务动作继续执行原有来源、会话和签名检查。

运行时固定为 `runtime-lock.json` 中的官方归档，不通过 npm 安装或使用机器上的其他 Electron。
使用仓库工具装配到 Host 载荷旁的 `desktop` 目录：

```text
python tooling/nexus.py desktop-bundle --runtime-archive <锁定的官方ZIP> --output <外部Host载荷目录>/desktop
node --test tests/frontend/native-close.test.cjs
node --test tests/frontend/host-page.test.cjs
python tooling/nexus.py test --group storage --method ActualOwnedElectronMaintenanceRestartsTheCandidateWithTheFrozenVisibleSettingsWindow --desktop-bundle <外部Host载荷目录>/desktop --build-root <Test构建目录> --output <外部报告目录>
```

工具验证归档大小和摘要，记录完整文件清单；桌面源码通过固定的官方 `@electron/asar` 生产者生成 `resources/app.asar`，逐项校验成员与当前源码字节及 SHA256 integrity。源码、归档存在缺失、多余成员、链接或未拥有数据时拒绝装配。重复装配先核对原装配的全部字节，拒绝未知内容。
归档构建使用 Node 22.12 或更新版本及原生 npm 锁文件；该构建依赖存放于外部工作区，不随软件发布。Electron 运行时仍取固定官方 ZIP。
依赖和装配产物全部位于显式外部路径。Host、Maintenance 和桌面的整体生产清单另由候选装配生成。
真实桌面维护用例显式接收完整 bundle，并核对运行时、当前桌面源码及全部文件；默认 storage 组明确列出未执行的这一外部输入用例。该用例须从正常无 Job 的本机入口运行，仍由产品核验实际入口。

桥接只允许绑定回环地址的主 frame，页内 hash 导航保持该身份；renderer 无 Node、shell、任意 invoke 或通用文件读写。
原生动作通过已绑定主进程的实际 TCP 连接向 Host 请求，并验证本次启动公钥签名和请求身份。
Host 的维护退出许可只在更新排空及原载荷租约有效时发放；壳先请求 renderer 准备普通草稿，再自然退出。
没有肯定答复、renderer 丢失或许可失效时保留窗口，维护不能切换载荷。
Host 确认完整 owned 进程族的原始句柄均已结束后才启动维护程序。
已受理的重启交接按 Host 自身的30秒期限完成；发起窗口按许可退出并断开 HTTP 请求时，不取消已经进入的交接。

Web 通过 `nexusDesktopChannel.prepareExit(callback)` 注册普通草稿确认。
有在途写入时拒绝退出；有未保存普通草稿时先确认。回调只有明确返回 `true` 才允许退出。
秘密仅驻留所属编辑器内存，不进入退出交接或普通持久化草稿。

托盘右键使用 Windows 系统菜单，提供轻量模式、页面重载和退出；左键恢复界面。隐藏保留当前 renderer 与普通草稿；
重载及切入轻量模式先走同一普通草稿准备，有在途写入或未获同意时保留原窗口。
轻量模式通过现役设置 CAS 保存 browser 选择，成功后释放 BrowserWindow；关闭轻量模式重建并显示同一 Web 页面。
完成实际窗口切换后，由真实 native TCP owner 确认当前 session 的运行模式，Host 才更新 effective desktop mode。
设置保存、窗口动作或原生确认失败不会冒充已更新的 effective 模式；原生 owner 退役后不能更新。
浏览器模式的显示/页面重载打开系统管理页面，不控制用户拥有的浏览器窗口。
两模式的退出仍使用 Host 签名 close permit；没有主窗口时不伪造 renderer 草稿确认。

原生菜单与文件保存确认读取当前界面语言，已激活 renderer 通过受限 `setLocale` 更新；
Browser 模式打开托盘时读取同一 interface preferences。登录窗口标题使用创建时的当前语言。

真实 Electron 局部运行验证使用 `tests/frontend/desktop-runtime.cjs --output <外部证据根> --mode <desktop|browser>`，
由锁定 Electron 可执行文件加载。它执行生产 main、Tray、BrowserWindow、renderer 和 IPC，替换签名 HTTP 与外部浏览器启动 I/O；
结果不代表同来源完整 Host 装配、外部浏览器实启、视觉或性能资格。

完整日志使用系统另存为选择器与流式写入。Electron 的文件状态检查在所选路径写入前显示原生确认；授权只缓存于当前文档，并在完整导航时清除。检查来自空 WebContents 的保存授权时仍核对绑定页面、请求 origin 和绝对文件路径，其他权限及目录写入保持拒绝。取消选择器不创建文件；拒绝写入不输出日志字节，系统选择器可能已创建空文件。

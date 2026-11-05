# NexusPipeline 0.18

Windows 11 x64 本地自动化管家。

启动 `NexusPipeline.exe`。运行数据保存在同目录的 `runtime`，桌面与浏览器模式共用同一实例。
程序需要 .NET 10 x64 共享运行时；Setup 会检测所需组件。

`NexusPipeline.update.exe` 由宿主完成更新交接后启动。更新只切换应用文件，保留运行数据及已有插件。
移动、备份或维护目录前，请退出本实例的宿主、桌面、托盘与任务进程。

从 0.17 升级时，请使用新目录安装 0.18。两代使用独立实例，不复制或原位覆盖旧数据。

默认提供模拟器支持与实时截图两个插件。其他插件可在插件中心安装。
卸载或重新安装插件会保留账号与配置；数据清理需要显式选择。

---

Start `NexusPipeline.exe`. Runtime data is stored in the adjacent `runtime` directory.
Desktop and browser modes use the same instance. The application requires the .NET 10 x64 shared runtime; Setup checks the required components.

The Host starts `NexusPipeline.update.exe` after the update handoff. Application updates preserve runtime data and existing plugins.
Exit the instance's Host, desktop, tray and task processes before moving, backing up or maintaining its directory.

Install 0.18 in a new directory when upgrading from 0.17. Keep the generations in separate instances.
Emulator Support and Live Screenshot are provided by default; install other plugins from the plugin centre.

# 生产桌面采用 Electron

生产桌面使用 Electron，并保留独立浏览器模式。离线分发包含锁定版本的桌面运行时和 Web 资源。
Electron 的进程生命周期、托盘和离线部署适合当前 Host 统一拥有桌面进程族的边界。

Host 负责实例、就绪、恢复、准入和统一关闭；桌面通过类型化能力连接。
托盘与显示隐藏各有一个 owner，浏览器模式复用 Host 生命周期。
生产桥接仅开放声明的能力，不提供通用 eval、任意命令或旧协议 fallback。
秘密草稿归独立内存 owner；renderer 丢失后提示重新输入，未知操作不自动补发。

运行时锁记录完整文件集合及摘要。缺失或摘要错误明确拒绝桌面启动。
更新通过已验证载荷和受控维护交接，确认完整 owned 家族停止后再替换。
运行时更新使用同一版本锁和验证流程，启动时不静默下载另一版本。
桌面失败时保留诊断，由用户选择浏览器模式。
生产管理员清单和隔离 Test Host 的 asInvoker 清单保持各自政策。

实现见[桌面启动](../architecture/desktop-startup.md)、[桌面授权](../reference/desktop-authorization.md)；
测量口径见[性能与有界资源](../development/performance.md)。

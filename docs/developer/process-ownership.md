# Windows 进程归属与观察

OwnedWorkerJob 在恢复控制的写入进程执行前，按挂起创建、加入本次 Job、持久登记完整身份、恢复主线程的顺序启动。
登记包含 PID、创建时间、映像路径与 SHA256、用户 SID、提升状态和实例/attempt 身份。
登记失败不得恢复线程；Job 关闭时终止其拥有的进程，不按名称结束外部进程。

Job 的累计 CPU 计数包含已经退出的成员。进程清单与累计创建数量分别查询，不能用当前存活 PID 清单推断历史进程集合。
停止时核对 Job 成员、保留的根进程及成员句柄，确认 Job 活跃数为零且对应内核进程句柄均已结束。
捕获期间出现新进程、成员归属不明或句柄未结束时，不证明 writer 已停止。

可在第一次分配进程前附加唯一的 ProcessStartObserver，通过专用 completion port 接收创建通知。
观察回调须及时保留进程句柄并核实身份；PID 通知本身不证明进程仍存活，也不抵抗 PID 重用。
Drain 等待已排入观察队列的回调结束；回调与 completion port 失败通过 Failures 保留。

Windows 的普通 Job 通知允许丢失，观察器不提供保证不漏事件的承诺。
需要完整进程族的测量必须将已观察身份数量与 Job 累计创建数核对，并检查所有观察错误。
缺失身份或未知归属使该轮测量不完整，不能补填零，也不能仅凭通知结束记录证明恢复安全。
采样器的观察能力不代替运行时持久拥有权及恢复前 writer 停止验证。

原生依据见 [Job completion port](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_associate_completion_port)
和 [GetQueuedCompletionStatus](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-getqueuedcompletionstatus)。

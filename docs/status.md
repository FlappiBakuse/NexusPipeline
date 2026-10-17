# 未完成问题

本页只记录 Host 的未解决问题与验证缺口；维护规则见[文档规范](development/documentation.md)。条目不提供新的操作授权。

## Host 验证缺口

| 范围 | 当前证据边界 | 完成条件 |
|---|---|---|
| 生产维护 UI | Windows Sandbox 覆盖旧版 Setup 升级、工具修复、取消/保留数据卸载、TCP 取消/断流/关窗；其 UAC 禁用且为 96 DPI | 补真实 UAC 接受/取消、150%/200% DPI、长错误、Ready 键盘操作及连接 Host 的慢下载取消/关窗证据 |
| 飞书签名推送 | 缺真实机器人环境验证 | 在明确授权的机器人范围内验证实际签名发送和响应 |
| 更新故障组合 | 已有受控恢复回归不覆盖所有组合 | 补长时间运行、文件锁与异常退出组合的恢复与数据保全证据 |

## 跨仓验证

官方插件的三版窗口、真实游戏/模拟器资格、Maa 驱动与插件业务回归缺口由 [Plugins 状态台账](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/status.md)维护。Host 的隔离进程、Jint、provider 与 Smoke 证据只证明对应宿主边界，不替代对端真实设备或账号资格。

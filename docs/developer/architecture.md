# 工程与所有权

| 工程 | 责任 | 编译依赖 |
|---|---|---|
| Core | 业务规则、用例、ports | .NET 基础库 |
| Platform | SQLite、文件、Windows、网络、扩展适配 | Core、Extensibility |
| Host | 组合根、Kestrel、CLI、MCP、生命周期 | Core、Platform、Extensibility 的已登记公开 DTO |
| Maintenance | 宿主退出后的安全切换与恢复 | Core、Platform |
| Installer | Setup 的每用户 worker 入口，与 Maintenance 共用安装命令及 Platform 生命周期 | Core、Platform |
| Extensibility | 独立公共 SDK，0.1.0 | .NET 基础库 |

Core 不引用 SDK DTO、数据库、Host、UI 或 DI 容器。
Core 使用 .NET 10 自带的 `System.Collections.Immutable` 保存不变定义集合，该标准库在编译边界中显式登记。
Core 使用标准库 `System.Security.Cryptography` 的 SHA256 为调度 occurrence、run 与 start 生成确定性身份；编译边界仅为 Core 登记该依赖。
Core 使用标准库 `System.Threading.Channels` 实现有界日志订阅队列，慢读者断开不会反压原始日志保存或业务判定；编译边界仅为 Core 登记该依赖。
Core 使用标准库 `System.Text.RegularExpressions` 校验有长度和执行时间上限的更新版本字符串；编译边界仅为 Core 登记该依赖。
Extensibility 使用标准库 `System.IO.Pipes` 连接宿主为当前 worker 绑定的本机命名管道；编译边界仅为 SDK 登记该依赖。
Extensibility 使用 `System.Security.Cryptography` 校验设备帧 SHA256，使用 `System.Text.Encoding.Extensions` 严格解码项目 UTF-8；编译边界仅为 SDK 登记这两个标准库依赖。
Host 可传输 Platform 已校验的公开 `Extensibility.Protocol` DTO，以及项目配置保存返回的 `Extensibility.State.DocumentCommit` 值回执；编译门禁仅登记这两个精确表面，仍拒绝 Host 引用 SDK ports 与运行时类型，激活、执行和权限转换继续归 Platform。
Platform 在边界转换公开 SDK DTO 与 Core port。
Automation、Execution、Configuration、Scheduling、Records、Notifications、Extensions、Maintenance、Settings、Secrets 各自拥有自己的事实。
跨存储或外部进程副作用以持久 intent、拥有权与 receipt 恢复。
[执行准入](execution.md) 由 Core 统一预留与交接顺序，Platform 实现冻结输入、持久受理和运行 inbox。
未知格式保留原字节并阻断相关操作，不重置现场。

桌面实测选定 [Electron](../decisions/desktop-shell.md)，`apps/desktop` 只接入这一生产实现。
浏览器模式继续保留，WebView2 对照原型仅在仓外证据中保存。
前端归 `apps/web`，契约权威归 `contracts`，官方插件独立消费固定 SDK 产物。
资源目标与容量见 [性能规范](performance.md)，完整成品仍须实际验收。

新增 Host/SDK 边界须对应现有用户用例或已复现缺陷，明确 owner、必要约束和完成条件。
执行主线保持可顺序阅读；受影响范围内合并无独立约束的转发层，持续持有的不可变输入在同一拥有期复用。
跨进程、撤销、可变资源、配置恢复与不可逆副作用的必要核验继续保留。
浏览器契约的目标输入为真实 Web schema 根及其传递引用闭包，仍从 `contracts` 单一权威生成；
完整源码追溯与组件构建复用分别管理，不能以文档或无关模块变化无条件扩大运行时依赖。

Storage 中具体用例顺序见 [流程归属](storage-owners.md)：Host 权限与 DTO 边界、Core 纯规则和 Platform SQL/文件 effects 由各自 owner 承担；路径不代替职责。

源码规则由 `Boundaries --source-boundaries` 使用 Roslyn 检查：业务不解析服务容器，
Control/Http 不直接创建 SQLite Store 或写文件，Core.Settings 实际符号依赖不得形成循环。
精确的异常日志 ILoggerFactory 与组合根保留，编译程序集白名单继续独立执行。
Web 的 TS/Vue AST 门禁见 [前端](frontend.md)，命名和行数检查不替代依赖规则。

插件安装操作摘要按序数排序全部 JSON 对象键，保留数组顺序和完整本地化值，使跨进程读取已完成安装历史时保持同一请求身份；元数据值变化仍产生不同摘要。

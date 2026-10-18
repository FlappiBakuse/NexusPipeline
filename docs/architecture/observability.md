# 通知与可观测性

Notifications 负责运行结果分发，Diagnostics 负责只读诊断；网络出口由 Platform 提供。各服务不得把通知或诊断失败改写为业务成功。

## 通知任务与分发

通知页面独立管理可复用 SMTP/Webhook 任务，配置保存在 `config/notifications.json`（schema 1）。每项有稳定 ID、名称、备注、类型、启用状态和任务级配置；名称唯一且不超过 64 UTF-8 字节，备注不超过 512 字节。新任务默认禁用，启用前检查必要配置。`MaxNotificationTasks` 默认 10，范围 1—99；迁移超过当前限额时保留已有任务，禁止继续新建。

用户绑定、用户全局覆盖、队列与签到只保存 `notificationTaskIds`。空列表不发送；全局同步不覆盖绑定原始选择。删除任务不清空引用，界面标记失效，发送时跳过。队列通知开启时只发送队列汇总，抑制队列内用户通知；关闭时使用用户配置。失败、取消、跳过及空队列沿用同一规则，通知失败不能改写执行结果。

`NotificationTasks` 负责克隆、校验、原子保存后发布内存状态；`NotificationDelivery` 在事件发送阶段冻结目标配置，按 ID 去重，以最多四路并发执行，每个目标有独立超时和取消。不同任务即使地址相同也分别发送，不自动重发。普通 DTO 只返回密钥配置状态，密钥通过 keep/replace/clear 写入，使用 DPAPI 加密。日志不记录请求 URL、认证响应及秘密。

通知编辑器使用单一填写框，密钥以密码框输入；填写新值后显式保存会替换已存值，留空保留原值，取消不写入。已保存的秘密不回填明文，编辑其他字段或删除未保存的新值不会清除原凭据。

脚本通知按每个目标的截图开关附图，队列汇总和签到使用文字。Discord 支持 multipart 附件，企业微信支持图片消息，飞书和 Slack 使用各自上传凭据，Generic 支持模板图片占位符；SMTP 使用 MIME 附件。

`Host/Initialization/NotificationMigration` 协调旧设置、绑定原始值、全局覆盖、队列和插件 scoped data 的迁移；插件负责自身已知 schema 的纯转换。journal 保存来源指纹、原字节、准备后的稳定 ID 和提交状态，中断后核对来源并继续。超过 99、未知格式、损坏数据或缺少必要转换器时阻止通知发送并保留来源，不部分迁移或并行启用旧发送路径。

## 宿主代理设置与网络边界

设置页提供三个代理模式：

| 模式 | 宿主外部 HTTP 行为 |
|---|---|
| `none` | 直接连接 |
| `system` | 使用 Windows `HttpClientHandler` 的系统代理设置 |
| `http` | 使用设置中的 HTTP/HTTPS 代理地址，可附带用户名和 DPAPI 加密密码 |

`OutboundHttpClientProvider` 为每次请求按当前设置创建 client，因此保存代理后新请求立即读取新配置。插件 catalog、插件包、软件更新和 Webhook 统一使用该出口；SMTP、Control API、MCP、本地 loopback 请求和插件子进程保持各自网络行为。localhost、`127.0.0.1` 与 `::1` 始终直连。设置 API 只返回代理密码占位符，密码不会进入界面响应、审计详情或日志。

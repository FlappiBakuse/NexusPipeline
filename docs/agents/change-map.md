# 按任务找事实源、owner 与最小证明

这张表只提供检索路由。它不触发验证；先完成本次选定的全部代码，再把需要的证明合并到统一计划。对应方法名与当前入口以[验证规范](../developer/validation.md)和实际源码为准。

| 任务 | 先读 | 主要代码 owner | 本轮必须守住的结果 | 最小证明方向 |
|---|---|---|---|---|
| 普通重试与无标记退出 | [状态与重试](../developer/state-and-retry.md) | Core.Execution；Storage.Execution 的实际调用/证据入口 | 已授权旧结果与重试恢复，不放宽真实 Unknown/冷恢复 | 独立输入向量＋一条确实消费该规则的执行链 |
| 首次快照、重试输入、同步缩减 | [配置](../developer/configuration.md) | Configuration use case、run preparation/progress、SQL 与文件适配 | 初始来源可验证、0→1→2 接续、原现场和旧快照保全 | 已有配置夹具中合并证明相关变化 |
| 专项修复被 blocking 诊断阻止 | [配置](../developer/configuration.md)与 Plugins 对应专项页 | TaskAdapterService、AdapterUseCases、插件 configuration | 合法修复可完成；运行阻断和写授权分别判断 | 真实提案→预览→应用；保留权限负例 |
| 超过 1 GiB 数据库启动 | [启动与容量](../developer/startup-and-capacity.md) | StateDatabase、DatabaseCopy、owned cleanup | 大库可用且不误删用户对象 | 触达边界的有效大库和相应拥有权证明 |
| 启动阶段进度／无控制台入口 | [启动与容量](../developer/startup-and-capacity.md) | Host lifecycle/StartupProgress、GUI launcher、受影响装配入口 | 进度早且真实，隐藏不取消恢复，CLI 保留 | 同源实际 Windows GUI 与必要自启/打包路径 |
| 历史筛选和账号深链 | [前端](../developer/frontend.md) | Records query、record-window、history-link、账号摘要 | 筛选不漏重，请求有界，run/user 归属稳定 | 同一多账号记录与混合历史夹具 |
| 自动发现、配置编辑与父表单 | [前端](../developer/frontend.md)及配置页 | ScriptSourceFields、AutomationEditorDialog、AdapterEditorPanel | 一次选择发现；非保存动作不保存草稿 | 有效可写父表单中的真实用户动作 |
| 设置分类与公共集合 | [前端](../developer/frontend.md)、[UI ABI](../developer/frontend-abi.md) | Host settings owner、公开元素、Plugins frontend-ui 消费者 | 插件与 Host 同一展开/定位/禁用语义 | 一个宿主分类＋实际插件分类的组合行为 |
| 登录加载与验证会话 | [前端](../developer/frontend.md) | Desktop login、Host bridge、GameCheckIn | 阶段可见、失败可继续、验证后完成、凭据不串 | 合成认证页；必要官方无账号阶段观察 |
| AppStore / Storage 依赖 | [架构](../developer/architecture.md) | 实际领域 owner 与组合根 | 单一事实源、边界缩窄且产品结果不变 | 实际依赖约束＋受影响已有产品用例 |
| 测试裁减与四类职责 | [验证](../developer/validation.md) | 现有 runner/工具入口与产品断言 | 失败可见、零选择可见，移除实现锁定 | 受影响真实选择/执行；不建新的数量注册平台 |
| 文档微调 | [文档导航](../README.md) | 用户任务页、对应领域页、status | 页面准确描述已实现/已验证状态 | 受影响链接与实际命令核对 |

## 跨仓处理

Host 拥有公开契约与能力，Plugins 拥有上游专项事实和插件业务。先确认变动是否真的改变公开契约；内部规则修复不自动要求 SDK bump 或全插件 rebuild。需要同源 SDK 时使用实际生产者回执，不手工复制某个旧 DLL 或前端声明。

上游命令索引、配置键、日志模式必须绑定实际适配器支持的上游版本。不能以“当前 next 测试期望就是这样”替代上游依据。具体专项 task、sourceId 与安全重试规则由 Plugins 文档承担，Host 只保存与消费经验证声明。

## 允许扩大的唯一理由

当前代码的真实依赖传播、失败根因、公开契约变化或目标环境要求导致原定范围不足时，写明因果链并增加最小必要修改/验证。没有这些证据，保持原任务范围。

不要添加旧 Nexus 协议兼容 reader、未知值默认为成功、无消费者接口、跨设备偏好框架、通用缓存/分片系统或第二套恢复 owner。不要为了消除一次报错运行全仓全平台测试，也不以修改断言或静默捕获为修复。

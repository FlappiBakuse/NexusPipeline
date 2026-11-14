# 前端行为、公共组件与页面边界

`apps/web` 是浏览器与 Electron 共用的 Vue 应用；Web 资产由 Host 提供，桌面沿受限原生桥接加载同一界面。本轮保留 next 的技术栈、公共 Custom Elements、SDK 和 typed control 边界，恢复已确认的用户操作语义。

## 必须恢复的主路径

| 用户任务 | 本轮目标 | 仍要保留的保护 |
|---|---|---|
| 选择专项根目录 | 选择后自动发现，用户只处理真实多候选或缺失 | 取消、旧响应失效、实际绑定资源与当前目录一致 |
| 编辑已有账号配置 | 常用入口可见，直接使用已绑定编辑程序和资源 | 原生授权、备份、writer 停止、完成/取消恢复 |
| 探测、查询与修复预览 | 这些动作不提交父用户表单 | 真正保存是明确出口，未知写入仍可核对 |
| 普通操作失败 | 固定或局部反馈，不挤动主要操作控件 | 错误可诊断；只有连接问题提示重连 |
| 账号摘要进入历史 | URL 保持 run、user 及需要的 attempt 身份 | 服务端账号范围与冻结步骤归属一致 |
| 设置分类 | 宿主/插件单一展开、选中置顶、重复关闭、视口稳定 | 插件退役与当前选择正确收敛 |
| 登录 | 加载可见、失败能继续原会话，验证成功后才结束 | 会话隔离、凭据来源、秘密生命周期与取消归属 |

这些是产品目标，当前实现差异在[状态表](../status.md)。新旧截图中的任务数差异没有同一配置输入证明时，不作为更改任务模型的依据。

## 表单与公共动作

非保存按钮显式拥有非提交语义，原生按钮设置 `type="button"`；真实保存使用唯一明确提交路径。不能依赖某个点击处理器恰好设置 busy 来阻止 HTML 默认提交。

公共按钮、字段和动作集合统一 busy、disabled、键盘、焦点、校验与提交语义；Vue 封装只适配模型和事件，不维护第二份控件状态。先复用现有元素，只补当前真实消费者缺少的能力，不新造万能表单框架。

公共化按行为判断：选择/预览不能保存父草稿；禁用不能触发命令；取消保持或丢弃哪份草稿必须明确；未知写入不能自动重发。颜色相同、标签名相同或存在 components 目录都不构成完成证明。

证据：[真实父表单](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/apps/web/src/pages/AutomationEditorDialog.vue#L106-L165)、[缺省按钮路径](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/apps/web/src/pages/AdapterEditorPanel.vue#L120-L287)。

## 公共集合与设置 owner

开关行优先使用已有 `nexus-switch-list`，避免插件和 Host 各自覆盖子控件私有边框。动作组、折叠卡和设置字段复用既有公开集合，只为真实交互补少量必要契约。

宿主和插件设置分类由同一个 owner 管理 id、标题、排序、展开、定位与可访问状态。插件通过现有扩展挂载边界贡献内容，不在 Host 写死壁纸分支，也不各自维护一套 details 展开状态。确需 SDK 元数据变化时按实际消费者同步，不先发明尚未实现的公共 API。

本地展开立即响应。已有偏好持久化可沿原能力处理，但失败不能阻塞阅读；它是界面偏好文件写入，不能误记为每次点击都写 SQLite。不为尚无需求的跨设备同步新增状态协议。

证据：[已有 SwitchList](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/apps/web/src/ui/structure-elements.ts#L211-L274)、[当前设置选择](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/apps/web/src/pages/settings-view-state.ts#L51-L118)。

## 草稿与窄接口

App shell 负责身份、权限、连接和导航。Automation 拥有类型明确的脚本/账号/队列草稿与具名命令；Records 拥有查询和深链；Logs/Media 拥有共享请求与取消；Extensions 拥有插件挂载 scope。

页面只取得所属任务需要的接口。不要让公共组件接受整个 AppStore 或反向导入 pages 私有类型；也不要为降低引用计数建立一组无职责的转发接口。类型引用过宽是能力设计问题，不能直接说成运行时 bundle 膨胀。

概览布局、活动设置、宿主设置与自定义壁纸采用即时提交，不注册编辑草稿。布局和活动弹窗仅保留本次打开的撤销基线，取消提交还原；保存、关闭和 Escape 保持已应用值。设置写入归应用或插件激活期 owner，页面卸载不取消已受理命令；未知结果按原操作只读核对。

普通草稿、秘密和未知写入身份分别保管。未编辑的表单关闭后释放，重新进入读取当前已保存值；关闭、切页及隐藏保留实际修改的草稿，显式取消清理当前目标及其子草稿；成功保存结清本次提交。离页取消读请求，已发送写入继续归原操作 owner；迟到结果保持原账号和原操作归属。秘密输入保存在应用级专用内存中，重开默认遮罩，不进普通 drafts、持久浏览器存储或日志。完整退出、重载和模式切换结束界面会话，不弹出草稿确认。

所有草稿编辑入口在恢复时显示就地提醒；仅恢复草稿后，已有对象中变化的输入与开关以黄色标记，改回原值清除。首次打开与新建表单不显示黄色字段状态。字段展示基线按目标保留在内存，成功保存更新，普通快照刷新不替换；秘密字段只保留修改状态。弹窗页脚动作使用相同的最小 80px 宽度。

现有共享 EventFeed、LiveLogs、MediaPool 与可见性清理继续复用。派生 lookup 按权威修订更新，减少逐行全数组查找；它不是第二份实体库。仅当代表性数据证明渲染是瓶颈时才给实际列表加窗口，不全站重写虚拟化。

证据：[AppStore 能力](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/apps/web/src/app/store.ts#L17-L71)。

## 历史与登录的完整身份

账号历史链接由共用构造入口接受账号上下文；服务端 summary/detail 和日志图片继续按冻结步骤过滤。整体运行入口才允许不带账号。查询筛选下推规则见[启动与容量](startup-and-capacity.md)。

登录窗口先提供可信加载反馈，再加载获准来源；候选凭据需后端验证成功才完成。加载合法认证 iframe 的许可与读取凭据的来源范围分开，不能为解决白屏取消全部来源限制。先按 CDP、导航、frame、网络和取消阶段观察，再修实际原因。

当前已核实初始官方 URL 没变；不能把截图白屏直接写成地址错误或普通 30 秒请求超时。受控 loopback 登录 fixture 也不能代替真实官方页面的网络观测。

证据：[账号链接](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/apps/web/src/pages/AutomationRows.vue#L89-L94)、[历史恢复](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/apps/web/src/pages/history-link.ts#L76-L110)、[登录窗口](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/apps/desktop/login-window.cjs#L19-L100)。

现役状态与只读投影细节见[Web 状态参考](frontend-state.md)。

## 最小验证

全部选定代码完成后，合并验证真实父表单非保存操作、账号多结果深链、宿主/插件设置切换，以及实际改动的控件交互。删除固定 DOM 个数、私有类名、像素与对象身份的实现断言；保留焦点、草稿、最后数据可达、权限和未知写入。

公共 ABI 确实改变才扩到受影响插件消费者；纯页面排版不运行全部插件或全部原生恢复。命令见[验证规范](validation.md)，精确 UI ABI 见[公开元素](frontend-abi.md)。

# 前端公共元素与扩展表面

`contracts/ui-elements.json` 引用 40 个独立元素定义，生成 SDK 的属性、事件与运行时元数据。
结构化值只通过 property 传递；程序设置不派发用户 change。
公开名称使用 `nexus-`，不注册旧元素别名。

当前 Native Custom Elements 覆盖台账中全部40项输入、操作、结构与反馈元素。
它们共享主题变量，通过标准 slot 保留消费者节点；交互元素的 `focus()` 进入实际控件。
密码 value 只驻留 property 和表单输入，不反射为 attribute 或表单恢复状态。
宿主 Vue 通过 property、事件、slot和显式可见性消费同一实现；应用归属见 [Web 前端](frontend.md)。
数字控件在标准 `aria-invalid` 上公开无效状态，保留未通过校验的原始文本；有效修正才更新 value 与 change。
图标按钮将标准 `aria-pressed` 同步至内部按钮，图标 slot 不拦截指针操作。
模态使用显式视口宽度及自绘样式，移动端操作按钮为44px，输入框与步进按钮按各自尺寸呈现。
`--nx-modal-title-size` 与 `--nx-modal-title-line-height` 用于具体表面的标题层级，`--nx-action-height` 只控制操作按钮。
`--nx-button-fill` 调整中性操作按钮的表面，`--nx-drag-size` 调整拖拽把手的触控区域。
`--nx-button-padding`、`--nx-button-radius` 与输入字体、内边距变量用于具体旧版表面，不改变元素属性或事件合同。
文件选择器显示操作文字及文件名，标准必填文本同时参与表单有效性；选择器忽略锚点未移动的延迟滚动事件。
模态将遮罩淡入和面板缩放分开计时；面板退出完成后才释放原生顶层，隐藏或卸载则立即释放。
导航扩展拥有完整页头与正文；宿主保留隐藏的核心页头目标供已访问核心页面使用，避免重复显示标题。

插件中心在720px以下以列表/详情两种视图呈现。已验证的安装详情同时返回安装修订与原归档摘要，
用于区别 develop 的同版本不同内容更新；稳定通道不把同版本归档差异作为升级。
发行 Markdown 由 [Marked](https://marked.js.org/) 编译，再由 [DOMPurify](https://github.com/cure53/DOMPurify) 清理；
正文不能注入表单、脚本、样式或宿主标识，外部链接仅允许 HTTP/HTTPS。
安装与设置命令在发送前保留完整原始正文；未知结果只读取原操作，不自动重放安装或运行激活。

SDK 的 `ExtensionScope` 拥有资源及子 scope；先 abort，再逆序释放，晚到 cleanup 立即执行。
前端注册表在 activate 完成前暂存 surface、route 和样式，成功后一并公布。
失败或停止时撤销所有注册及样式，并释放已挂载表面的子 scope。
表面先在断开的独立容器准备；取消后晚到代码只能持有已撤离的容器。
注册表是受信扩展的生命周期边界，不是浏览器脚本安全沙箱。
激活上下文的 `locale` 返回当前语言，`onLocaleChanged` 在原表面拥有期内通知语言变化；导航标签可使用 getter。
语言切换只更新文案，保留普通输入节点、原始无效文本和焦点。公共元素同样观察文档语言，秘密仍按可见性及会话拥有权清理。

`tests/frontend` 的原生浏览器 fixture 覆盖属性、事件、slot、主题、焦点、输入法、弹窗及整体回滚。
测试通过公共 SDK 消费同一组件源码，运行结果与完整产品集成证据分开登记。
测试驱动使用 [Electron 输入接口](https://www.electronjs.org/docs/latest/api/web-contents) 与
[Chromium Input 协议](https://chromedevtools.github.io/devtools-protocol/tot/Input/)，不把合成 DOM 键盘事件当作实际输入法证据。

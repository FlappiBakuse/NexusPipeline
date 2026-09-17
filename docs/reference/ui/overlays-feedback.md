# 弹层与反馈

本页定义 menu、tooltip、modal、dialog popover、确认对话框、toast、spinner、加载和空状态。弹层组件负责其内部键盘与焦点生命周期；调用方负责 `open` 状态和业务结果。

## 菜单与提示

| 元件 | 属性（类型；默认值） | 事件 | slot 与生命周期 |
|---|---|---|---|
| `nxp-menu` | `open` boolean；`false`；`label` string；`菜单`；`disabled` boolean；`false` | `update:open`: `[boolean]`；`close`: `[]` | `trigger` slot 替换触发按钮文案；默认 slot 是 menu 内容。打开时发出跨实例协调事件；Escape、菜单内容点击关闭并恢复触发器焦点，外部点击更新 `open=false`。 |
| `nxp-tooltip` | `text` string；空；`placement`: `top\|right\|bottom\|left`；`top` | 无 | 默认 slot 是触发内容；文本非空时显示 `role=tooltip`。通过 hover 或 focus-within 显示，不承担点击操作。 |

`nxp-menu` 的默认 slot 内容应提供可操作的按钮或链接。菜单项的选择事件由调用方元素发出，菜单只负责关闭与焦点恢复。

## Modal 与 Popover

| 元件 | 属性（类型；默认值） | 事件 | slot 与行为 |
|---|---|---|---|
| `nxp-modal` | `open` boolean；`false`；`title`/`ariaLabel` string；空；`closeLabel` string；`Close`；`closeable` boolean；`true`；`locked` boolean；`false`；`size`: `default\|wide`；`default`；`surface`: `default\|secondary`；`default`；`footer` boolean；`false`；`panelClass`/`bodyClass` string；空 | `close`: `[]` | `header`、默认 body、`footer` slot。打开时聚焦首个可聚焦元素，关闭时归还打开前焦点；Escape 和可关闭背景点击触发 close，`locked=true` 时不允许关闭。 |
| `nxp-dialog-popover` | `open` boolean；`false`；`id` string；未提供；`ariaLabel`/`closeLabel` string；空；`closeable` boolean；`true`；`labelledby` string；空 | `close`: `[]` | 默认 slot 是浮层内容。它使用 `role=dialog`；打开时聚焦内容，关闭/卸载时归还原焦点。定位由调用方提供。 |
| `nxp-confirm-dialog` | `open` boolean；`false`；`title` string，必填；`message` string；空；`confirmLabel`/`cancelLabel` string；`Confirm`/`Cancel`；`confirmTone`: `primary\|danger`；`primary`；`busy` boolean；`false` | `close`、`cancel`、`confirm`: `[]` | 默认 slot 是补充内容。`busy=true` 时锁定 modal、隐藏关闭路径并阻止 cancel；confirm 事件由调用方决定异步提交。 |

Modal 默认 body 使用 `nxp-scroll-area`，调用方可以通过 `bodyClass` 指定自己的语义 class；该 class 只作为当前内容布局钩子，不应依赖内部滚动器的私有节点。

## 反馈状态

| 元件 | 属性（类型；默认值） | 事件与 slot | 用户可观察行为 |
|---|---|---|---|
| `nxp-toast` | `message` string；空；`tone`: `info\|success\|warning\|danger`；`info`；`visible` boolean；`true` | 无自定义事件；默认 slot 覆盖 message | message 为空或 visible=false 时不渲染；可见内容使用 `role=status` 与 polite live region。 |
| `nxp-spinner` | `label` string；空 | 无；无 slot | 使用 status 语义；label 为空时使用宿主 loading 文案。 |
| `nxp-loading-state` | `title` string，必填；`description` string；空；`ariaLabel` string；空；`testId` string；`plugin-loading-state` | 无；无 slot | 输出 status/live region 和 progressbar；`ariaLabel` 为空时使用 title。 |
| `nxp-empty-state` | `title` string；`暂无内容`；`description` string；空；`linkHref`/`linkLabel` string；空；`tone`: `muted\|danger`；`muted` | 无；`title` 与默认说明 slot | title 始终可见；提供 linkHref 才显示返回/行动链接；danger 只改变状态语义外观。 |

## 焦点与卸载规则

- 调用方在 `open` 变化后保留组件实例，组件负责打开时捕获焦点、关闭时恢复焦点；卸载时同样清理事件监听和浮层定位。
- 同一页面的 menu、select、time picker 和日期/对话浮层通过公开的 overlay 协调行为避免同时展开；业务代码不直接派发内部协调事件。
- `close` 只表示组件明确报告的关闭请求；异步业务是否成功、是否继续保持 open 由调用方决定。
- `locked` 或 `busy` 用于不可中断的事务窗口，调用方仍应在完成或失败后更新状态。

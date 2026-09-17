# 公共 UI 元件参考

本文件负责宿主注册的 `nxp-*` 元件目录和选型路由。props、事件和 slot 以实际 SFC 与契约测试为准；插件消费边界见 [Frontend API](../plugin-api/frontend.md)，页面结构和领域数据不进入公共 UI。

当前注册表位于 `frontend/src/ui/register.ts`，包含 38 个元素：

| 职责 | 元件 |
|---|---|
| 动作与显示 | `nxp-button`、`nxp-icon-button`、`nxp-icon`、`nxp-badge`、`nxp-card` |
| 字段与输入 | `nxp-field`、`nxp-text-input`、`nxp-text-area`、`nxp-number-input`、`nxp-switch`、`nxp-select`、`nxp-range` |
| 专用输入 | `nxp-file-picker`、`nxp-path-picker`、`nxp-color-picker`、`nxp-time-picker` |
| 弹层与反馈 | `nxp-menu`、`nxp-tooltip`、`nxp-modal`、`nxp-toast`、`nxp-confirm-dialog`、`nxp-dialog-popover` |
| 状态与导航 | `nxp-spinner`、`nxp-empty-state`、`nxp-pager`、`nxp-scroll-area`、`nxp-tabs`、`nxp-page-header` |
| 复合元件 | `nxp-switch-setting`、`nxp-switch-list`、`nxp-loading-state`、`nxp-section-card`、`nxp-collapsible-card`、`nxp-action-group`、`nxp-date-range-picker`、`nxp-drag-handle`、`nxp-entity-row`、`nxp-sortable-list` |

## 选型

- 普通动作使用 `nxp-button`，图标动作使用 `nxp-icon-button`，异步动作通过 `busy` 和 `disabled` 表达状态。
- 标题、帮助和错误使用 `nxp-field`；输入值保持受控，标签必须关联真实值载体。
- 列表行使用 `nxp-entity-row` slots；排序使用 `nxp-sortable-list` 配合 `nxp-drag-handle`，组件只发出完整的新 key 顺序。
- 日期范围使用 `nxp-date-range-picker`；自然日字符串由调用方保存和转换，组件不拼业务 URL。
- 弹层、菜单、分页、滚动和状态面优先复用现有元件，保持 Escape、焦点恢复、键盘和 ARIA 行为。

公共元件允许在内部实现中使用原生 HTML 控件。业务页面必须通过公开元件表达通用动作与字段；隐藏 input、原生文件对象选择及领域专属图表交互按架构扫描器的精确例外规则处理。

## 复合元件契约

Custom Element 事件的参数按顺序位于 `CustomEvent.detail` 数组；对象和布尔属性通过 DOM property 或 Vue 绑定赋值。

- `nxp-sortable-list`：`axis` 为 `y` 或 `both`，`disabled` 禁用排序；直接列表项必须有唯一、非空 `data-dnd-id`，并通过 `nxp-drag-handle` 发起操作。`reorder` 参数为完整 key 数组和移动项 key；调用方更新数据后呈现新顺序。Escape、pointercancel、外部列表变化与卸载均取消当前操作；键盘方向键移动并恢复手柄焦点。
- `nxp-date-range-picker`：受控属性为 `open`、`from`、`to`，日期使用 `YYYY-MM-DD`；`maxDate` 限制日期上界。`open`、`close` 通知可见状态变化；`apply` 返回 `{ from, to }`。关闭会丢弃未提交草稿，显示调用方最后提交的范围。
- `nxp-entity-row`：`itemId` 提供排序标识，`as` 可选 `div`、`article`、`li`；默认内容为行主体，`leading`、`content`、`meta`、`actions` 提供具名区域；页面负责实体业务和操作结果。
- `nxp-action-group`：默认插槽承载动作，布局属性由组件定义；每个动作通过公共按钮表达禁用和忙碌状态。
- `nxp-field` 与 `nxp-page-header` 的具名插槽在公共元素与宿主 Vue 组件两种使用方式下保持一致；嵌套公共元素独立消费自己的插槽。

验证：`node tests/run.mjs frontend --group ui`、`node tests/run.mjs frontend --group bridge`、`node tests/run.mjs contract`，以及 [前端边界检查](../../architecture/frontend.md)。

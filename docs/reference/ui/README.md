# 公共 UI 元件参考

宿主在 `frontend/src/ui/register.ts` 注册公开的 `nxp-*` Native Custom Elements。注册表是元件集合的唯一来源；下面的分组页维护可直接消费的属性、事件、slot、值语义和生命周期契约。插件消费边界见[Frontend API](../plugin-api/frontend.md)，页面结构和领域数据不进入公共 UI。

## 按职责阅读

| 分组 | 覆盖范围 | 入口 |
|---|---|---|
| 动作与输入 | 按钮、图标、字段、文本/数字、文件和路径 | [actions-inputs.md](actions-inputs.md) |
| 选择与日期 | 开关、单/多选、范围、颜色、时间、日期范围和 tabs | [selection.md](selection.md) |
| 弹层与反馈 | menu、tooltip、modal、popover、确认、toast、加载和空状态 | [overlays-feedback.md](overlays-feedback.md) |
| 布局与列表 | card、标题、分组、行、排序、分页和滚动 | [layout-lists.md](layout-lists.md) |

## 选型原则

- 普通动作使用 `nxp-button`，图标动作使用 `nxp-icon-button`；异步动作通过 `busy` 与 `disabled` 表达，组件负责阻止重复点击。
- 输入值使用受控 `modelValue`；调用方消费 `update:modelValue` 和 `change`，并为真实输入提供可访问名称。
- 日期范围使用 `nxp-date-range-picker`，日期格式为 `YYYY-MM-DD`；组件维护未提交草稿，调用方在 `apply` 后保存范围。
- 列表排序使用 `nxp-sortable-list` 配合 `nxp-drag-handle`；调用方保存完整的新 key 顺序并处理持久化错误。
- 弹层、菜单、分页、滚动和状态面优先复用公共元件；Escape、焦点恢复、键盘和 ARIA 行为由元件负责。

公共元件内部可以使用原生 HTML 控件。业务页面和插件使用公开元件表达通用动作与字段；隐藏 input、原生文件对象选择以及领域专属交互仅在边界扫描器登记的精确例外内出现。

Custom Element 事件的参数按声明顺序位于 `CustomEvent.detail` 数组；对象、数组和布尔属性通过 DOM property 或 Vue 绑定赋值。每个分组页的示例均给出 Vue 用法与事件消费方式。

测试入口统一见 [testing/README.md](../../testing/README.md)。公共 UI 的组件契约由 `frontend/src/ui` 的测试维护，插件装配由 Frontend API/contract 测试维护；前端分层和扫描边界见[前端架构](../../architecture/frontend.md)。

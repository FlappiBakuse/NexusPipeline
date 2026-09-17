# 布局与列表

本页定义卡片、页面标题、分组、实体行、排序、分页、滚动和列表状态。布局元件拥有结构与可访问语义；领域页面负责数据、请求、排序保存和错误呈现。

## Surface 与页面结构

| 元件 | 属性（类型；默认值） | slot | 说明 |
|---|---|---|---|
| `nxp-card` | `tone`: `default\|secondary`；`default`；`as`: `section\|article`；`section`；`unstyled` boolean；`false` | 默认内容 | 通用 surface；`unstyled=true` 保留语义元素并移除组件默认 surface 样式。 |
| `nxp-section-card` | `title`/`description` string；空；`variant`: `primary\|secondary`；`primary` | `header`、`description`、`actions`、默认 body | 有标题、说明或 actions 时生成 header；默认内容进入 body。 |
| `nxp-collapsible-card` | `title`/`description` string；空；`expanded` boolean；`false`；`panelId`/`controlsId`/`panel` string；空；`surface`: `default\|secondary`；`default` | `header`、`actions`、默认 body | `expanded` 受控；标题按钮包含 `aria-expanded`/`aria-controls`，点击后发出 toggle。 |
| `nxp-page-header` | `eyebrow`/`title`/`description` string；空 | `eyebrow`、`title`、`description`、`actions` | 统一页面标题与右侧操作区域；actions slot 由调用方放置按钮。 |
| `nxp-action-group` | `direction`: `row\|column`；`row`；`align`: `start\|center\|end\|stretch`；`center`；`justify`: `start\|center\|end\|between`；`end` | 默认动作内容 | 只负责布局；动作状态由子按钮表达。 |

`nxp-collapsible-card` 的 `toggle` 事件 detail 为 `[boolean]`，调用方应将新值写回 `expanded`。其余 surface 元件没有自定义事件。

## 列表行与拖拽

| 元件 | 属性（类型；默认值） | 事件 | slot 与身份 |
|---|---|---|---|
| `nxp-entity-row` | `itemId` string；空；`as`: `div\|article\|li`；`div`；`slotLayout`: `normal\|contents`；`normal` | 无 | `leading`、`content`、`meta`、`actions`；无 content slot 时默认 slot作为 content。`itemId` 非空时映射为排序所需的 `data-dnd-id`；`contents` 只改变 leading/actions wrapper 的布局，不改变 slot 责任。 |
| `nxp-drag-handle` | `label` string；`Reorder`；`title` string；空；`disabled` boolean；`false`；`tabIndex` number；`0`；`ariaHidden` boolean；`false` | 无自定义事件；点击被组件消费 | 无 slot。它是排序手柄的键盘/可访问入口；实际重排由 `nxp-sortable-list` 协调。 |
| `nxp-sortable-list` | `axis`: `y\|both`；`y`；`disabled` boolean；`false`；`tag` string；`div`；`transitionName` string；空；`canDrag` 谓词；未提供 | `reorder`: `[string[], string]`，完整 ids 与 movedId | 默认 slot 必须是直接含唯一、非空 `data-dnd-id` 的列表项。Escape、pointercancel、外部列表变化和卸载取消当前操作；调用方收到完整顺序后保存并更新列表。 |

### 排序示例

```vue
<nxp-sortable-list @reorder="(ids, movedId) => saveOrder(ids, movedId)">
  <nxp-entity-row v-for="item in items" :key="item.id" :item-id="item.id">
    <template #content>{{ item.name }}</template>
    <template #actions><nxp-drag-handle :label="`移动 ${item.name}`" /></template>
  </nxp-entity-row>
</nxp-sortable-list>
```

Vue 模板中事件参数由组件声明展开为两个参数；在 Custom Element 中使用 `event.detail[0]` 取得完整 id 数组，`event.detail[1]` 取得移动项。

## 分页与滚动

| 元件 | 属性（类型；默认值） | 事件 detail | 行为 |
|---|---|---|---|
| `nxp-pager` | `page` number；`1`；`totalPages` number；`1`；`total` number；`0`；`pageSize` number；`20`；`label` string；`分页`；`previousLabel`/`nextLabel` string；空 | `update:page`、`pageChange`: `[number]` | 只有 totalPages>1 时显示；目标页限制在 1..totalPages；当前页再次选择不发事件；页面范围说明使用 live region。 |
| `nxp-scroll-area` | `direction`: `vertical\|horizontal\|both`；`vertical`；`ariaLabel` string；空；`viewportClass` string；空 | 无 | 默认 slot 放滚动内容；公开 `viewportClass` 只用于调用方需要的语义布局钩子。组件管理滚动条、拖拽、键盘和卸载监听。 |

## 布局契约

- 列表项的业务身份由调用方提供，组件不会根据索引或展示文案生成稳定 key。
- `reorder` 事件只描述用户操作，网络保存、失败回滚、权限提示和重新加载由领域页面负责。
- 公开元素的样式使用 design tokens、属性 variant 和明确的调用方 class；内部节点 class 不作为插件跨版本契约。
- 触控目标、焦点顺序和 ARIA 状态由组件保持；页面不得用装饰性 DOM 结构替代可访问名称或状态。

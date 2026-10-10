# 动作与输入

本页定义按钮、图标、字段和路径输入的公开契约。输入类元件均采用受控值：调用方保存 `update:modelValue` 的值，并在需要提交或校验时处理 `change`。Custom Element 消费者收到的自定义事件参数按表中顺序放在 `event.detail` 数组中。

## 动作与显示

| 元件 | 属性（类型；默认值） | 事件 | slot 与行为 |
|---|---|---|---|
| `nxp-button` | `tone`: `default\|primary\|success\|warning\|danger`；`default`；`variant`: `solid\|ghost`，`solid`；`size`: `sm\|md`，`md`；`type`: `button\|submit\|reset`，`button`；`disabled`: boolean，`false`；`busy`: boolean，`false`；`label`: string，空；`wrap`: boolean，`false` | 无自定义事件；使用原生 `click` | 默认 slot 覆盖 `label`。`busy` 同时设置忙碌语义并阻止点击，`disabled` 阻止点击。`wrap` 让纯文本文案正常换行。 |
| `nxp-icon-button` | `label`: string，必填；`pressed`: boolean，`false`；`expanded`: boolean，可省略；`disabled`: boolean，`false`；`type`: `button\|submit\|reset`，`button` | 无自定义事件；使用原生 `click` | 默认 slot 放图标。`label` 同时作为 `aria-label`；`pressed`/`expanded` 映射到对应 ARIA 状态。 |
| `nxp-icon` | `name`: string，必填；`label`: string，空；`className`: string，空 | 无 | 无 slot。未知 `name` 使用默认图标；有 `label` 时作为可访问名称，否则隐藏于辅助技术。 |
| `nxp-badge` | `tone`: `muted\|blue\|ok\|warn\|bad`，`muted`；`label`: string，空；`wrap`: boolean，`false` | 无 | 默认 slot 覆盖 `label`；用于状态或分类展示，不承载操作。`wrap` 让文本正常换行并由内容决定高度。 |

## 字段容器

`nxp-overflow-text` 接受 `label: string`（默认为空）与 `wrap: boolean`（默认 `false`），默认文本 slot 覆盖 `label`。容器宽度由调用方布局约束，溢出时保持单行省略，悬停或键盘聚焦时往返滚动，离开后复位；完整文本保留在可访问内容和原生提示中。组件跟踪内容、字体及尺寸变化，在卸载时释放观察器。位于按钮或链接内时沿用该控件的焦点；启用减少动态效果时不播放滚动动画。`wrap=true` 时正常换行，不启用省略、滚动或额外焦点。

`nxp-badge` 和纯文本 `nxp-button` 使用相同的溢出行为，可通过 `wrap=true` 为详情区域选择正常换行。复杂按钮 slot 中的紧凑文字应显式使用 `nxp-overflow-text`；卡片正文、诊断详情和日志直接使用可换行文本。

`nxp-field` 用一个真实 `<label>` 包住字段和说明。`for` 应指向内部输入的 `id`；嵌套的公开输入仍由它自己负责值和事件。

| 属性 | 类型；默认值 | 语义 |
|---|---|---|
| `label` | string；空 | 默认标签文本 |
| `help` | string；空 | 作为字段提示数据；复杂提示使用 `help` slot |
| `description` | string；空 | 字段说明 |
| `error` | string；空 | 错误文本，渲染为 `role=alert`、`aria-live=polite` |
| `required` | boolean；`false` | 在标签后显示必填标记 |
| `for` | string；空 | 传给 `<label for>` |

slot：`label`、`help`、默认字段内容、`description`、`error`。字段容器不发自定义事件。

## 文本与数字

| 元件 | 属性（类型；默认值） | 事件 detail | 边界 |
|---|---|---|---|
| `nxp-text-input` | `id` string；未提供；`modelValue` string；`""`；`placeholder` string；`""`；`disabled` boolean；`false`；`type` string；`text`；`maxlength` number/string；未提供；`readonly` boolean；`false`；`autocomplete` string；未提供；`ariaLabel` string；未提供；`showPasswordToggle` boolean；`false`；`showPasswordLabel`/`hidePasswordLabel` string；英文默认文案；`passwordVisibilityControlled`/`passwordVisible`/`secretConfigured` boolean；`false` | `update:modelValue`: `[string]`；`change`: `[string]`；`password-visibility-request`: `[boolean]` | 输入和 change 均返回当前字符串。密码眼睛在 `type=password`、启用切换且未禁用、有值或 `secretConfigured` 时出现；切换保留焦点。受控模式仅请求显示状态，不自行改为明文。 |
| `nxp-text-area` | `id` string；自动生成；`modelValue` string；`""`；`placeholder` string；`""`；`disabled` boolean；`false`；`rows` number；`4`；`maxlength` number/string；未提供；`readonly` boolean；`false`；`ariaLabel` string；未提供 | `update:modelValue`: `[string]`；`change`: `[string]` | 返回当前字符串；内部滚动条随内容更新，调用方不依赖其内部 DOM。 |
| `nxp-number-input` | `id` string；空；`modelValue` number/string；空字符串；`min`/`max` number；未提供；`step` number；`1`；`placeholder`/`help`/`ariaLabel` string；空；`disabled` boolean；`false` | `update:modelValue`、`change`: `[number\|string]` | 空文本返回 `""`，其他文本转换为 number。加减按钮按 `min`/`max` 截断并按 `step` 发出；手工文本输入保留调用方的解析责任。 |

需要授权才能读取的秘密使用 `passwordVisibilityControlled=true`：未读取时 `modelValue=""`、`secretConfigured=true`，不能填充假遮罩字符。处理 `password-visibility-request` 后，由调用方确认、请求专用接口并设置 `passwordVisible`；取消确认时仍为空且隐藏。`readonly` 可以保护未回读字段，同时保留眼睛入口。隐藏时调用方负责清除自动来源明文；默认非受控模式继续在元件内部切换已输入值。

## 文件与路径

| 元件 | 属性（类型；默认值） | 事件 detail | 使用约束 |
|---|---|---|---|
| `nxp-file-picker` | `accept` string；空；`multiple` boolean；`false`；`disabled` boolean；`false`；`label` string；`选择文件` | `change`: `[File[]]` | 选择取消时发出空数组；原生 file input 隐藏在组件内部，调用方只消费文件数组。 |
| `nxp-path-picker` | `id`/`modelValue`/`placeholder`/`help` string；空；`disabled`/`browseDisabled` boolean；`false`；`browseDisabledReason` string；空；`ariaLabel` string；`路径`；`kind`: `file\|folder\|file-or-folder`；`file`；`filter` string；空 | `update:modelValue`、`change`: `[string]`；`browse`: `["file"\|"folder"]` | 文本编辑立即发出值。`browse` 只表达用户应打开的选择器类型；宿主负责实际原生选择和回填 `modelValue`。`file-or-folder` 会显示两个 browse 动作。`browseDisabled` 仅禁用选择按钮，不阻止手工输入；原因只用于按钮提示和辅助技术，不占用字段布局。 |

### 受控输入示例

```vue
<nxp-field label="名称" for="script-name">
  <nxp-text-input
    id="script-name"
    :model-value="name"
    @update:model-value="name = $event"
    @change="validateName"
  />
</nxp-field>
<nxp-button :busy="saving" :disabled="!name" @click="save">保存</nxp-button>
```

在 Custom Element 代码中使用 `event.detail[0]` 读取 `update:modelValue`、`change` 或 `browse` 的第一个参数；原生 `click` 仍按 DOM 事件处理。

## 样式边界

调用方可以使用宿主 design tokens 和元素自身公开的 tone/variant/size 属性。徽标还支持 `--nx-badge-min-height`、`--nx-badge-padding`、`--nx-badge-border` 和 `--nx-badge-font-size` 作为调用方布局变量。`.nxp-*` 内部节点、弹窗布局和输入包装器不属于跨插件样式 API。

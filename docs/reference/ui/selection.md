# 选择与日期

本页定义开关、选项、数值范围、颜色、时间、日期范围和 tabs 的值语义。所有 `modelValue` 都由调用方持有；事件的 Custom Element `detail` 是按顺序排列的参数数组。

## 开关与选项

| 元件 | 属性（类型；默认值） | 事件 detail | 值与键盘语义 |
|---|---|---|---|
| `nxp-switch` | `id` string；空；`modelValue` boolean；`false`；`disabled` boolean；`false`；`label`/`ariaLabel` string；空；`semanticRole`: `switch\|button`；`button` | `update:modelValue`、`change`: `[boolean]` | 点击在 `true`/`false` 间切换；禁用时不发事件。`semanticRole=switch` 使用 `aria-checked`，默认使用按钮的 pressed 语义。`false` 必须作为有效关闭值传入。 |
| `nxp-select` | `id` string；空；`modelValue` string/string[]；空字符串；`options` `NxpOption[]`；空数组；`multiple` boolean；`false`；`disabled` boolean；`false`；`ariaLabel`/`placeholder` string；空 | `update:modelValue`、`change`: 单选 `[string]`，多选 `[string[]]` | 选项 `{ value: string, label: string, disabled?: boolean, title?: string }`。单选未选中返回 `""`；多选返回字符串数组。禁用 option 不可选；Escape、点击外部或打开另一个浮层会关闭并恢复触发器焦点。 |
| `nxp-tabs` | `modelValue` string，必填；`tabs` `NxpTab[]`，必填；`ariaLabel` string；空；`testId` string；未提供 | `update:modelValue`、`change`: `[string]` | `NxpTab` 为 `{ value: string, label: string, testId?: string, disabled?: boolean }`。选择禁用项或当前项不发事件；调用方更新 `modelValue`。 |

`nxp-select` 的 `options` 使用字符串 `value`；数字值由调用方在传入前规范化。隐藏表单值是组件内部实现，业务代码消费受控事件。

## 数值、颜色与时间

| 元件 | 属性（类型；默认值） | 事件 detail | 规范化与边界 |
|---|---|---|---|
| `nxp-range` | `modelValue` number/string；`0`；`min` number；`0`；`max` number；`100`；`step` number；`1`；`disabled` boolean；`false`；`ariaLabel` string；空 | `update:modelValue`、`change`: `[number]` | 原生 range 的当前值转为 number；范围和步长由原生控件执行。`0` 是有效值。 |
| `nxp-color-picker` | `modelValue` string；`#76a7ff`；`disabled` boolean；`false`；`ariaLabel` string；`颜色` | `update:modelValue`、`change`: `[string]` | 颜色输入返回文本值；可识别的 3/6 位十六进制会用于颜色预览，非法文本的预览回退为黑色，原始文本仍由调用方收到。 |
| `nxp-time-picker` | `id` string；空；`modelValue` string；空；`disabled` boolean；`false`；`ariaLabel` string；`时间` | `update:modelValue`、`change`: `[string]` | 选择器以 `HH:mm` 文本提交；面板内时/分变化立即提交。Escape、外部点击或打开另一个浮层会关闭；禁用时不打开。 |

## 日期范围

`nxp-date-range-picker` 的 `open`、`from` 和 `to` 是必填受控属性，日期使用自然日字符串 `YYYY-MM-DD`。它在打开时从最后提交的 `from`/`to` 创建草稿；点击日期只改变草稿，点击 Apply 才发出结果。

| 属性 | 类型；默认值 | 说明 |
|---|---|---|
| `open` | boolean；必填 | 显示或隐藏日期面板；关闭时丢弃未提交草稿 |
| `from` / `to` | string；必填 | 最后提交的范围；允许空字符串表示尚未选择 |
| `maxDate` | string；空 | 可选的最大日期；为空时使用当前自然日 |
| `locale` | string；空 | 日期月份和范围显示语言；宿主调用方应传入当前规范化 locale |
| `popoverClass` | string；空 | 由调用方追加到日期浮层根元素的公开样式钩子 |
| `displayId` / `displayTestId` / `popoverId` / `testId` | string；空 | DOM 标识或测试标识 |
| `dialogLabel` | string；`Choose date range` | 日期浮层的可访问名称 |
| `toLabel` / `startLabel` / `endLabel` / `applyLabel` | string；`to`、`Start`、`End`、`Apply` | 可见文案 |
| `previousMonthLabel` / `nextMonthLabel` | string；英文默认文案 | 月份导航可访问名称 |
| `dateHelp` | string；`Select a start and end date.` | 底部辅助说明 |
| `weekdays` | string[]；Sun–Sat | 按星期日到星期六提供七个表头 |

事件：`open` 与 `close` 无参数；`apply` 的 detail 为 `[{ from: string, to: string }]`。只有完整、顺序正确且不超过有效最大日期的范围会触发 `apply`，成功后同时触发 `close`。

```js
function onApply(event) {
  const [{ from, to }] = event.detail;
  range.value = { from, to };
}
```

## 复合开关

`nxp-switch-setting` 的 `label` 必填；属性为 `id` string 空、`modelValue` boolean false、`description`/`help`/`ariaLabel` string 空和 `disabled` false。它发出 `update:modelValue`、`change`，detail 均为 `[boolean]`，并将 `false` 作为合法关闭态传递。默认 slot 不参与设置值。

`nxp-switch-list` 没有属性和自定义事件，只接收默认 slot，适合承载多个 `nxp-switch-setting`；每个设置项的值和错误状态仍由子组件与调用方维护。

开关组合的布局由组件负责；需要调整业务布局时使用公开 CSS Variables，不访问内部节点 class。`nxp-switch-setting` 支持 `--nx-switch-setting-min-height`、`--nx-switch-setting-height`、`--nx-switch-setting-padding`、`--nx-switch-setting-border`、`--nx-switch-setting-border-bottom`、`--nx-switch-setting-radius`、`--nx-switch-setting-background`、`--nx-switch-setting-hover-background` 和 `--nx-switch-setting-copy-padding`。其中 `--nx-switch-setting-control-width`、`--nx-switch-setting-control-height`、`--nx-switch-setting-track-width`、`--nx-switch-setting-track-height` 用于协调右侧 `nxp-switch` 的尺寸；`nxp-switch` 也可直接使用 `--nx-switch-control-width`、`--nx-switch-control-height`、`--nx-switch-track-width`、`--nx-switch-track-height`、`--nx-switch-thumb-size` 和 `--nx-switch-thumb-translate`。

## 选择器消费规则

- 通过公开属性传入 `false`、`0`、空字符串和空数组；调用方不要用 truthiness 判断这些值是否存在。
- 弹层选择器的打开状态和草稿状态由组件内部处理，外部只保存已发出的受控结果。
- 组件不会拼接业务 URL、修改页面路由或替调用方提交网络请求。

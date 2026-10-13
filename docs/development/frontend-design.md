# 前端设计规范

宿主页、由 Host 渲染的专项能力以及官方 managed 插件共用本规范。公共组件位于 `frontend/src/ui/`，插件使用公开 `nxp-*` 元素；源码和生成的 bundle 分别管理。

| ID | 规则 |
|---|---|
| UI-01 | 语义卡片最多三层，第四层起违规。包装、字段和透明列表行不计层数；深数据继续用缩进分组显示，不截断。 |
| UI-02 | 交互使用公共组件；原生实现限公共组件内部及明确登记的必要节点。 |
| UI-03 | 公共卡片按渲染祖先交替表面，Vue 与 light DOM 共用上下文。浮层重新建立视觉上下文。 |
| UI-04 | flex/grid 子项允许收缩；长文本换行、省略后提供全文或内部滚动，不能依靠页面裁剪掩盖越界。 |
| UI-05 | 内容尺寸响应式，覆盖 360/768/1280 宽度；内容高度驱动，受限媒体或日志提供内部滚动。 |
| UI-06 | 可排序集合使用一个整体卡面、分隔线行和稳定 ID，把手支持鼠标与键盘；不显示上移/下移箭头按钮。 |
| UI-07 | 间距复用 4/8/12/16/24/32/40px 的 `--nx-space-*`；字段和区块优先使用语义别名。 |
| UI-08 | 对话框、确认、菜单、提示复用公共组件；业务代码不使用 alert/confirm/prompt。 |
| UI-09 | 控件具备可访问名称、焦点和键盘路径；触控目标至少 40px。 |
| UI-10 | 空、加载、错误、成功及保存中状态可辨认，失败保留草稿与可恢复操作。 |
| UI-11 | 文案走 i18n；中英文、长路径与无空格文本均可读取。 |
| UI-12 | 使用主题和动效 token，响应 reduced motion；离开页面释放订阅、观察器和浮层。 |

卡片绘制根使用 `data-nxp-surface-kind="card"`，自定义元素 host 不重复标记。`data-nxp-surface-depth` 和 `data-nxp-surface-tone` 由公共上下文维护；业务代码不按任务深度计算背景。`tone`、`variant`、`surface` 仍接受现有值，卡片背景随真实容器层级计算。任务递归组件的 `flattenAtLimit` 在第三层之后改为透明分组，仍保留折叠、任务身份、证据及深链接。

| 例外 | 精确范围 | 条件 |
|---|---|---|
| 原生交互实现 | `ui/primitives/` 与 `ui/composites/` 内公开控件的实际实现节点 | 仍需标签、焦点、键盘与禁用语义；调用方不复制实现。 |
| 隐藏文件输入 | 公共 FilePicker 的文件输入 | 仅用于文件选择，界面操作由公共按钮承载。 |
| 数字增减按钮 | `ui/primitives/NxpNumberInput.vue` 的 `data-nxp-step` 按钮，含公开 `nxp-number` | 保持上下排列的紧凑增减按钮，可低于 40px；数字输入框仍提供直接输入和键盘操作。 |
| 脚本导入文件输入 | `features/scripts/ScriptEditorModal.vue` 的 `importFileInput` | `type=file`、`aria-hidden=true`、`tabindex=-1`，仅公共导入按钮激活。 |
| 判断脚本上传 | `features/scripts/ScriptEditorModal.vue` 的 `uploadJudgeScript` | 创建未插入页面的 `type=file` 输入，由公共上传按钮激活。 |
| 头像上传 | `features/users/UsersPage.vue` 的 `uploadAvatar` | 创建未插入页面的 `type=file` 输入，由公共头像按钮激活。 |
| 日期投影 | `features/history/components/RangePicker.vue` 的 `history-from/history-to` | `type=hidden`，承载现役日期控件的值投影，不增加可见交互。 |
| 控件与媒体尺寸 | 统一控制高度、图标、边框、触控尺寸及媒体 aspect-ratio | 不作为业务卡片固定宽高的豁免。 |
| 模态最大宽高 | 公共 Modal 的面板 max-width/max-height | 窄屏收缩，短视口内容可滚动，footer 可访问。 |
| 业务状态背景 | 输入、选中、危险、成功与警告提示 | 使用相应语义 token，不算普通卡片背景交替。 |

设计专项在仓外本地运行，静态检查解析 Vue/TS/CSS AST 并生成全路由、slot、次级界面 inventory；动态结构标记 NEEDS_BROWSER。例外执行映射必须定位具体组件/节点，不允许整页或整插件豁免。真实浏览器检查覆盖双语、双主题、三种视口，以及深任务树、长文本、短视口、键盘排序、焦点返回与反复挂载。三层合法、四层失败必须分别校验。

专项脚本、报告和当次截图保存在已登记的仓外验收目录，不提交、不加入 PR CI、不建立像素基线。设计断言不加入核心功能门禁；排序持久化、确认、公开事件与生命周期的功能回归仍使用现役测试入口。真实浏览器未执行或 inventory 未覆盖时不能记为验收通过。

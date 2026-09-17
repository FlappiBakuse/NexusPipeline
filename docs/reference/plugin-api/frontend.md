# 前端架构

## 前端插件运行时（Frontend API 1.5）

前端扩展与 C# API 独立版本化。manifest 同时声明 `frontend-module` capability 和 `frontend` 对象：

```json
"capabilities": ["frontend-module"],
"frontend": {
  "apiVersion": "1.5",
  "entry": "web/main.js",
  "styles": ["web/style.css"]
}
```

入口 ES module 必须导出 `activate(host)`。宿主通过 `GET /api/plugin-runtime/frontend` 发布已启用、API 兼容的安全描述，动态加载入口并按需注入样式。插件 host 提供：

- `host.plugin`：当前插件的 manifest 描述（含 `name`、`displayName`、`version`、`frontendApiVersion`），冻结只读；
- `host.api.get/post/put/patch/delete(route, body, signal)`：以 JSON 语义访问插件自己的 `/api/plugin-api/` 命名空间；
- `host.api.blob(route, { query?, signal? })`：以 `GET` 读取二进制响应，返回 `Blob`；查询参数经 `query` 传入；
- `host.api.upload(route, body, { method?, contentType?, query?, signal? })`：发送二进制请求体并读取 JSON 响应；`method` 缺省 `POST`，`contentType` 缺省取 `body.type`，再回退到 `application/octet-stream`；
- `host.routes.register(route, handler)`：注册 `#/plugin/<name>/<route>` 页面路由；
- `host.nav.register({ id, title, route, icon, order })`：向 `shell.nav` 增加导航项；
- `host.slots.register(slot, renderer)`：接入稳定 UI slot。renderer 接收 `{ element, context }`，在自己的 surface 中挂载内容并返回清理函数；
- `host.ui.query/save/action(...)`：使用声明式 UI 贡献接口；`host.ui.toast(message, tone)` 显示宿主提示；
- `host.lifecycle.onPageEnter/onPageLeave/onPageUpdated/onDispose(...)`：订阅页面生命周期；
- `host.appearance`：通用外观表面，提供 `registerTheme(name, definition)`、`applyTheme(name)`、`setTokens(tokens)`、`clearTokens()`、`setBackground(surface)` 和 `clearBackground()`。
- `host.executionPreview.capture(runId, signal)`：按宿主当前运行目标读取受控的 PC 游戏客户区或模拟器画面；返回 360p JPEG 或等待状态。该接口用于运行预览，不等同于判断脚本的运行期通知截图。
- `host.i18n`：读取插件 manifest 中的本地化资源，提供 `locale`、`defaultLocale`、`t(key, args, fallback)` 和本地化日期/时间/数字格式化；资源仅属于当前插件。

稳定的外部契约包括 Frontend API `1.5` 精确版本、上述 `host.*` 能力、18 个公开 slot 名称、renderer surface 与 context 的可观察语义、公开 `nxp-*` 元素、主题 token、light DOM 下的可观察视觉与交互行为，以及 route/nav/slot/lifecycle 的挂载与清理语义。宿主侧桥接实现位于 `frontend/src/plugin-bridge/`，其文件划分、内部函数、宿主平台模块路径和宿主私有 class 都是内部实现，不构成插件公共 API。

前端模块运行在管理页面同源环境，可以使用 DOM、构建后的 ES module 和 CSS。Frontend API 采用精确版本匹配：只有 `1.5` 被接受，其他主次版本均拒绝加载，不提供兼容桥。启用且兼容的插件会直接加载其前端模块；宿主继续校验运行状态、Frontend API 版本、公开资源路径、扩展名和文件存在性。插件前端应使用 Vue/TypeScript/Vite 或等效构建链生成 `web/` 静态资源，通过公开 `nxp-*` Native Custom Elements 以及 slot surface 与宿主交互，不依赖宿主 Vue 内部实现。

公开元素注册表位于 frontend/src/ui/register.ts 的 NEXUS_PUBLIC_ELEMENTS，当前包含 38 个元素，完整清单与复合元件契约见[公共 UI 目录](../ui/README.md)。插件使用注册表登记的元素。

元素在 light DOM 下渲染，自身不产生额外布局盒：插件的结构卡片（`nxp-collapsible-card`、`nxp-section-card`）与宿主卡片一样直接参与设置页卡片栅格，展开态因此按同一规则置顶。`nxp-switch-list` 把多个 `nxp-switch-setting` 组成与设置页一致的开关列表。

文本类元素（`nxp-button`、`nxp-badge`）通过 `label` 属性接收文案：属性写法不产生插槽子节点，父级重渲染不会影响元素自身的 DOM 与交互；插槽内容（`<nxp-button>删除</nxp-button>`）继续作为替代写法，元素在父级重新渲染后仍保留自身结构与样式。

- `nxp-section-card`：props 为 `title`、`description` 和 `variant`（`primary` 或 `secondary`，默认 `primary`）；默认插槽为 body，具名插槽为 `header`、`description` 和 `actions`。
- `nxp-collapsible-card`：props 为 `title`、`description`、`expanded`（布尔，默认 `false`）、`panel-id` 和 `surface`（`default` 或 `secondary`，默认 `default`）；展开状态由调用方受控，展开变化时 emit `toggle`，事件负载在 `CustomEvent.detail[0]`；`panel-id` 同时用于 `aria-controls` 与 body 的 id；body 为默认插槽，header 右侧为 `actions` 具名插槽。
- `nxp-modal`：公共二级编辑表面使用 `surface="secondary"`，宽版使用 `size="wide"`；设置 `footer` 布尔属性后会启用底部操作栏，`footer` 具名插槽中的内容显示在带分隔线的底部操作栏。

设置页的折叠卡片由宿主统一协调，插件可以接入同一协议：插件展开自己的卡片时向 window 派发 `nxp-settings-panel-toggle`（`detail` 为 `{ panelId }`，收起时 `panelId` 为 `null`），并监听 `nxp-settings-panel-state`（`detail` 为 `{ panelId }`）以收起其它卡片。字段帮助文案使用宿主工具提示约定：在控件容器上设置 `data-help="说明文字"`。

前端资源必须位于插件目录的 `web/` 下；宿主只允许 `GET`/`HEAD` 访问 `/plugin-assets/{plugin}/{relative}`，执行路径包含校验、扩展名白名单和文件存在校验，不提供目录浏览。允许的文件类型为 JS/MJS、CSS、JSON、SVG、PNG、JPG/JPEG、WEBP、GIF、ICO、WOFF/WOFF2。`plugin.json`、配置、密钥、程序集和调试符号不属于公开资源。

外观表面使用 CSS Variables 作为主题 token；主题名称、token 名和值均经过长度和字符校验。`setTokens` 的 token 名必须匹配 `--[A-Za-z0-9_-]{1,96}`，值不超过 4096 字符且不含控制字符；token 应用在 `body` 上，因此优先于主题 token 并跨主题切换保持有效，直到 `clearTokens()` 或替换新集合。`setBackground(surface)` 接受 `url`（仅 `http`、`https`、`blob` 和 `data` 协议，其余拒绝）、`blurPx`（0–40）、`dimPercent`（0–80）、`surfaceTransparencyPercent`（0–50）与 `secondarySurfaceTransparency`（布尔，默认 `true`）。背景地址在交给 `setBackground` 后由外观表面托管：替换新地址或调用 `clearBackground()` 时宿主回收上一个 `blob:` Object URL，插件无需重复释放。外观变化继续广播 `nexus:appearance-changed`；`createAppearanceHost()` 不接收插件名参数。

壁纸配置、配额、文件校验、去重、轮换、配色与持久化属于插件业务，由插件通过自己的资产 scope、插件 Web API 与前端模块实现；宿主只提供通用资产存储、二进制 Web API 与通用外观表面。旧宿主壁纸数据的搬迁见上文「旧外观数据搬迁」。

`capabilities` 仅作为发现元数据，除已明确接入的 v1.3 扩展端口外不会自动获得业务语义。`script-profile` 等未来能力需要宿主明确接入；`background-jobs` 不会被当作专项脚本选择器。代码插件默认关闭，启用后需重启服务；运行状态可在 `/api/status` 的 `configuredEnabled`、`runtimeEnabled`、`state`、`minHostVersion`、`runtimeErrorCode`、`hasFrontend` 和 `frontendApiVersion` 字段中查看。宿主版本低于 `minHostVersion` 时使用 `state=Incompatible` 与 `runtimeErrorCode=plugin_incompatible_host`，不会解析专项插件、注册其能力或加载 managed-code 程序集；Plugin API 不兼容使用 `plugin_incompatible_api`。插件商店列表与详情另提供 `compatibilityCode`，使用 `host_version_too_low`、`plugin_api_incompatible` 或 `invalid_version` 区分更新阻断原因。前端描述中的 `defaultLocale` 与 `localization` 只包含该插件已声明并通过校验的资源。

插件管理页使用 `/api/plugins` 与 `/api/plugins/store` 获取列表，使用 `/api/plugins/{name}/detail` 与 `/api/plugins/store/{name}/detail` 获取详情。详情包含统一展示元数据、完整更新记录和受限 README；作者、标签、主页和 README 由插件仓库的 `store.json` 与包内容提供，`createdAt` 是 catalog 可选的创建日期投影，缺失时按空值展示，更新时间取最新更新记录日期。

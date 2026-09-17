# 插件公共契约

[文档门户](README.md) · [插件 API 索引](reference/plugin-api/README.md) · [公共 UI 参考](reference/ui/README.md)

本文件保留为插件 API 的兼容入口。当前完整契约按运行形态拆分为 manifest、managed、Frontend 和 data-specialized 四份专题；插件安装与加载责任见[插件运行与安装](architecture/plugins.md)。

## 当前契约导航

| 任务 | 当前权威专题 |
|---|---|
| 身份、能力、资源、目录和发行包 | [Manifest 与包格式](reference/plugin-api/manifest.md) |
| managed-code C# 生命周期与宿主端口 | [Managed Plugin API](reference/plugin-api/managed.md) |
| 浏览器模块、slot、路由和 dispose | [Frontend API](reference/plugin-api/frontend.md) |
| resolve、judge、只读探针、截图和恢复描述 | [Data-specialized](reference/plugin-api/data-specialized.md) |
| 公共 `nxp-*` 元件的属性、事件和 slot | [UI 参考](reference/ui/README.md) |

## 接入边界

- `plugin.json` 是插件身份、类型、最低宿主版本、能力和资源入口的声明；具体字段以 [manifest.md](reference/plugin-api/manifest.md) 为准。
- managed-code 插件通过 `NexusPipeline.Plugin.Abstractions` 的当前 API 端口工作；宿主内部服务、领域模型和组合根不属于插件依赖面。
- Frontend 插件导出 `activate(host)`，在激活时注册能力，在 dispose 时移除路由、导航、slot、词典和事件订阅；只消费 Frontend API 1.5 与公开 `nxp-*` 元件。
- data-specialized 插件由目录数据和 profile 解析驱动；判断脚本输出、配置编辑和 `config-restore.json` 均受当前输入/输出契约约束。

## 旧链接承接

这些显式锚点承接旧版外部链接，并指向当前对应专题。API 版本号和 ADR 编号保留为契约身份；普通专题不再沿用旧总目录编号。

<a id="目录结构"></a>
<a id="managed-code-c-插件plugin-api-v18"></a>
<a id="前端插件运行时frontend-api-15"></a>
<a id="pluginjson根文件"></a>
<a id="resolvejson推导配置"></a>
<a id="判断脚本"></a>
<a id="配置还原描述config-restorejson"></a>

旧入口与当前专题的对应关系见 [migration-map.json](migration-map.json)。官方插件作者步骤见相邻仓库的 [NexusPipeline-Plugins 文档门户](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/README.md)。

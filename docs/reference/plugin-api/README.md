# 插件 API 参考

插件契约按运行形态分层维护。`manifest` 描述插件身份、资源和能力声明；`managed` 描述 C# 插件可以调用的宿主服务；`Frontend` 描述浏览器模块、slot、公共元素和生命周期；`data-specialized` 描述专项脚本、profile 推导与配置还原。插件作者的操作流程见相邻的 NexusPipeline-Plugins 仓库文档。

| 契约 | 负责内容 | 入口 |
|---|---|---|
| Manifest 与包格式 | `plugin.json`、`store.json`、资源路径、能力声明和发行布局 | [manifest.md](manifest.md) |
| Managed Plugin API | `INexusPlugin` 生命周期、Plugin API v1.9、用户/数据/通知/模拟器等服务端口 | [managed.md](managed.md) |
| 执行 provider | 冻结计划、Host worker、结构化事件和既有队列／恢复接入 | [execution-provider.md](execution-provider.md) |
| Frontend API | Frontend API 1.5、`activate(host)`、路由/导航/slot、公共元素和 dispose | [frontend.md](frontend.md) 与 [UI 参考](../ui/README.md) |
| Data-specialized | `resolve.json`、judge、只读探针、截图和 `config-restore.json` | [data-specialized.md](data-specialized.md) |

## 维护边界

- 宿主 API 的版本与真实注册表以 `src/NexusPipeline.Plugin.Abstractions`、`frontend/src/plugin-bridge` 和本目录专题为准。
- Frontend 插件只能消费公开 Frontend API、公开 slot 和 `nxp-*` Native Custom Elements；宿主私有 Vue 组件、私有 class 和内部状态不属于契约。
- 插件安装和加载的宿主所有权见[插件运行与安装](../../architecture/plugins.md)；具体包校验和作者工作流见相邻插件仓库的[文档门户](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/README.md)。

任务级事实与选择重试见[专项任务协议](task-protocol.md)。

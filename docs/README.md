# NexusPipeline 文档

产品安装与使用见仓库根目录 [README.md](../README.md)。本门户面向开发、维护和插件作者，按任务连接当前规则、代码入口与验证要求；版本历史由 CHANGELOG 和历史索引负责。

## 按任务选择

| 任务 | 这里解释什么 | 阅读入口 |
|---|---|---|
| 修改配置交换或处理残留 | 配置归属、会话阶段、保全优先级与恢复失败边界 | [配置专题](architecture/configuration.md)、[恢复专题](architecture/recovery.md) |
| 排查取消、超时或资源租约 | 取消传播、进程退出、配置还原与租约释放的先后 | [执行专题](architecture/execution.md) |
| 修改调度去重或 occurrence | 队列计划、触发窗口、去重 fence 与调度恢复 | [调度专题](architecture/scheduling.md) |
| 新增页面控件 | 元件选型、属性/事件/slot 契约和公共边界 | [UI 参考](reference/ui/README.md)、[前端架构](architecture/frontend.md) |
| 修改插件宿主接口 | managed、Frontend、manifest 与 data-specialized 契约的责任边界 | [插件 API 索引](reference/plugin-api/README.md) |
| 修改插件安装恢复 | catalog 校验、pending 事务、安装交换与加载时机 | [插件运行专题](architecture/plugins.md)、[插件 API 索引](reference/plugin-api/README.md) |
| 修改宿主更新 | 更新发现、下载校验、启动应用和失败恢复 | [更新专题](architecture/update.md) |
| 验证一处修改 | 测试层级、逻辑分组、正式模式和本地反馈差别 | [测试索引](testing/README.md)、[测试域](testing/domains.md) |
| 开始或维护一次开发 | 环境、构建、协作和发行前置条件 | [开发索引](development/README.md) |
| 查当前未解决问题 | 当前状态和仍需完成的验证 | [项目状态](STATUS.md) |
| 追溯已发布版本 | 版本事实、历史文档和决策理由 | [历史索引](history/README.md)、[CHANGELOG](../CHANGELOG.md) |

## 按资料类型浏览

- [架构索引](architecture/README.md)：当前产品机制、不变量、模块边界和代码定位。
- [参考索引](reference/plugin-api/README.md)：插件接口、公共 UI 和控制面契约。
- [开发索引](development/README.md)：环境、协作、构建与发行步骤。
- [测试索引](testing/README.md)：测试策略、唯一命令入口、逻辑域和隔离要求。
- [决策索引](decisions/README.md)：仍影响当前实现的架构取舍。
- [历史索引](history/README.md)：已发布版本归档，不作为当前行为规范。

机器路由保存在 [map.json](map.json)。本地可执行 `node tools/docs-index.mjs find 配置恢复 --json`，默认只返回当前主题。

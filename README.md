# NexusPipeline

NexusPipeline 是 Windows 11 x64 本地自动化管家。它把脚本、账号配置、运行队列、日程、日志与插件放在同一界面中管理；桌面客户端与浏览器共用业务界面。

> 本文档是 0.18 本地开发对齐稿。本轮目标是恢复 0.17.6 的已确认业务行为，同时保留 next 的架构、格式和代码风格。目标行为的实现与验证状态统一记录在[当前状态](docs/status.md)；本文档不表示软件已经正式发行。

## 开始使用

1. 按[安装与启动](docs/user/install.md)取得完整候选，使用独立的新目录。
2. 按[第一次运行](docs/user/quickstart.md)登记一个已能独立运行的脚本。
3. 为账号绑定脚本并核对配置，先看计划，再开始运行。
4. 在[运行历史](docs/user/history.md)中确认该账号的结果、日志和截图。

0.18 使用新的实例与插件协议，不提供把 0.17 运行目录直接导入的新格式兼容层。保留原版本及原目录，通过原版本查看旧记录。Setup、Portable 与 Update 的用途不同，不能混用。[安装说明](docs/user/install.md)解释具体步骤。

## 按任务阅读

| 你要做什么 | 入口 |
|---|---|
| 使用软件、管理账号和日程 | [用户指南](docs/user/README.md) |
| 修改 Host、Web 或桌面客户端 | [本地开发](docs/developer/README.md) |
| 让 Codex 或其他 Agent 修改项目 | [Agent 任务入口](docs/agents/README.md)与根 AGENTS.md |
| 开发官方插件 | [NexusPipeline-Plugins](https://github.com/FlappiBakuse/NexusPipeline-Plugins)中的作者文档 |
| 判断当前功能和候选是否已经验证 | [当前状态](docs/status.md) |

## 开发约定

本次选定任务的代码、必要的产品假设断言与对应文档先统一完成；之后冻结输入，合并执行最小定向验证。不要每写完一个文件或任务卡就运行全量测试。验证分别报告 lint、typecheck、test 与 build；构建成功不代替业务结果通过。[开发流程](docs/developer/workflow.md)与[验证规范](docs/developer/validation.md)给出可直接执行的规则。

产品行为以用户已确认的恢复目标为准，精确协议以当前 schema 与 SDK 为准。旧格式读取器、第二套状态机、通用兼容框架和未出现需求的分支不随行为恢复引入。

## 版本依据

本开发包对比 Host main `3fa803f3cb3ab59564bfad6fcf19c512dbfcd45d` 与 next `0bb4d144d92805a2d489c65d0be3b7527c7810fd`。本机开始时核对实际 HEAD；只检查任务相关漂移，不把这两个参考提交写成新实现的身份。

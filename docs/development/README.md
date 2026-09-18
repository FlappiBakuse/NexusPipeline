# 开发索引

本目录把开发工作拆成环境、协作和发行三个当前专题。测试层级见[测试索引](../testing/README.md)，完整命令见[测试命令](../testing/commands.md)；运行时数据和用户现场遵循项目 [AGENTS.md](../../AGENTS.md) 的边界。

## 开发入口

| 主题 | 适用场景 | 入口 |
|---|---|---|
| 环境与本地运行 | 安装 SDK、构建前端/宿主、调试运行时和隔离数据 | [setup.md](setup.md) |
| 协作与提交 | 版本边界、提交授权、文档维护和 UI 测试治理 | [workflow.md](workflow.md) |
| 发行与更新 | 发布前门禁、Pre-release 规则、包校验和发布后清理 | [release.md](release.md) |

开始一次修改时，同时核对[架构索引](../architecture/README.md)、[插件 API 索引](../reference/plugin-api/README.md)和适用的测试域；版本历史与当前状态分别见[历史索引](../history/README.md)和 [STATUS.md](../STATUS.md)。

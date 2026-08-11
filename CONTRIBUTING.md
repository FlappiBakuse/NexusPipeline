# 贡献指南

感谢参与 NexusPipeline（枢链）开发。**完整规范见 [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)**（提交信息、版本号、Release 分发规则）。本文为快速入门索引。

## 快速开始

1. 同步最新代码：`git checkout main && git pull`；
2. 本地验证：`build.cmd` → 全量 e2e（`npx playwright test`，46 用例）全绿；CI 跑核心回归集（`$env:NEXUS_CI = "1"; npx playwright test`，45 用例）；专项测试 `node uitest\judge-scenarios.mjs`（99 断言）与 `node uitest\chaos-queue.mjs`（171 断言，需管理员 shell）；
3. 提交并推送（v1.0.0 之前直接 push `main`，禁止 force push；发布操作须先经用户同意）。

## 协作模式（以 v1.0.0 为界）

- **v1.0.0 之前**：`main` 无分支保护，直接 push main，不走 PR；版本发布一律 **Pre-release**。
- **v1.0.0 起**：所有改动只能通过 Pull Request 合入 `main`（CI 构建 + e2e 全绿后 squash 合并）。

## 提交信息规范（摘要）

采用 **Conventional Commits**，type / scope 英文，描述中文：`<type>[<scope>][!]: <描述>`。

| type | 用途 |
|---|---|
| `feat` | 新功能 |
| `fix` | 缺陷修复 |
| `docs` / `refactor` / `perf` / `test` | 文档 / 重构 / 优化 / 测试 |
| `build` / `ci` / `chore` / `style` / `revert` | 构建 / CI / 杂务 / 样式 / 还原 |

示例：

- `feat(dispatch): 新增调度中心批量执行`
- `fix(history): 修复历史详情时区错位`
- `feat(plugins)!: 变更插件契约签名`（破坏性变更须标注）

## 发布流程（摘要）

1. 构建与测试全绿；
2. 打 tag `vX.Y.Z` → `gh release create --prerelease`（标题 `vX.Y.Z`，notes 参考 v0.3.1 分组格式）；
3. 资产：`NexusPipeline-vX.Y.Z-win-x64.zip`（exe + wwwroot + plugins + README + LICENSE，排除 config/）+ 同名 `.sha256`（内容纯 hash，遵守 v0.2.1 及之前规则）。

详见 [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)。

# 贡献指南

感谢参与 NexusPipeline（枢链）开发。协作方式以 **v1.0.0 为界**：

- **v1.0.0 之前（早期开发阶段）**：仓库 `main` 无分支保护（无 ruleset），v0.1.0 至今均为**直接推送 `main`**，不走 Pull Request；提交前先 `git pull` 避免分叉，**禁止 force push**；版本发布一律标记 **Pre-release**。
- **v1.0.0 起（正式版本）**：所有改动**只能通过 Pull Request** 合入 `main`（CI 构建 + e2e 测试全绿后 squash 合并），禁止直接推送到 `main`。

## 开发流程

1. 同步最新代码：`git checkout main && git pull`；
2. 在 `main` 上直接完成改动，按规范提交（小改动可一条提交，大改动建议分多条逻辑提交）；
3. 推送前本地验证：`build.cmd` + `node uitest\test.mjs`（全量 e2e）通过；
4. 推送：`git push origin main`；
5. 如需发布：打 tag（`git tag vX.Y.Z` + `git push origin vX.Y.Z`）→ `gh release create`。

## 分支命名

如确需开分支协作，按前缀命名：

| 前缀 | 用途 |
|---|---|
| `feat/` | 新功能（如 `feat/dispatch-center`） |
| `fix/` | 缺陷修复（如 `fix/history-timezone`） |
| `docs/` | 文档（如 `docs/readme-license`） |
| `refactor/` | 重构（不改变行为） |
| `test/` | 测试相关 |
| `chore/` | 构建、依赖、杂务 |

## 提交信息规范

采用 **Conventional Commits**，type 用英文规范词，描述用中文：

- `feat: 新增调度中心批量执行`
- `fix: 修复历史详情弹窗时区错位`
- `docs: 补充文件结构说明`
- `refactor: 抽取运行会话状态机`
- `test: 增加调度队列端到端用例`
- `chore: 更新 build.cmd 发布脚本`

## 提交要求

- 描述说明：变更内容、原因、测试结果（本地 e2e 通过数）；
- 变更范围尽量聚焦单一目的，便于回滚；
- 不提交任何运行产物与配置数据（`release/`、`config/`、`history/`、`logs/`、`uitest/runtime/` 等已被 .gitignore 排除）；
- 不提交任何密钥或账号信息（通知密钥以 DPAPI 加密，加密值也请勿提交）。

## 版本与发布

- 版本号遵循 SemVer；发布走 GitHub Releases（预发布标记 Pre-release）；
- 发布流程：提交代码 → 打 tag（如 `v0.2.0`）→ `gh release create` 上传打包产物（exe + wwwroot + plugins + README + LICENSE，不含用户配置），附 SHA256。

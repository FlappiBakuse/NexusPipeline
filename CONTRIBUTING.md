# 贡献指南

感谢参与 NexusPipeline（枢链）开发。所有改动一律通过 **Pull Request** 合入 `main`，禁止直接推送到 `main` 分支。

## 开发流程

1. 从最新的 `main` 拉取分支：`git checkout main && git pull && git checkout -b <分支名>`；
2. 在分支上完成改动，按规范提交；
3. 推送并创建 PR：`git push -u origin <分支名>`，然后 `gh pr create`（或网页创建）；
4. **PR 必须通过 CI（构建 + e2e 测试）** 后方可合并；
5. 合并方式：**Squash merge**（PR 内多个提交压缩为一条进入 main）；
6. 合并后删除远程与本地分支。

## 分支命名

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

## PR 要求

- 标题同提交规范（如 `fix: 修复历史详情弹窗时区错位`）；
- 描述说明：变更内容、原因、测试结果（本地 e2e 通过数 / CI 状态）；
- 变更范围尽量聚焦单一目的，便于评审与回滚；
- 不提交任何运行产物与配置数据（`release/`、`config/`、`history/`、`logs/`、`uitest/runtime/` 等已被 .gitignore 排除）；
- 不提交任何密钥或账号信息（通知密钥以 DPAPI 加密，加密值也请勿提交）。

## 版本与发布

- 版本号遵循 SemVer；发布走 GitHub Releases（预发布标记 Pre-release）；
- 发布流程：合并代码 → 打 annotated tag（如 `v0.2.0`）→ `gh release create` 上传打包产物（release/ 下的 exe + WebRoot + plugins，不含用户配置）。

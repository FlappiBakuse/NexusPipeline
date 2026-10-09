# 协作流程

## 协作与提交规范

### 版本与发布权

- 版本 tag、Release 与发布资产由项目维护者负责；未经明确授权，不执行 commit、push、tag、Pull Request 或 Release。
- 版本号变更对应已确认的版本开发计划；用户指定新版本并开始开发后，立即同步项目版本配置。
- 开发前保存仓库外 HEAD、diff 与原字节检查点；标签操作另需明确授权。
- 不得提交运行产物、用户配置、日志、密钥；配置与用户数据永不进入版本库。

### 分支策略

| 参与者或阶段 | 提交路径 |
|---|---|
| 外部贡献者 | fork 或工作分支 → Pull Request |
| 项目维护者 | 本地 `develop` → Pull Request；`Host / 构建检查` 成功后 squash 合入 `main`，禁止普通直推或 force push |

如需开分支，使用 `feat/`、`fix/`、`docs/`、`refactor/`、`test/` 或 `chore/` 前缀。Release 分类遵循[发布专题](release.md)的宿主项目发布策略。

### 功能拆分与 UI 测试边界

代码提交按功能边界拆分，每个提交保持范围清晰、可独立验证、可审查和可回退；功能实现、测试治理、文档和 CI 调整分别形成对应变更单元。提交操作仍需维护者明确授权。

LLM 与自动化代理不得新增持久化视觉回归测试、截图基线或像素/布局断言。临时浏览器验证脚本放在操作系统临时目录，验证结束后删除且不得加入 Git。持久化 UI 测试只验证功能结果、ARIA、焦点、状态、提交、路由、API 效果和生命周期；测试归属与断言边界见[测试政策](../testing/policy.md)。

### 提交信息

采用 [Conventional Commits 1.0.0](https://www.conventionalcommits.org/zh-hans/v1.0.0/)，type 和 scope 使用英文，描述使用中文。

```text
<type>[<scope>][!]: <描述>

[可选正文]

[可选脚注]
```

- 冒号后使用一个空格；`<scope>` 使用圆括号，可省略；`!` 放在冒号前表示破坏性变更；
- 描述以动词开头，简短说明结果，不加句号；正文和脚注用空行分隔。

| type | 含义 |
|---|---|
| `feat` | 新功能 |
| `fix` | 缺陷修复 |
| `docs` | 文档与规范 |
| `refactor` | 不改变行为的重构 |
| `perf` | 性能优化 |
| `test` | 测试用例增改 |
| `build` | 构建系统与依赖 |
| `ci` | CI 工作流 |
| `chore` | 版本、脚本与工具配置 |
| `style` | 不改变逻辑的代码样式 |
| `revert` | 还原提交，并在脚注注明被还原提交 |

示例：

```text
feat(dispatch): 新增调度中心批量执行
fix(history): 修复历史详情时区错位
refactor(core): 抽取运行会话状态机
```

破坏性变更使用 `feat(scope)!:` 或在脚注写明 `BREAKING CHANGE: 说明升级影响与备份要求`。涉及既有 API、配置格式、磁盘布局或 Plugin API 契约的变化，先说明当前格式、备份要求和发布说明，并取得维护者确认；运行时保持当前持久化协议，不承担跨版本数据转换。

### 文档治理

一个主题只保留一份完整规则，其他地方用摘要和链接；evergreen 文档不记录已完成版本的流水账、旧验证数字和「当前最新 vX.Y」矩阵。发现代码、测试和 DESIGN 对产品行为的描述不一致时：先确认当前实现与回归测试，再判断 DESIGN 是 intended contract 还是陈旧描述；需要改变产品行为时停止文档范围内的自动修改，向维护者报告并等待决定。

### 开工与完工治理

开工运行 `node tools/governance.mjs preflight --path <目标目录> --keyword <主题>`，按需显式提供 `--partner-root <Plugins路径>`。输出从当前 `AGENTS.md`、`docs/map.json` 和 `NEXUS_PUBLIC_ELEMENTS` 提取文档、测试域及公共 UI 候选；Host 使用内部 Nxp 组件，插件使用公开 nxp-* 元素。新公共 UI 须解释候选为何无法复用，选择 primitive、composite 或 feature-local，语义重复由人工审核。

完工运行 `node tools/governance.mjs check --base <完整基线SHA> --working-tree --owner Tools --owner Docs --owner Tests --partner-root <Plugins路径>`。Owner 使用报告中的名称，可以重复声明；没有文件数或 LOC 上限。纯治理任务加 `--governance-only` 明确禁止产品源码、契约和版本输入的变更。报告可用 `--report <仓库外JSON路径>` 保存。

文件规则只检查 A/R/C 和未跟踪新增文件；已知运行产物拒绝，未知 Owner/JSON 角色及额外 Owner 为 REVIEW。新增 `@ts-nocheck` 为 REVIEW，保留已有豁免。REVIEW 需要解释，退出码 0 只表示没有确定性拒绝，不表示审核完成。版本授权无法从 Git 推断，普通任务中的相关输入变更仅提示审核。

`node tools/check-doc-links.mjs` 保留链接/片段/map 校验，并核对指定现役 API 声明与代码常量。此有限提取不检查任意文档语义；未知表述为 REVIEW，权威提取失败为 NOT_CHECKED 且退出非零。历史和负向测试不扫描旧版本数字。

Roslyn 报告包含分模式的去重 Owner 边及源码文本指纹。用 `--architecture-current <报告> --architecture-baseline <报告>` 比较：非法规则保持 FAIL，合法新增边为 REVIEW；无基线或模式不匹配为 NOT_CHECKED。调用者须记录两次编译的 SHA、引用和构建输入；源码文本指纹不证明引用程序集身份。A04 的存储后缀检测仍有现役静态盲区。该入口不替代 A01–A05、生产 CI 或功能测试，也没有修改必需检查或发布权限。

治理工具自测为 `node --test tools/governance.test.mjs`，先将 `NEXUS_TEST_ARTIFACT_ROOT` 指向已登记的外部隔离根；语义正反向 fixture 仍使用现役架构入口。

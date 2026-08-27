# 贡献指南（Contribution Guidelines）

感谢参与 NexusPipeline（枢链）开发。本文件说明 Issue、代码协作、提交信息、代码风格、文档治理和质量门禁。各主题的详细权威来源如下：

- 开发环境与调试：[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)；
- 测试层级、完整命令与发布门禁：[docs/TESTING.md](docs/TESTING.md)；
- 版本路线与未完成事项：[docs/ROADMAP.md](docs/ROADMAP.md)；
- 当前未解决问题：[docs/KNOWN_ISSUES.md](docs/KNOWN_ISSUES.md)；
- 发布流程：[docs/RELEASING.md](docs/RELEASING.md)；
- 安全漏洞：[SECURITY.md](SECURITY.md)。

## 目录

1. [版本与发布权](#1-版本与发布权)
2. [协作模式](#2-协作模式)
3. [如何提交 Issue](#3-如何提交-issue)
4. [如何提交代码](#4-如何提交代码)
5. [提交信息规范](#5-提交信息规范)
6. [代码与文档规范](#6-代码与文档规范)
7. [测试流程](#7-测试流程)

## 1. 版本与发布权

- 普通贡献始终通过 Pull Request 提交；版本 tag、Release 与发布资产由项目维护者负责。
- 版本号变更对应已确认的版本开发计划；用户指定新版本并开始开发后，立即同步项目版本配置。
- 架构重构、新功能和破坏性版本开工前创建本地开发基线备份 tag。备份 tag 只保留在本地，不推送到 origin。
- 未经维护者明确授权，不执行 commit、push、tag、Pull Request 或 Release；按项目规约要求创建的本地开发备份除外。
- 不得提交运行产物、用户配置、日志、密钥；配置与用户数据永不进入版本库。

## 2. 协作模式

| 参与者或阶段 | 提交路径 |
|---|---|
| 外部贡献者 | fork 或工作分支 → Pull Request |
| v1.0.0 之前的项目维护者 | 按当前主分支策略直接 push `main`，提交前先同步远端，禁止 force push |
| v1.0.0 起的项目维护者 | 工作分支 → Pull Request；CI 全绿后 squash 合入 `main`，禁止直接 push 或 force push |

如需开分支，使用 `feat/`、`fix/`、`docs/`、`refactor/`、`test/` 或 `chore/` 前缀。版本发布在 v1.0.0 前统一标记为 Pre-release。

## 3. 如何提交 Issue

### 缺陷报告

请使用 Issue 模板，并提供：

1. **版本或提交**：来自 `status` 命令、`/api/status` 的 `version` 字段或 Release 标题；
2. **复现步骤**：最小、可重复的操作步骤和相关配置条件；
3. **现场信息**：脱敏后的管理器日志、对应历史记录和必要的运行环境信息；
4. **预期行为与实际行为**；
5. **影响范围**：是否涉及配置、用户数据、远程访问、进程或插件。

请勿直接粘贴 `config/` 文件、密钥、账号、完整日志或未脱敏的个人路径。

### 功能建议

请说明使用场景、目标用户、期望行为、与现有脚本/队列/判断脚本/通知功能的关系，以及对 API、配置、插件、运行时数据和测试的影响。

安全问题遵循 [SECURITY.md](SECURITY.md)，使用私密漏洞报告入口。

## 4. 如何提交代码

1. 从最新 `main` 创建带前缀的工作分支，外部贡献者使用 fork；
2. 先阅读与修改范围对应的权威文档：行为看 `DESIGN.md`，定位代码看 `ARCHITECTURE.md`，测试看 `TESTING.md`，插件看 `docs/PLUGIN_API.md`；
3. 变更前检查工作树，保护运行时数据和用户配置；
4. 按第 7 节执行适用质量门禁；
5. 按第 5 节创建提交，说明变更范围、兼容性和验证结果；
6. 维护者按第 2 节的主分支策略处理合并与推送；
7. 涉及版本发布时，遵循 [docs/RELEASING.md](docs/RELEASING.md)。

涉及既有 API、配置格式、磁盘布局或 Plugin API 契约的变化，先说明迁移方案并取得维护者确认。

## 5. 提交信息规范

采用 [Conventional Commits 1.0.0](https://www.conventionalcommits.org/zh-hans/v1.0.0/)，type 和 scope 使用英文，描述使用中文。

### 5.1 格式

```text
<type>[<scope>][!]: <描述>

[可选正文]

[可选脚注]
```

- 冒号后使用一个空格；
- `<scope>` 使用圆括号，可省略；
- `!` 放在冒号前表示破坏性变更；
- 描述以动词开头，简短说明结果，不加句号；
- 正文和脚注用空行分隔。

### 5.2 type

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

### 5.3 示例

```text
feat(dispatch): 新增调度中心批量执行
fix(history): 修复历史详情时区错位
docs: 补充文件结构说明
refactor(core): 抽取运行会话状态机
test(e2e): 增加调度队列关键路径
ci: 接入文档一致性检查
```

破坏性变更使用 `feat(scope)!:` 或在脚注写明：

```text
BREAKING CHANGE: 说明迁移方式和兼容性影响
```

## 6. 代码与文档规范

### 6.1 代码

- 后端模块边界、依赖方向和代码落点遵循 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)；
- 新增类型默认 `internal`，公开类型限于项目既有契约清单和独立 Plugin API；
- Web API 由 `src/Web/ApiXxxHandler.cs` 承担，命令行入口位于 `src/Cli/`，服务注册位于组合根；
- 日志使用明确级别方法，保持配置交换、运行时数据和进程清理的不变量；
- 前端保持零构建、零 CDN、原生 ES modules、事件委托和稳定测试属性；完整前端约束见根目录 `AGENTS.md`。

### 6.2 文档治理

#### 主题所有权

| 主题 | 唯一详细权威来源 | 其他文档允许做什么 |
|---|---|---|
| 用户产品说明、安装、使用 | `README.md` | 提供摘要和链接 |
| 当前产品行为、状态机、不变量、已接受约束 | `docs/DESIGN.md` | 提供摘要和链接 |
| 当前模块、依赖、代码定位 | `docs/ARCHITECTURE.md` | 提供摘要和链接 |
| 开发环境、构建、调试 | `docs/DEVELOPMENT.md` | 提供摘要和链接 |
| 测试层级、完整命令、门禁 | `docs/TESTING.md` | 说明责任并链接 |
| Contributor 流程、Commit、文档治理 | `CONTRIBUTING.md` | 不复制实现细节 |
| 发布、SemVer、资产、SHA | `docs/RELEASING.md` | 只链接发布规则 |
| 安全支持与漏洞报告 | `SECURITY.md` | 只链接安全规则 |
| 历史版本变化 | `CHANGELOG.md` | 不在 evergreen 文档重复 |
| 尚未完成的开发计划 | `docs/ROADMAP.md` | 完成后移出并记录 CHANGELOG |
| 当前未解决缺陷与风险 | `docs/KNOWN_ISSUES.md` | 修复后移出 |
| Plugin SDK、manifest、扩展契约 | `docs/PLUGIN_API.md` | DESIGN/ARCHITECTURE 只说明边界并链接 |
| AI/Codex 操作约束 | `AGENTS.md` | 不保存产品语义副本 |
| 历史测试证据 | `tests/legacy/**` | 不作为当前规范引用 |

#### Evergreen 规则

`README.md`、`AGENTS.md`、`CONTRIBUTING.md`、`SECURITY.md`、`docs/` 下的当前规范文档和 `docs/PLUGIN_API.md` 都属于 evergreen 文档。除兼容性确实依赖版本号的内容外：

- 不记录已完成版本的流水账、修复过程和旧验证数字；
- 不把 `KN-*`、内部阶段编号或历史问题编号写进当前语义说明；
- 不维护“当前最新 vX.Y”这类容易过期的矩阵；
- 一个主题只保留一份完整规则，其他地方用摘要和链接；
- 完成项从 ROADMAP 移出，未解决项从 KNOWN_ISSUES 移出；
- 历史由 CHANGELOG、Release Notes、Git 历史和 legacy evidence 承担。

#### 冲突处理

发现代码、测试和 DESIGN 对产品行为的描述不一致时：

1. 先确认当前实现与回归测试；
2. 判断 DESIGN 是当前 intended contract 还是陈旧描述；
3. 需要改变产品行为时，停止文档范围内的自动修改，向维护者报告并等待决定。

文档清理不能替代行为决策，也不能通过改写说明掩盖未解决冲突。

## 7. 测试流程

完整命令、测试归属、CI 顺序、System Smoke 和清理要求只维护在 [docs/TESTING.md](docs/TESTING.md)。本节只给出责任矩阵：

| 修改范围 | 必须执行 |
|---|---|
| 文档、模板或 CI 文案 | 文档一致性检查；交付前执行默认质量门禁 |
| 后端、配置、调度、判定或存储 | Unit/Component、Web Logic、构建、UI Smoke；按影响范围追加 System Smoke |
| 进程、端口、解释器、模拟器、managed plugin 或更新事务 | 默认质量门禁 + 管理员 System Smoke |
| 压力、长时调度或 flake 诊断 | 适用门禁 + 对应 Stress/Soak 工具，并记录结果 |
| 发布前 | UI Smoke、适用 System Smoke、资产与 SHA 校验 |

测试失败时保留失败证据并修复根因。禁止通过自动重试、跳过失败或修改测试语义来掩盖问题。

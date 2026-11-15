# 贡献指南

首次开发先读[环境与首次运行](docs/getting-started/development.md)。按[文档导航](docs/README.md)定位主题；AI Agent 另遵守 [AGENTS](AGENTS.md)。

## 开发流程

1. 核对 HEAD、分支、工作树、暂存区和最新远端引用，确认已有修改归属。保留原内容；本机使用 `develop`，未同步先保全并正常快进或必要合并。
2. 明确任务、业务 owner、允许修改范围和可观察完成条件。当前代码、schema 与已确认需求共同确定预期；需要比较历史行为时固定参考提交。
3. 集中完成选定范围的实现、产品假设断言和对应文档。跨仓 SDK 变更先完成权威契约意图，再按依赖生成镜像和实际消费者输入。
4. 全部选定手写改动就绪后，按[验证规范](docs/development/validation.md)合并最低有效层检查。相同 SDK、编译闭包和场景执行一次，分项记录 lint、typecheck、test、build。
5. 失败保留原始结果，修复直接原因，复验受影响项。已通过且输入未变的无关结果继续复用。
6. 交付改动、实际命令与退出码、源码/产物身份、未运行项和阻塞。文档后改时单列 docs-only delta，原产物保留原身份。

准备与实现阶段以读取、编辑和静态差异自查为主；统一验证前不逐文件运行全套检查。诊断任务先准备最小观测支持，再依据实际结果补局部实现。

## 工程要求

架构边界见[系统架构](docs/architecture/overview.md)，用例归属见[Storage owner](docs/architecture/storage-owners.md)，命名及例外见[命名规范](docs/development/naming.md)。注释解释代码不易表达的原因；长期文档只描述最终行为与仍有效的设计背景。

依赖、缓存、构建、测试实例和报告使用显式仓外路径，用户数据与测试夹具分离。生成输出用现役 producer；常规开发使用仓内依赖锁。只有明确的依赖更新任务才使用[锁更新命令](docs/reference/commands.md#工具链与依赖锁)。

## 分支、提交与 PR

本机开发提交和推送仅进入 `develop`；按可独立审查、验证和回退的功能边界拆分，相关源码、测试和文档一起处理。先完成全部适用验证，再按授权统一推送。当前聊天的 GitHub 授权决定是否执行远端动作。

main PR 的现役入口为 [ci.yml](.github/workflows/ci.yml)，按完整差异选择生产闭包，文档范围保留检查结果，项目测试单独执行。PR 说明写最终问题、行为和实际验证；候选、tag 与正式发布遵循[发行流程](docs/operations/release.md)。

## 文档贡献

只修改行为实际影响的页面。新内容先查[主题权威表](docs/README.md#主题与权威来源)，选择现有位置或补充链接；放置规则、ADR 条件和自动生成边界见[维护规则](docs/README.md#维护规则)。

纯文档修改检查相对链接、锚点、代码路径和命令参数，并运行：

```powershell
python -B tooling/nexus.py check --scope source
git diff --check
```

本仓没有独立 Markdown lint 或链接检查 CLI；source check 负责文本、命名和生成一致性。链接另按文件存在及 GitHub 标题锚点核对。无需为文档修改安装依赖或构建客户端。

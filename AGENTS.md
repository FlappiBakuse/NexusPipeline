# NexusPipeline Agent 指令

本仓拥有 Host、Web、Windows 桌面与维护入口，以及插件公共 SDK。先读当前任务，再按[任务地图](docs/agents/README.md)选择领域页和直接调用者；[文档导航](docs/README.md)列出各主题的唯一维护位置。

## 开工与边界

- 核对分支、HEAD、工作树、暂存区和远端；保护已有修改，未知来源只暂停依赖它的操作。
- 本机开发遵守适用的 Workspace 规则，使用 `develop`。提交、推送、PR、tag、合并和发布按当前聊天授权执行。
- 禁止 reset、clean、自动 stash、force push 或覆盖未知用户内容。真实账号发送、游戏/脚本执行、生产数据变更需要对应授权。
- 仅为确有独立规则的模块增加子 AGENTS。Codex 沿工作路径合并指令，同目录优先 AGENTS.override.md，较深规则在适用范围覆盖冲突项；见[官方说明](https://learn.chatgpt.com/docs/agent-configuration/agents-md)。

## 模块导航

| 模块 | 代码 | 先读 |
|---|---|---|
| 领域规则与 ports | `src/core` | [架构](docs/architecture/overview.md) |
| SQL、文件、恢复与效果 | `src/platform/Storage` | [用例归属](docs/architecture/storage-owners.md)、[存储](docs/reference/storage.md) |
| 组合、HTTP、CLI、MCP | `apps/host` | [控制面](docs/reference/control-http.md) |
| Web / Electron | `apps/web`、`apps/desktop` | [前端](docs/architecture/frontend.md)、[桌面启动](docs/architecture/desktop-startup.md) |
| 安装、更新、卸载 | `apps/installer`、`apps/maintenance`、`apps/uninstaller` | [维护生命周期](docs/operations/installation.md) |
| 公共契约与 SDK | `contracts`、`sdk` | [SDK](docs/reference/sdk.md) |

## 必须保持

- 一个业务事实只有一个 owner。Core 不引用 Host、Platform、SDK wire DTO、数据库或 UI；Platform 实现 ports；Host 组合服务和入口。
- Test/Production 使用同源业务与 HTTP；Web、CLI、MCP 共用用例和权限。新格式按当前 schema 校验；未知字节保全拒绝，不补空库或旧协议兜底。
- 恢复与配置还原先核验 writer 完整停止、资源和实例身份；未知外部副作用只读核对，禁止自动重放。
- 普通重试、冷恢复后继和原操作查询保持各自身份；配置来源、调用输入、活动输出与长期发布分开处理。
- 秘密仅经专用能力和内存保护区处理；不写普通 DTO、草稿、浏览器存储、日志或回执。keep/set/clear 使用明确意图。
- 手写文本 UTF-8、LF；命名以[命名规范](docs/development/naming.md)和现役检查器为准。文件上限：C#450、TS/JS350、Vue300、CSS350、工具400、测试500、JSON/YAML350、文档400行。函数目标60行，超过100行审查职责。
- 生成文件通过 canonical producer 更新；不手填指纹、SDK 身份或 PASS。依赖、构建、实例、报告写显式仓外根；本机临时材料放 Temp。
- 删除代码先核对符号引用、全部编译上下文和[加载入口](docs/architecture/overview.md#从运行入口定位)；公开 SDK、序列化类型、原生 ABI 与动态注册须有独立的保留或删除依据。

## 实现与验证

- 按 [CONTRIBUTING](CONTRIBUTING.md)集中完成本次选定代码、必要断言和文档，再汇总去重的定向验证；失败后只补受影响项。
- 文本与源码规则：`python -B tooling/nexus.py check --scope source`；契约改变再选 `check --scope contracts`。
- C# 定向编译：`python -B tooling/nexus.py build --profile test --project NexusPipeline.Core --output <仓外构建槽>`。
- 产品假设按[验证规范](docs/development/validation.md)选择现役 group/method；Web 参数见[前端验证](docs/development/frontend-testing.md)。文档修改运行链接/路径检查及 `git diff --check`。
- PASS、FAIL、NOT_RUN、BLOCKED 按实际范围记录。未提交输入记 local-uncommitted、commit/tree 为 null；原产物保留自己的冻结身份。
- 原生功能交付须形成真实软件；纯文档修改执行适用文档检查。版本选择按当前目标，不因修复或重建重复 bump。

## 文档联动

公共接口变动检查对应参考和生成物；模块边界变动检查架构；构建/验证变动检查开发指南；安装与发布变动检查维护文档。重要且长期的边界选择记录 ADR，保留可核对背景。用户行为变化只更新对应用户步骤；仓库文档只保留项目已实现的内容。新文档归属和维护规则见[文档维护](docs/README.md#维护规则)。

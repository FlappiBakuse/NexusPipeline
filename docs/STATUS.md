# 项目状态（Status）

**更新日期**：2026-09-19｜**发布模式**：`major=0` 或带 `-beta.N` / `-rc.N` 后缀的版本为 Pre-release；`major>=1` 且无后缀的版本为正式 Release。源码进入 `main` 必须经过 PR、当前候选的完整 Qualification 与 squash merge；日常开发目标为 `develop`。

> 本文件记录尚未完成的开发计划、活跃技术验证和当前未解决问题。已完成版本以 [CHANGELOG.md](../CHANGELOG.md)、代码和测试结果为准。版本号只在用户明确指定时修改；开发检查点保存在仓库外，不以 Git 标签代替文件备份。

## 当前未完成事项

- 公共 UI 元件由对应 SFC 维护 DOM、交互和内部样式；feature 负责业务布局、状态与公开 props/events，插件通过注册的 `nxp-*` 元件消费宿主能力。

- 当前版本治理与验证结果记录在 [CHANGELOG.md](../CHANGELOG.md)；后续工作聚焦下方插件生态扩展、技术验证和已知问题台账。
- 前端架构现状以[架构索引](architecture/README.md)和[前端架构专题](architecture/frontend.md)为准；插件契约调整以[插件 API 索引](reference/plugin-api/README.md)为准。

## 后续功能：插件生态扩展

- [ ] 为更多官方 managed-code 插件补充 mock HTTP 与事件回归测试。
- [ ] 继续完善插件设置贡献的字段校验、错误呈现和目录元数据检查。
- [ ] 持续验证用户级插件数据清理、密钥保护和升级后的当前数据访问。

## 活跃技术验证与决策

- [ ] 在真实机器人环境完成钉钉/飞书签名推送验证。
- [ ] 完成更新事务的进一步故障注入矩阵，覆盖长时间运行、文件锁和异常退出组合。
- [ ] 持续维护运行时版本动态展示、真实计时回归和 Release 资产校验。
- [ ] 在 NexusPipeline-Plugins 的 `EmulatorSupport` 插件完成雷电、夜神和 BlueStacks 真机验证；实例识别、ADB 路由、启动/前台查询/截图/应用停止与安全关闭矩阵由[插件发行指南](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/RELEASING.md)维护。宿主 System Smoke 覆盖 Generic ADB、MuMuManager 和 managed-code provider 跨边界调用。

## 已知问题台账

只记录当前仍未解决、能够影响当前版本或需要专项验证的缺陷与技术风险。已修复问题的版本归属见 [CHANGELOG.md](../CHANGELOG.md)；产品有意保持的行为见 [DESIGN.md](DESIGN.md) 的「已接受的设计约束」。

状态说明：

- **稳定复现**：固定输入、确定调用顺序或最小测试场景可以重复命中。
- **待专项复现**：风险方向已经确认，但需要 Windows 文件系统时序、进程生命周期或长时运行等专用环境才能进一步定量。
- **调查中**：现象或影响范围已知，复现条件尚未收敛。

| 编号 | 风险描述 | 当前代码与证据 | 影响 | 后续动作 | 状态 |
|---|---|---|---|---|---|
| F01 | 两仓库根 AGENTS 依赖仓库外上级文档 | `AGENTS.md` | 单仓库 checkout 无法独立获得工程规则 | 使用仓库内自足 AGENTS，并在无父文档 checkout 复核 | OPEN |
| F02 | H3/H4/H5 以管理员权限作为测试准入 | `tests/run.mjs` | 普通终端无法执行同一套资格内容 | 统一 Test Host 与权限观测，删除权限跳过分支 | OPEN |
| F03 | 旧 CI 证明编排仍在测试入口 | `tests/run.mjs` | 旧 manifest/选择逻辑可能伪造或遮蔽真实结果 | 删除旧 CI plan、job manifest 与 summary 对账 | OPEN |
| F04 | H1 架构违规不会阻断 | `tools/NexusPipeline.Architecture/Program.cs` | 违规可能以成功退出码进入资格 | 使用强制 check 并传播违规退出码 | OPEN |
| F05 | baseline-prune 可能吸收新增违规 | `tools/NexusPipeline.Architecture/Program.cs` | 新债务被写入批准基线 | 先判定违规，再生成仅用于审阅的裁剪结果 | OPEN |
| F06 | 架构规则覆盖范围不足 | `tools/NexusPipeline.Architecture/BoundaryRules.cs` | 模块越界、定位器和环依赖可能漏检 | 补齐 namespace、locator、模块环与地图规则 | OPEN |
| F07 | `both` 架构模式未加载两个编译模式 | `tools/NexusPipeline.Architecture/ProjectLoader.cs` | 生产/Test Host 差异未被同时验证 | 明确加载两种属性图并分别报告 | OPEN |
| F08 | Roslyn 语义依赖收集不完整 | `tools/NexusPipeline.Architecture/DependencyCollector.cs` | 泛型、文件多声明和未匹配符号可能漏边 | 归一化符号并对无法归属的声明报错 | OPEN |
| F09 | 生命周期仍通过全局组合根解析 | `src/Host/Lifecycle/Bootstrap.cs` | 组合根边界未真正闭合 | 向生命周期显式传递所需依赖 | OPEN |
| F10 | 组合根容器生命周期未收尾 | `src/Host/Composition/HostCompositionRoot.cs` | 延迟捕获容器且无法验证/释放 | 构造验证、具体依赖传递和根释放契约 | OPEN |
| F11 | develop catalog 指向源码分支 raw 文件 | `src/Modules/Plugins/Repository/PluginRepositorySourceContext.cs` | 开发通道不遵守 Release 资产协议 | 固定消费 `plugins-develop` Release 资产 | OPEN |
| F12 | 远程资源重定向策略可能拒绝 CDN | `src/Platform/Networking/RemoteResourcePolicy.cs` | 合法 catalog/包无法下载 | 区分首跳官方仓库与允许的 HTTPS 资产主机 | OPEN |
| F13 | 未安装插件的兼容性判断顺序不一致 | `src/Modules/Plugins/Repository/PluginUpdatePolicy.cs` | 不兼容候选可能先被允许 stage | 与包层兼容性校验统一判定顺序 | OPEN |
| F14 | Plugins Qualification checkout 与 host-root 错配 | `NexusPipeline-Plugins/.github/workflows/release-qualification.yml` | P1/P2/P3 可能使用错误 SDK | 统一兄弟 checkout 布局与显式 host-root | OPEN |
| F15 | SDK 来源验证只认顶端引用且路径假设固定 | `NexusPipeline-Plugins/tools/sdk_source.py` | 固定 SHA 或本地联调可能解析错误输入 | 用提交可达性/实际 checkout 验证同一 SDK | OPEN |
| F16 | P3 源码与稳定发行基线未完整分离 | `NexusPipeline-Plugins/tools/qualification.py` | 候选可能读取当前工作树 catalog/state | 目标 main 提供发行基线，候选只提供源码输入 | OPEN |
| F17 | Qualification 固定旧 attempt 且失败仍可能 exit 0 | `tools/qualification_control.py` | 过期或失败资格可能被接受 | 使用实际 runAttempt，并让失败传播到 CLI | OPEN |
| F18 | 可信 workflow 身份未绑定实际 workflow 定义 | `.github/workflows/release-qualification.yml` | C、token 与长时 Gate 可能漂移 | 固定实际 workflow/controller SHA 和令牌生命周期 | OPEN |
| F19 | 合并资格关联仅做字符串搜索 | `tools/qualification_control.py` | 伪造文本可能被当作关联资格 | 严格解析并比对 H/B/C/runId/runAttempt/SDK | OPEN |
| F20 | Plugins Preview/stable 仅生成候选 artifact | `NexusPipeline-Plugins/tools/repository_publish.py` | 发布链路没有真实 writer | 实现可测试的远端写入事务与失败传播 | OPEN |
| F21 | stable workflow 缺触发与 writer | `NexusPipeline-Plugins/.github/workflows/publish-stable.yml` | main 合入后不会完成受控生成物发布 | 增加资格复核、受控触发和 publisher App 写回 | OPEN |
| F22 | Host Release 未验证合并资格 | `.github/workflows/release.yml` | tag 祖先关系不能代表候选已获资格 | 严格校验 main 合并提交及资格关联 | OPEN |
| F23 | Host 发布 runner 与打包边界不完整 | `.github/workflows/release.yml` | 资产可能打包到输入目录或过早公开 | 独立工作目录、显式仓库、先上传后公开 | OPEN |
| F24 | 远端保护与默认入口未切换 | GitHub 仓库设置 | main 仍可能绕过目标门禁 | 按 S11 由维护者配置并记录真实结果 | OPEN |
| F25 | 长期文档存在旧版本/父文档前提 | `docs/STATUS.md`、仓库导航 | 后续开发者会按过时规则操作 | 同步现行规则和补充后的未完成项 | OPEN |
| F26 | Test Host 构建产物 promise 清理失效 | `tests/run.mjs` | 多 Gate 可能复用已删除产物 | 按 mode+runId 管理缓存并在最后消费者后清理 | OPEN |
| F27 | 隔离 runtime 复制边界过宽 | `tests/support/test-runtime.mjs` | 用户数据或旁车文件可能进入测试目录 | 使用本次产物 allowlist、marker 和身份核验 | OPEN |
| F28 | Platform 内置 Host 更新产品策略 | `src/Platform/Networking/RemoteResourcePolicy.cs` | 基础设施层反向拥有业务发布策略 | 将策略移至 Updates/Host 端口，Platform 只做网络约束 | OPEN |
| F29 | Scripts 与 Users 存在类型级双向依赖 | `src/Modules/Scripts/Validation/ScriptSaveValidation.cs`、`src/Modules/Users/Contracts/ResolvedScriptUser.cs` | 模块图有真实环依赖 | 以消费者端口/共享契约拆开类型边界 | OPEN |
| F30 | Backend map 生成有空占位和不稳定 revision | `tools/NexusPipeline.Architecture/BackendMapWriter.cs` | 地图无法作为当前结构权威 | 从完整声明/测试归属生成稳定 map 与 entry points | OPEN |

## 维护规则

- 发布完成后，从「当前未完成事项」与「活跃技术验证」移除对应完成项，在 `CHANGELOG.md` 记录发布内容。
- 未解决且影响当前版本的问题进入上方台账；修复、失效或确认为设计契约后立即移出，历史由 CHANGELOG 与 Git 保留。
- 新条目必须先确认当前代码路径、回归测试和影响范围，再写入台账。
- 发现文档与代码/测试冲突时，先核对 intended contract；涉及产品行为的决定需要单独确认，不通过文档修改掩盖冲突。
- 新版本开发必须先创建本地开发基线备份并同步版本号；版本开发期间不拆分发布。

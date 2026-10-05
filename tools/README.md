# 宿主工具

产品测试由 `node tests/run.mjs` 编排，完整命令见[测试命令](../docs/testing/commands.md)。构建入口只在仓库外的原字节隔离副本运行；本机输出、缓存、报告和候选目录通过参数及进程环境提供。

| 入口或资源 | 输入与输出 | 调用者、平台与副作用 | 验证 |
|---|---|---|---|
| `NexusPipeline.Architecture/` | 当前 C# 工程 → 模块依赖与组合边界诊断 | backend 架构组；.NET 10；只读源码 | backend 核心组与架构反例 |
| `NexusPipeline.TaskProtocolTests/` | 显式插件检出、当前夹具 → Jint、账号隔离、编辑、运行身份及安装契约报告 | partner 门禁及显式诊断；Windows/.NET 10；受控运行目录和进程 | 插件核心组及 Host partner 门禁 |
| `host_candidate_source.py` | 原 candidate run、writer run → 服务端核实的 SHA、attempt、artifact ID/digest | Release writer；GitHub Actions read 网络；仅追加指定 output | `test_host_candidate_source.py` |
| `host_release.py` | 真实源码、前端、预装清单、staging 和版本 → 阶段清单、ZIP、候选及校验结果 | release workflow；生产构建在 Windows；明确输出目录、官方包下载 | `test_host_release.py` |
| `host_release_publish.sh` | 经核验的生产候选、来源身份及 token → tag 对应 Release 资产 | release writer；Bash/gh/curl；需对话内发布授权，写 GitHub | 来源与包校验；远端发行后下载复核 |
| `host_installer.py` | 同一 production staging、运行时锁、Inno 编译器 → Setup 与 metadata | release workflow；Windows；下载依赖、写指定输出，不安装软件 | `test_host_installer.py` |
| `installer.iss.in`、`installer-launch.iss`、`installer-languages/` | 安装器模板、共享启动/OS 返回边界及语言资源 | Inno 编译；正式 Setup 可安装 runtime、登记当前代际并写用户选择目录 | 安装器工具测试、受控登记和人工生产验收 |
| `runtime-dependencies.json` | 官方 URL、版本、size、SHA512/SHA256 与签名身份 | 构建及安装器的唯一 runtime 输入；不卸载其他 major | 真实下载/hash/Authenticode 与 installer 测试 |
| `installation-generation.mjs` | `Directory.Build.props` 中代际 → 当前名称和登记身份 | 测试运行器；只读；生产 helper 使用相同工程属性 | 安装代际单元及原生平台契约 |
| `pe_manifest.py` | 实际 PE → 嵌入的权限清单及运行标记 | 生产/Test Host/installer 校验；只读文件 | Release 与 installer 工具测试 |
| `check-doc-links.mjs` | 当前 Markdown、docs map → 链接/片段和路径诊断 | `host.docs` 与显式文档检查；Node；只读 | 文档门禁 |
| `source-hash.mjs` | 明确源码根 → 源码文件集合和指纹 | runner/候选来源；Node；只读 | runner 身份与 staging 测试 |
| `tests/` | 上述工具的受控输入 → 原生 unittest 报告 | CI control 门禁；Python；临时数据只在外部测试根 | `node tests/run.mjs gate --id host.release-contract` |

这些目录包含命令入口、可导入模块、工程和静态资源。CLI 参数及发行阶段以当前 parser 和[发布指南](../docs/development/release.md)为准；生成候选、验证通过和实际发布分别记录。

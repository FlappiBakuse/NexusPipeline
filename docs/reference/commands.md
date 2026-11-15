# 工具命令与构建来源

从 Host 仓库根执行；所有路径参数使用本轮拥有的明确仓外目录。本页列工具职责与输入，验证范围和测试示例归[验证规范](../development/validation.md)，首次运行归[开发入门](../getting-started/development.md)。尖括号参数须替换为实际路径或回执。

## 主入口

[tooling/nexus.py](../../tooling/nexus.py) 是参数的代码权威。`--help` 和 `<command> --help` 可读取当前选择。

| 子命令 | 输入与结果 | 详细说明 |
|---|---|---|
| `snapshot` | `--output`；可选 `--git-commit`，导出源码和身份 | [原字节来源](../operations/release.md#原字节来源) |
| `check` | `--scope source` 或 `contracts` | [lint](../development/validation.md#lint) |
| `build` | `--profile test|production --output`，可重复 `--project` | [typecheck](../development/validation.md#typecheck-与必要准备) |
| `test` | `--group --build-root --output`，可重复 `--method` | [test](../development/validation.md#test) |
| `dependencies` | `--update-locks --build-root`，可选项目 | 本页工具链与依赖锁 |
| `storage-experiment` | `--build-root --output` | [容量与性能](../development/performance.md) |
| `desktop-bundle` | `--runtime-archive --output` | [Electron 模块](../../apps/desktop/README.md) |
| `sdk-packages` | `--build-root --consumer-root --output --cache`，可选 `--compile-only` | [SDK](sdk.md)、[SDK 消费者](../development/validation.md#sdk-消费者与命名职责) |
| `product-publish` | `--build-root --output --runtime-archive --profile` | 本页完整软件 |
| `product-assemble` | `--publication-root --output`，可选 `--bundle-root` | 本页完整软件 |

`--project` 接受工程名，例如 `NexusPipeline.Core`；测试分组和方法取自当前解析器与原生发现。跨卷和桌面等外部输入只用于相应选定用例。Web runner 是独立脚本，参数见[前端验证](../development/frontend-testing.md)。

## 完整软件

`product-publish` 生成同源 Windows 单文件输入，`product-assemble` 消费实际 publication 和已验签插件 bundle。完整命令见[build](../development/validation.md#build)；三资产与 Setup 规则见[发行](../operations/release.md#公开应用与更新包)。

产品使用锁定官方 Electron ZIP；SDK 原包由真实 producer 和消费者回执绑定。构建成功、原生功能验收和正式发行分别记录。

## 工具链与依赖锁

构建、发布和原生测试按 [global.json](../../global.json) 解析 .NET 10.0 稳定 SDK，记录实际版本、安装位置和关键工具字节。中间输出按 SDK 隔离。

日常使用仓内 NuGet 锁。SDK 的 ILLink 注入项有差异时，工具在仓外派生锁，应用依赖请求、版本、摘要与依赖边沿用源码锁。选版验证见[工具链验证](../development/validation.md#发布产物与工具链验证)。

仅在依赖更新任务中执行：

```text
python -B tooling/nexus.py dependencies --update-locks --build-root <仓外构建槽>
python -B tooling/web_build.py --update-lock --output <仓外构建槽>
```

## 源码与回执

源码快照格式 `nexus-source-manifest-sha256-v2-lf` 按 UTF-8 相对路径排序，保存原字节大小、摘要与模式。仓外构建槽拥有不可变 manifest 和去重原字节；更新前核对旧证据。组件复用核对项目闭包、原回执和完整产物，未知 MSBuild 输入拒绝复用。

未提交输入记录 `local-uncommitted` 和 null commit/tree；固定 Git 导出另按原字节校验。原生测试保留真实 TRX、退出码与计时。来源资格与正式 writer 的服务端核验见[发行](../operations/release.md)。

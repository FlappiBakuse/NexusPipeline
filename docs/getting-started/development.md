# 开发环境与首次运行

目标：从 Host 源码形成一个独立的本地 Test 服务，确认控制面可用。完整桌面、安装器与插件装配按后续任务准备。

## 前置条件

完整原生环境为 Windows 11 x64、Python 3.12+、.NET 10.0 稳定 SDK 和 Node/npm。CI 使用 Python 3.13、Node 24；本地前端依赖以 [package.json](../../apps/web/package.json) 和原生锁为准。

[global.json](../../global.json) 从 10.0.100 起按 `latestFeature` 选择已安装稳定功能带；不选预发行。C# 编译配置见 [Directory.Build.props](../../Directory.Build.props)。只有当前任务需要的组件才准备，纯文档修改无需完整原生栈。

## 准备工作目录

在 Host 仓库根执行，使用本轮拥有的仓外根。示例使用显式仓外目录：

```powershell
$NexusWorkRoot = 'C:\NexusWork\nexus-development'
New-Item -ItemType Directory -Force "$NexusWorkRoot\tmp" | Out-Null
$env:TEMP = "$NexusWorkRoot\tmp"
$env:TMP = $env:TEMP
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:npm_config_cache = "$NexusWorkRoot\cache\npm"
```

构建入口自行冻结当前源码并准备锁定依赖，产物和回执留在指定槽。源码定位见[架构](../architecture/overview.md)，独立快照与工具参数见[命令参考](../reference/commands.md)。

## 编译并启动服务

按[贡献流程](../../CONTRIBUTING.md)完成本批改动后，构建 Host 闭包。首次运行也可直接使用当前源码：

```powershell
python -B tooling/nexus.py build --profile test --project NexusPipeline.Host --output "$NexusWorkRoot\host"
& "$NexusWorkRoot\host\build\bin\NexusPipeline.Host\release\NexusPipeline.Host.exe" serve --runtime-root "$NexusWorkRoot\runtime" --port 58731
```

构建应成功并写出 `build-receipt.json`；Host 成功启动后保持运行，控制地址为 `http://127.0.0.1:58731`。此处显式使用独立 runtime，避免无参数 GUI 入口将开发构建当作已装配软件。

在另一个终端从同一仓库根查询：

```powershell
$NexusWorkRoot = 'C:\NexusWork\nexus-development'
& "$NexusWorkRoot\host\build\bin\NexusPipeline.Host\release\NexusPipeline.Host.exe" control status --address http://127.0.0.1:58731
```

预期 stdout 返回当前实例的 JSON 状态；服务日志与就绪信息由原 Host 输出。首次只读观察不创建账号、不运行游戏或脚本。停止时在服务终端正常结束，等待所属工作停止。

## 下一步与失败定位

| 任务或现象 | 下一入口 |
|---|---|
| Web 类型、行为和浏览器调试 | [前端验证](../development/frontend-testing.md) |
| Core 或 Storage 规则验证 | [定向验证](../development/validation.md) |
| 完整 Electron 软件 | [产品命令](../reference/commands.md#完整软件)、[桌面模块](../../apps/desktop/README.md) |
| SDK 与插件开发 | [SDK](../reference/sdk.md)及 Plugins 作者入口 |
| 编译失败 | 构建回执和其中的原生日志，先检查 SDK 与锁定依赖 |
| 端口、未知格式或恢复阻断 | [诊断](../operations/diagnostics.md)、[用户排障](../user/troubleshooting.md) |

端口已占用时先核对所属实例，换用明确空闲端口并同步查询地址。数据库准入失败保留原 runtime 和回滚日志，不通过删除数据获得一次空实例启动成功。

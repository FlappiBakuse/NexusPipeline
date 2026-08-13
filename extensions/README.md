# NexusPipeline 外部插件开发指南

本目录存放随发布附带的专用插件工程（`BetterGIAdapter` / `March7thAssistantAdapter` / `ZenlessZoneZeroOneDragonAdapter` / `MaaEndAdapter`）。
插件以 DLL 形式放入主程序 `plugins/` 目录，启动时自动发现加载。

## 工程模板（四个插件已对齐）

每个插件工程为一个 SDK 风格类库，csproj 统一结构：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>YourPluginAdapter</AssemblyName>
    <RootNamespace>YourPluginAdapter</RootNamespace>
    <Version>0.1.0</Version>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\NexusPipeline.csproj" />
  </ItemGroup>

</Project>
```

- 通过 `ProjectReference` 引用主工程（契约类为 public，见下）；
- 构建产物只发布 DLL（`dotnet publish -c Release -p:PublishSingleFile=false`），由 `build.cmd` 复制到 `release/plugins/`；
- 各工程根目录的 `bin/`、`obj/` 为构建残留（gitignore 已覆盖），提交前清理。

## 契约（public，主工程 `src/Plugins/IPlugin.cs`）

| 接口/类 | 说明 |
|---|---|
| `IPlugin` | 元数据（Name/DisplayName/Description/Version/IsBuiltIn）+ 生命周期（Initialize/Shutdown） |
| `ISpecializedScriptPlugin : IPlugin` | 专项脚本适配：`Resolve(rootPath)` 推导主程序/参数/配置/日志与判断脚本（可选完成标志）；`GameName` 提供中文游戏名（脚本卡片徽章显示，不写入主程序） |
| `ScriptProfile` | `Resolve` 返回的配置快照（MainExe/Args/ConfigPath/LogPath/SuccessMarkers/JudgeScript/ConfigTemplate） |
| `INotifyChannel` | 通知通道：`NotifyScriptAsync` / `NotifyQueueAsync`（多通道并存，单通道异常隔离） |
| `PluginContext` | 插件与宿主的唯一交互入口（见下） |

## PluginContext 能力

- `Log(message)`：写入管理器日志；
- `Settings` / `ReloadSettings()`：读取/重载宿主设置；
- `Resolve<T>()`：从宿主组合根容器解析服务；
- **插件级配置（v0.5.1+）**：`GetConfig<T>()` / `SetConfig<T>()` 落盘 `config/plugins/<插件名>.json`（PascalCase、原子写入）；`GetSecret(key)` / `SetSecret(key, value)` 密钥走 DPAPI（`enc:` 前缀，与普通配置同文件，value 为空 = 清除）。

> 插件**不要**直接引用宿主内部实现（`RuntimeContext` 等 internal 类型）。

## 编写与构建

1. 新建工程：复制任一现有插件工程，修改命名空间/程序集名，实现 `IPlugin`（或 `ISpecializedScriptPlugin`）；
2. 构建：主工程 `build.cmd` 会顺带发布全部 `extensions/` 插件到 `release/plugins/`；
3. 验证：`release/plugins/` 下 DLL 随主程序启动自动加载（`/api/status` 的 `plugins` 列表可见；专用插件在新建脚本选择卡片层出现「新建{DisplayName}专项脚本实例」）。

## 打包与发布

- 发布 zip 含 `plugins/` 目录（四个内置插件 DLL），随主程序整体拷贝部署；
- 插件 DLL 可删可替换（外部插件默认启用，显式禁用记入设置 `DisabledPlugins`）。

## 专项插件一览

| 插件 | 游戏 | 主程序/启动参数 | 配置/日志 | 判定要点 |
|---|---|---|---|---|
| BetterGIAdapter | 原神 | `BetterGI.exe --startOneDragon` | `User\OneDragon\NexusPipeline.json` / `log\better-genshin-impact.log` | 「一条龙和配置组任务结束」结束关键字 + 失败任务改写 `TaskEnabledList` 选择性重试（+ 清空 NextTaskId） |
| March7thAssistantAdapter | 崩坏：星穹铁道 | `March7th Launcher.exe`（编辑配置）+ `.\March7th Assistant.exe` 显式相对路径（运行时启动目标） | `config.yaml` / `logs\{YYYY-MM-DD}.log` | 判断脚本：「游戏终止：StarRail」marker + 任务级失败提示行 |
| ZenlessZoneZeroOneDragonAdapter | 绝区零 | `OneDragon-Launcher.exe -o -c` | `config/` / `.log\log.txt` | 「关闭游戏成功/暂停运行」结束 + 「指令[ X ] 执行失败」提取（成功 + notifyText） |
| MaaEndAdapter | 明日方舟：终末地 | `MaaEnd.exe --autostart --quit-after-run` | `config/`（`mxu-MaaEnd.json`）/ `debug\{YYYY-MM-DD}-*.log` | 最后一个启用任务「任务完成/失败: X」判定行收尾 + 失败任务改写配置选择性重试（无运行记录机制） |

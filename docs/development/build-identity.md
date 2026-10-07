# 构建身份契约

Host 与桌面载荷使用一份冻结输入身份。`tools/build_identity.py` 负责生成和验证规范记录，`src/Modules/Updates/ApplicationBuildIdentity.cs` 负责 C# 读取和重新计算。结构定义为仓内的 `tools/schemas/build-inputs.schema.json` 与 `desktop-build.schema.json`，读取器还须检查跨字段一致性。

`buildInputs` 固定 Host source/tree、Plugins source、构建控制器 SHA、两个 npm lock 的字节摘要、Electron 精确版本和原始下载包摘要，以及实际工具链。安装代际为 `g0170`，RID 为 `win-x64`，bootstrap 协议为整数 1。版本使用受限 Nexus 格式；字段集合必须精确匹配 schema。

产品版本以 `src/NexusPipeline.csproj` 的 `Version` 为真源。桌面 package 与 lock 的根版本必须与之相同，冲突在创建输出或下载前拒绝。同代补丁使用各自的实际版本，安装代际继续保持 `g0170`。

当前 `buildInputs.schemaVersion=2` 另固定 `runtimeProfileId`、`runtimeProfileSha256` 与 `runtimeInventorySha256`；后两个字段分别摘要 `desktop/runtime-profile.json` 和 `runtime-files.json` 的原始字节。payload 的 desktop 投影与 Host 内嵌清单逐项核对这三个字段。Python、C#、Electron 和 schema 仅接受完整的旧格式 1 或完整的 profile 格式 2，旧格式不能携带新字段，新格式不能缺失字段。身份记录和 payload 的外层 schema 仍为 1。

`frontendHash` 是最终 `index.html` 原始字节的 SHA256，不做换行、BOM 或 HTML 序列化修正。`buildId` 是规范 `buildInputs` 字节的 SHA256，二者均为小写 64 位十六进制。HTTP 投影名称分别是 `frontendBuildId` 与 `desktopBuildId`。先冻结前端字节，再生成身份记录；输出 EXE、ASAR、payload manifest、run ID、时间和绝对路径不进入冻结输入。

规范 JSON 递归按 ASCII ordinal 键排序，以紧密逗号和冒号连接对象。字符串只接受可打印 ASCII，只转义双引号和反斜线；整数使用最短非负十进制表示。编码为 UTF-8，无 BOM、无末尾换行。解析拒绝重复键、未知字段、数组、布尔、null、浮点、负数和不可打印字符；身份记录最多 64 KiB，整数不超过 JavaScript 安全整数范围。

身份记录包含 schema/product/version/generation/RID、buildId、frontendHash、bootstrap 协议及完整 buildInputs。读取器重新计算 buildId，并验证所有顶层投影与输入一致。Host 内嵌记录与 ASAR 根的 `/desktop-build.json` 必须消费同一份生成字节。它标识输入，实际程序文件的摘要由外层载荷清单另行验证。

在外部构建目录冻结真实输入后，可执行：

```text
python tools/build_identity.py generate --inputs <实际冻结输入.json> --output <新的外部desktop-build.json>
python tools/build_identity.py verify --record <实际desktop-build.json>
```

生成器拒绝覆盖已有输出。来源字段必须来自实际源码与锁定工具；记录通过结构校验并不证明来源可信、产品已构建或发行验收通过。正式候选来源、资产摘要与服务端资格继续按[发行流程](release.md)核验。

本地未提交构建仍固定基线 HEAD，并从实际工作树文件及 Git 文本属性计算 sourceTreeSha，不改写真实暂存区或生成提交。源码原始字节指纹、未提交状态与对端指纹另外进入本地运行及验收报告；这些产物仅为本地诊断。正式候选必须来自干净提交，其冻结 sourceTreeSha、partnerSha 与 workflowSha 还须和可信候选来源逐项相等。

共享合成向量位于 `tests/fixtures/build-identity/`，仅用于跨语言规范字节及摘要一致性验证。其 `0.0.0` 工具版本与合成 SHA 不可用作生产输入。C# 用例进入 `host.backend.updates-restart`，Python 用例进入 `host.release-contract`，范围选择器将规则、schema 和向量同时映射到两类验证；预算、原生报告和清理要求保持[测试命令](../testing/commands.md)的规则。

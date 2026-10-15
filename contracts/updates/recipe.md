# 应用传输配方

[recipe.schema.json](recipe.schema.json) 定义包内 `package-info/recipe.json` 的字段结构。Python 工具 `tools/update_recipe.py` 和 Host `UpdateRecipe` 还校验路径、集合、尺寸总预算、目标身份及精确来源关系。JSON Schema 不替代这些语义检查。

配方 schema 1 与旧独立 target manifest schema 1 是不同协议。配方要求 reader 2、payload manifest 2、g0170 无业务存储变化路线。共同应用包含与 Host 完全相同字节的 `NexusPipeline Update.exe`；安装专属卸载工具不在共同应用配方中。路径不允许进入插件、账号、配置或运行数据。

`includedFiles` 包含本包携带的应用文件及目标 payload manifest；`reusedFiles` 只列精确基线中与目标字节一致的文件；`removedFiles` 描述基线中目标不再包含的文件。三组路径无大小写冲突，目标集合不得同时包含文件及其子路径。included 与 reused 的并集必须等于目标清单加清单本身。清单不列自身摘要；配方不列 ZIP 自身摘要。

基线固定 release tag、release ID、asset ID、资产 SHA256 及基线清单 SHA256。来源须不早于 0.17.3 且严格低于目标；版本比较不能替代精确清单匹配。Portable 不含复用或删除；无可验证基线的 Update 可以携带完整目标，但不得宣称具备手动增量覆盖资格。

`bundledFiles` 独立列出 Portable 中 EmulatorSupport 和 LiveScreenshot 的预装文件。Update 中此集合必须为空。预装文件不属于共同应用树；自更新验证其字节但不复制到 staging 或已有插件目录。

归档仅允许 included、bundled 文件、配方和中英文覆盖说明。生成过程中重新核验源文件摘要；重复项、链接、特殊文件、压缩比和尺寸超限均拒绝。接收方先验证整个发行资产，再读取配方，复用文件逐项校验后写入新 staging，最终由完整应用校验器验证构建身份。传输错误或合法差异包的基线不匹配允许一次完整包回退，摘要与路径错误不允许回退掩盖。

`manualCover.allowed` 只有精确基线、无应用删除、无业务迁移且要求启动后可信归属核验时才可以为 true。它不把手动复制变成可回滚事务，也不授权仅凭本地清单刷新安装登记。

v2 发现对象包含 `schemaVersion=2` 和 `channels`，每渠道保留 previousPublishedTarget/pendingTarget。非空目标包含 releaseTag、readerProtocol、requiredCapabilities、assets 和可空 basePayloadManifestSha256；assets 的 setup/portable/update 名称分别固定为 `NexusPipeline-v<V>-win-x64-Setup.exe`、`-Portable.zip`、`-Update.zip`。基线提示仅决定下载选择，不授权复用。Release 分页回读的资产必须精确为这三项。

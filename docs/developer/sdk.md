# SDK 导出与身份

公共 SDK 为 Extensibility 0.1.0；Host 和官方插件产品版本独立管理。
`contracts/schemas` 是唯一 schema 权威，`contracts/sdk-exports.json` 将每份输入明确归为插件、控制面、内部或共享定义。
新增未分类 schema、未登记引用或未知动态引用会拒绝生成。

插件导出包含显式根及其精确引用闭包。共享片段可来自内部文档，镜像只保留必要的定义或属性；
片段文档根使用 `not: {}` 拒绝整文档输入，不因此公开内部 plan、journal、数据库或恢复 DTO。
公共 C#/TS 消息来自这一闭包；其余 wire DTO 在 Platform 的 `Serialization.Protocol` 生成。
Core 不引用这两种 wire DTO，控制面类型不默认成为插件 API。

兼容身份由公共 schema 语义、UI ABI、实际 .NET 公共 API/ABI 描述和实际 TypeScript 声明共同计算。
无语义的 schema 注释、生成器实现注释或内部格式变化不改变公共兼容身份。
真实公共字段、接口、约束、常量、布局、签名及必要属性的变化必须被识别。
`SdkIdentity.ContractFingerprint` 自身值不再次参与计算，避免自引用。

现有 Boundaries 工具用 `System.Reflection.Metadata` 读取实际 SDK DLL，不加载或执行它。
`contracts/sdk-public-api.json` 记录实际描述；前端使用固定 TypeScript 编译器读取实际输出声明，
对应 `contracts/sdk-frontend-api.json`。描述不能从手写类型清单或源码字符串猜测。
这些描述不证明任意方法体的行为等价；完整源码、产物摘要及实际消费者验证仍须分别保留。

`contracts/source-fingerprint.json` 记录完整契约、SDK 和生成工具的来源指纹。
构建仍保存整个源码快照、逐文件清单和二进制指纹；消费者锁定实际包摘要。
来源指纹变化与公共不兼容不是同一判定，不能仅因内部源码变化拒绝插件。

公共 API 变更后执行 `python -B tooling/sdk_refresh.py --output <仓外生成工作区>`。
工具先生成当前形状，冻结源码，仅编译 Extensibility 和现有 Boundaries 工具，
再从真实程序集和锁定 TypeScript 编译器输出生成两份 API 描述，最后重算契约身份。
工作区及依赖可在同一拥有期复用，源码和日志保留来源回执；未知改动拒绝覆盖。
`GENERATED` 仅表示生成完成，不代表 SDK 包消费者或产品验收。
随后执行 `contract_codegen.py --check`、契约检查、正式构建和消费者验证。

正式构建及 NuGet 打包核对实际程序集描述；前端 `npm run build` 与 `prepack` 核对实际输出声明。
后端 SDK 打包接受只编译 Extensibility 与 Boundaries 的定向构建收据，并要求实际 API 核验通过、当前源码与冻结导出逐字节一致；不要求构建桌面应用。产品验收仍单独构建完整软件。
平铺消费者构建槽必须显式指定同一冻结源码根，并校验 SDK 源码和固定依赖锁。
普通包消费者只读取发布的 dist 与固定 API，不依赖仓外私有工具。
最后编译、运行实际独立消费者，并重新验证受公共变化影响的正式加载和不兼容拒绝路径。

编译任务的有限重复由 `task.repetitionLimit` 声明，观察返回 `completedRepetitions`；本次续跑起点由 Host 在观察请求和执行项目 selection 的 `repetitionOffsets` 中提供。worker 必须核对任务身份、原总次数和已完成次数，仅执行剩余次数，并保留原绝对次数编号。

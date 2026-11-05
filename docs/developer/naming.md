# 手写代码命名

手写源文件、类型、成员方法及测试方法通常不超过三个英文词。
确实需要介词表达关系时最多四词，并逐项登记；四词不是自动豁免。
Async、Tests、Fixture、Is、Has、Can 都计词；标准接口 I 前缀单列。
PascalCase、camelCase、snake_case 和连字符采用相同口径。
HTTP、JSON、SQL 等标准缩写及 BetterGi、MuMu 等真实品牌保持原拼写，不造缩写绕过规则。
目录和命名空间表达上下文，方法表达核心动作；完整测试场景放参数、docstring 或 DisplayName。

`tooling/source_checks.py` 检查手写文件和 Python AST 声明。
Web 构建调用 `tooling/frontend_names.mjs`，通过 TypeScript AST 和 Vue SFC script 检查手写类型、方法、普通函数及局部函数声明。
前端固定平台签名按 `tooling/frontend-exceptions.json` 的确切文件、名称、类别与数量核验；生成集合来自现役清单。
构建时 `tooling/boundaries/SourceNames.cs` 用 C# 语法树检查类型、方法和局部函数。
生成输出只按现役生成器的确切输出集合排除，生成字节仍须匹配生成器。
`tooling/naming-exceptions.json` 索引具体例外清单：每项限定文件、类型或方法、名称、数量和原因。
例外缺失或声明数量变化均使构建失败，不允许目录级手写豁免。

SDK 0.1.0 的已冻结接口、系统接口实现、override 和 Windows native entrypoint 保留固定名称。
SDK 同时由已编译公共表面清单核验。两个超长 SDK 文件名与其冻结接口对应。
内部四词例外仅保留必要的时间、来源或事务拥有关系；不接受超过四词的自由内部命名。
JavaScript 桌面入口同样进入前端声明检查；外部协议字段与公开 ABI 仍由契约检查核验。

测试身份始终是完整命名空间、类和方法。Theory 的每个原生用例保留独立身份及数量。
构建对账编译元数据与全部现役 policy；运行对账实际 TRX，拒绝零用例、意外 skip、缺报告和超选。
不同类可有同名短方法，选择必须精确，不依赖末端方法名全局唯一。
desktop bundle 条件及无 Job 的原生 vstest 入口依照精确 FQN 同步维护。
历史收据保持原字节，通过本轮符号映射关联，不改写旧报告冒充新执行。

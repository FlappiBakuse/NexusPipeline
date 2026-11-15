# 手写代码命名

手写文件和声明最多四个英文词；含检查器完整介词词元时最多五词，六词超限。PascalCase、camelCase、snake_case 和连字符使用相同分词口径，标准接口 I 前缀单列。`Async`、`Tests`、`Fixture`、`Is`、`Has`、`Can` 都计词。

介词按显式词表匹配，例如 `within` 命中，`input` 中的 `in` 不命中。HTTP、JSON、SQL 等标准缩写与 MuMu 等品牌沿检查器现有拆分处理，不造缩写规避规则。

目录和命名空间表达上下文，方法表达核心动作；测试场景写在参数、docstring 或 DisplayName。注释说明非显然原因。

## 代码权威与例外

| 范围 | 检查器 |
|---|---|
| 文件、Python AST 声明 | [source_names.py](../../tooling/source_names.py)、[source_checks.py](../../tooling/source_checks.py) |
| C# 类型、方法和局部函数 | [SourceNames.cs](../../tooling/boundaries/SourceNames.cs) |
| TS/Vue/桌面声明 | [frontend_names.mjs](../../tooling/frontend_names.mjs) |

生成文件只按现役 producer 的精确输出集合排除，字节仍需匹配生成器。固定 SDK/系统签名、override 和 native entrypoint 使用逐项例外，索引为 [naming-exceptions.json](../../tooling/naming-exceptions.json)，前端为 [frontend-exceptions.json](../../tooling/frontend-exceptions.json)。例外限定文件、名称、类别、数量与原因；不设整目录手写豁免。

## 测试身份

选择测试使用完整命名空间、类和方法，以实际原生发现与 runner 参数为准。保留零选择、失败、异常 skip、取消、超时与缺报告检查；不维护第二份全量方法/参数数量清单。原始历史回执保持自己的身份。

执行入口见[验证规范](validation.md)，文件行数边界由 [AGENTS](../../AGENTS.md) 和现役 source checker 维护。

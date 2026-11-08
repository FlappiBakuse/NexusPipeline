# 最小实现对应的统一定向验证

**执行时机：本次选定任务在两仓的代码、必要断言和文档先统一完成，再执行本页定义的验证计划。** 不在每张任务卡后运行一次 lint/typecheck/test/build，也不以“保险”为理由默认跑全仓或全部插件。

本页定义职责与当前可用命令。任务卡决定本次选择；以下命令是语法与路径示例，不是要求整页逐条执行的清单。工具分层任务完成后，只微调实际变化的入口，不提前把不存在的 `nexus lint` 或 `nexus typecheck` 写成可用命令。

## 四类结果

| 类别 | 回答的问题 | 应包含 | 不计入这里 |
|---|---|---|---|
| lint | 代码与依赖规则是否满足 | 格式、必要命名/边界、生成一致性、文档链接 | 私有结构的“产品测试覆盖” |
| typecheck | 类型和实际消费者是否一致 | TS/Vue 类型、C# 编译诊断、受影响公开 SDK 消费者 | 自造 JSON 字面量往返的业务 PASS |
| test | 哪个产品或逻辑假设成立 | 真实输入、输出、持久状态、授权副作用和容量边界 | 索引名、对象引用、DOM 个数、内部写次数固定 |
| build | 是否形成当前输入的可运行产物 | 必要编译、资源、worker/桌面/安装闭包与清单 | 未运行的业务验收、方法数量对账 |

四类职责不等于四个 CI job，也不要求 C# 为 typecheck 和 build 编译两次。同一次实际编译可产生类型诊断与产物，分别归类；测试所需编译作为 preparation 记录，不能被算成测试已通过。

## 先选假设，再选层

有效断言应在实现替换、用户可见结果不变时继续通过。保留不能错跑、不能串账号、不能丢配置、不能重复发送、不能越权、不能因大历史无限读取等假设。

| 本次影响 | 最低有效证明 | 何时才增加更高层 |
|---|---|---|
| 纯重试/同步规则 | 当前生产规则与独立输入向量 | 规则是否被调用是本次缺陷的一部分时，补一个实际消费链 |
| 文件/SQL 恢复或大库删除 | 隔离原生资源与真实持久入口 | 不用全平台矩阵代替该拥有权边界 |
| UI 表单误提交或深链 | 真实组件组合、真实路由/身份 | 原生窗口或桌面桥确实参与时才增加 Electron |
| 专项插件入口、日志或修复 | 固定上游向量与实际 adapter | Host 消费诊断/授权/修复时补原包集成 |
| 公共 SDK/元素 ABI | 实际受影响消费者 typecheck 与行为 | 没有 ABI 改变不自动扩大到全部插件 |
| 启动与安装入口 | 同源实际 Windows 产品、所改入口 | 只有改到更新/卸载等路径才补相应原生场景 |
| 文档 | 受影响链接、真实命令与状态一致 | 不构建产品来证明文字修正 |

外部发送次数、当前页请求量等若直接表达“不能重复发送”“读取必须有界”，可以是产品断言。应删除的是无产品依据的内部步骤精确次数，不是所有数量断言。

## 当前命令：路径与源码身份

从 Host 仓库根执行，以下变量换成本机明确路径：

```powershell
$NexusBuildRoot = 'D:\Projects\Temp\nexus-validation\host-test'
$NexusReportRoot = 'D:\Projects\Temp\nexus-validation\reports'
```

使用已拥有且可复核的构建槽复用依赖；报告使用本次新目录。不是本轮拥有的非空目录不能当作输出。源码身份由现役工具读取实际字节；未提交源码为 local-uncommitted，参考 HEAD 单列。

### lint

现有源码/契约静态入口：

```powershell
python -B tooling/nexus.py check --scope source
python -B tooling/nexus.py check --scope contracts
```

按改动选择，契约完全未变不为了凑四项而重复跑契约生成检查。schema 或生成权威确实变更时，代码阶段使用 canonical generator 更新必要源文件，再在统一阶段检查一致性。不能通过修改生成物或旧指纹让验证阶段悄悄改变源码。

现役 AST/程序集边界与生成检查需要的工具可复用一次编译。旧 build 中耦合的检查按本轮分层任务迁出或分别报告；不临时禁掉真实依赖保护来获得绿色构建。

### typecheck 与必要准备

Web 的依赖准备入口只准备锁定工作区，不运行 Vite 或 vue-tsc：

```powershell
python -B tooling/web_build.py --prepare-only --output $NexusBuildRoot
$NexusWebProject = Join-Path $NexusBuildRoot 'web-workspace\apps\web'
Push-Location $NexusWebProject
try {
    node .\node_modules\vue-tsc\bin\vue-tsc.js --noEmit
} finally {
    Pop-Location
}
```

只在本次需要 Web 类型或组件依赖时选择。工作区属于当前冻结源码，不能借用另一个 checkout 的 node_modules 后冒称同源。前端 AST 命令和浏览器参数可查[现有参数参考](frontend-testing.md)；其旧“完整验收”段落不扩大本页选定范围。

C# 可按实际项目名选择闭包，例如纯 Core 改动的编译形状：

```powershell
python -B tooling/nexus.py build --profile test --project NexusPipeline.Core --output $NexusBuildRoot
```

`--project` 接受项目名，不是 csproj 路径。若后面的已选原生 test 会准备同一闭包，就由它准备一次，不先重复执行此示例。公开 SDK 未变时，不默认重建所有独立消费者。

只需 SDK 编译/打包输入时，现有 `sdk-packages --compile-only` 会产生真实 SDK 和独立消费者的编译结果，可供 Plugins 的现役导入流程使用。它的 `status=PASS` 只表示该已选范围通过；`validationMode=compile-only` 下 runtimeConsumers 与 lifecycle 仍为 NOT_RUN。不要为取得导入资格无条件补运行时消费者或插件生命周期；只有选中了相应产品假设才执行。依据：[SDK 输出范围](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/tooling/sdk_packages.py#L129-L146)、[现役 Plugins 导入](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/5f88763ca4f49836b0bead262dad4acfacaebb63/tooling/import_sdk.py#L18-L40)。

### test

先合并任务卡所需的产品假设与现有方法，再选择现役 group/method。下面只演示已存在的定向方法语法，不表示所有变更都需要预算测试：

```powershell
python -B tooling/nexus.py test --group core --method NexusPipeline.CoreTests.RetryBudgetTests.KeepsOriginalDeadline --build-root $NexusBuildRoot --output $NexusReportRoot
```

原生分组支持重复 `--method`，把同组所选方法一次提交；Python 分组当前接受一个完整方法 ID。以当前命令实现的选择能力为准，不为凑方法数量补无价值用例。登记简化后用实际发现和稳定产品 ID 保证执行范围，不维护第二套全量方法数量权威。

Web 当前 suite 可显式选择，例如 `automation`、`account-summary`、`history-formatting`；完整语法在[前端参数参考](frontend-testing.md)。选择真实相关 suite 或必要单场景，不默认运行全部 controls、所有浏览器和所有 Host 场景。没有 `nexus test --group web`，不要虚构该入口。

契约/fixture、真实集成、原生产品与真实上游分别记录。loopback 合成登录页通过不证明官方网页登录可用；adapter 纯函数通过不证明 Host 修复入口已接通。

### build

需要交付客户端或原生行为验收时，使用同一冻结源码和实际当前输入：

```powershell
python -B tooling/nexus.py product-publish --build-root $NexusBuildRoot --output 'D:\Projects\Temp\nexus-validation\product' --runtime-archive '<本机锁定 Electron ZIP 的绝对路径>' --profile test
python -B tooling/nexus.py product-assemble --publication-root '<本次实际 publicationRoot>' --output 'D:\Projects\Temp\nexus-validation\application' --bundle-root '<本次已验证插件 bundle 的绝对路径>'
```

尖括号是必须替换的显式输入，publicationRoot 从真实回执取得，不猜最新目录。完整产品任务需要实际软件；纯规则或文档改动不自动 publish 整个产品。Test 与 Production 结果分开；只有权限清单、发行入口或对应交付任务需要时补生产范围，不能借本地 Test 信任执行正式发布。

## 结果记录与停止条件

每个选定检查记录源码/输入身份、命令、退出码、实际范围、原始输出和结论。PASS 只适用于该项实际执行范围；lint/typecheck/build 成功不等于产品假设已验收。未运行写 NOT_RUN；前置条件缺失写 BLOCKED。取消、缺报告、非预期跳过、零选择与超时不能填 PASS。

失败保留原始结果，修根因后只补失败项及本次修复真正影响的依赖；已验证且输入未变的无关范围不重复跑。任务卡目标已被最低有效证据覆盖后停止，不追加“再保险”测试、长压力或真实游戏。

本轮文档中的 5,296 是审查时静态声明用例数，历史 1,298 是当时专项局部执行结果；它们都不能成为新代码的验收计数或本轮 PASS。

源码快照也包含docs。代码阶段先定稿业务说明，统一验证结果默认写仓外；若随后只改文档，单列docs-only delta并保留实际产物的冻结身份，不把旧回执说成新全树通过。纯文档修订不触发无理由的业务重跑；需要纳入新产物时只准备实际必要的产物闭包，并按真实工具规则复用可用证据。

命令依据：[nexus.py](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/tooling/nexus.py)、[项目选择](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/tooling/build_product.py#L99-L132)、[Web 准备与类型检查](https://github.com/FlappiBakuse/NexusPipeline/blob/0bb4d144d92805a2d489c65d0be3b7527c7810fd/tooling/web_build.py#L73-L113)。

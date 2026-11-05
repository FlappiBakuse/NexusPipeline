# 选定项目编译

`ExtensionActivationContext.Compilers` 注册只读项目编译器。managed 清单同时声明 `source.compile`、`managed.compilers` 和 `selected-script-root` 文件权限。
data-specialized 清单通过 `specialized.compiler` 声明其他插件的 extensionId/compilerId，并声明相同的选定根读取权限。
清单声明不提供任意根路径；同一插件不能把自己声明成外部 compiler 依赖。

`ProjectCompilerRegistry` 只解析已发布激活。注册跟随实际 scope 清理，回调执行期间保持 use；撤销先取消并排空回调，再释放注册。
`InstalledCompilerPort` 复用安装消费作用域，在回调前后复核实际安装和权限，并把原调用及激活取消传给 provider。
`IProjectCompiler.DiscoverAsync` 返回 provider 自己的选择 JSON；`CompileAsync` 返回完整内容图及有界执行 JSON，普通 JSON 不授予源访问权限。

`SelectedProjectSource` 从现役 binding、脚本和 Script root 取得实际根，冻结相关定义和物理身份。
只读文件独立持有物理 Reference claim、原文件及父目录句柄；返回给 compiler 的缓冲区与 Host 原字节分离。
目录读取保留完整有界成员及物理身份；缺失文件和缺失父目录同样保留负读观察，防止新增文件改变回退结果。
冻结及发布前的 `RequireCurrent` 复核定义、目录、存在性、原生身份和已持有文件。
每次读取继续复核当前调用权限；同一捕获过程中复用目录观察，完整核验在封存和使用边界进行。

源码最多 128 文件，单文件 8 MiB、总计 32 MiB；最多 128 个目录观察和合计 4096 个目录成员。
禁止链接、越界路径、大小写别名和不存在的调用授权。`SelectedSourcePort` 在回调结束后关闭，不能由插件保留并继续读取。
封存内容使用现役 `ImmutableBlobStore` 的 stream 入口，完整来源不受普通 JSON 配置的 2 MiB 限额替代。
`CaptureResource` 从同一选定根交接 native resource 子树，交付现役 `FileTreeArchive` 与 `TreeReadLease`；整树与声明子集共用同一归档形态。
每次捕获仍受原文件树的 4096 项预算和实际物理 Reference claims 限制；至多 8 棵树、累计最多 1 GiB，不占编译源码的 128 文件配额。
原树字节及物理身份保留，源文件和归档 blob 均持有只读句柄；后续核验重扫同一范围并拒绝新增文件，整树归档重扫整棵树，子集归档只重扫声明成员及其祖先目录。
`CompilationResult.ResourceTrees` 给出当前编译所需的选定根内相对路径；Host 从实际选定根独立捕获，目录观察不授予 native 运行资格。
`ResourceTrees[].IncludedPaths` 让编译器只声明树内实际需要的成员：1–128 个互不重叠的精确相对路径，重复、大小写别名、越界路径和嵌套在另一项之下的值均拒绝。
null 表示整棵树，且只对非空树根有效；Host 只归档这些路径及其祖先目录的并集，归档内出现声明之外的文件或目录、或声明成员实际缺失时拒绝整份结果。

`ProjectConfigMounts` 将同一 binding 已声明并保存的账号配置映射到选定项目内的原相对位置。
文件和目录均从不可变修订及 blob 读取；当前脚本配置即使包含另一个账号的不同字节，也不能替代该保存内容。
保存目录中缺失的成员继续返回缺失，不回退当前磁盘。整份保存修订保持 Reference 预留，文件标记与实际读集绑定原 binding/配置修订、契约和 SHA。
普通项目文件与已保存配置共用同一 128 文件/32 MiB 源码预算；独立返回缓冲区不会改动持有的原字节。
当前映射只接受声明在同一个 Script root 内的配置路径；外部配置根没有隐含别名。
源码端口提供保存树的虚拟目录及 `Configurations` 身份，绑定配置与契约修订、快照 SHA、形状和实例选择；选择 JSON 不能声称配置已挂载。

`CompilationResult` 通过 RootIndex、Assets、ExecutionView 交接内容；每份 `CompiledAsset` 给出名字、Artifact/Index/Page 种类、完整字节及本次图内引用。
Host 独立复制原字节、计算 SHA，验证唯一精确名字、闭合无环图和无孤儿内容；编译器自报的内容不会成为路径或权限。
整图最多 32 MiB；最多 8 个 Artifact、8 个 Index、4096 个 Page。Index 最多 512 KiB，Page 最多 64 KiB，执行 JSON 最多 256 KiB。
完整 Artifact 作为独立内容保留，执行侧不需要一次展开所有 schema 页面。持久内容继续由同一 blob owner 验证。

`CompilationResult.PreviewIndex` 单独声明可供浏览器读取的公开子图；null 不公开任何预览。
公开根必须为 Index，从该根可达的全部资产只接受 Index/Page，引用 Artifact 时拒绝整个声明。
完整 RootIndex 与私有 Index/Page 不因此公开，完整图继续进入冻结计划和私有编译读取。
插件负责为公开根生成适合普通页面展示的投影，不把秘密、原配置快照或私有执行内容放入公开子图。

`ProjectPreviewService` 将保存选择重新编译为不可变预览，最多同时保留八份、每份两分钟。
每次分页核对当前调用、账号绑定、脚本/根修订、物理根身份、两份实际安装和保存配置读集。
响应只列公开子图成员；分页返回精确原字节的 Base64 和原 SHA，避免 JSON 重新编码改变内容身份。
预览持有 blob Reference，释放、过期或客户端寿命结束后由同一 owner 清理。
客户端已撤销时立即拒绝读取，后台清扫最多五秒释放其缓存引用。

Web、CLI、MCP 通过 `ProjectUseCases` 共用 context/discover/select/preview/page/release。
普通入口为 `POST /control/v1/projects/{action}`，select 要求 configuration.edit，其余要求 configuration.read。
CLI 使用 project- 前缀命令；MCP 使用 project_ 前缀并遵循现役能力策略，不额外开放配置写权限。
公共 `FrontendTransport.project` 通过实际插件激活和客户端 grant 进入绑定编辑表面。
Host 从实际 binding 和已安装 consumer manifest 解析 compiler，不接受浏览器自报 consumer/provider 权限。
实例、表面、绑定修订及操作身份均独立校验；预览同时绑定调用主体、客户端和 binding，不能被普通项目端口读取。

`ProjectCompilationService` 从实际安装的专项 manifest 取得 compiler 关联，复核双方安装证据、已发布 runtime、权限与当前调用。
同一个使用作用域内冻结完整内容图、源码、配置读集和独立资源归档，并保存完整 blob 闭包；保存配置与原生资源树重叠时拒绝读取当前磁盘替代账号内容。
发布回调持有双方安装和资源预留；回调前后复核配置、实际来源和激活，结束后不能继续使用其当前性证明。
源码和冻结资产的逐次读取复核调用、租约及激活存活；完整来源重扫保留在封存和发布边界，不在每个分页读取时重复扫描原树。
任务消息通过独立 `compiled.fingerprint/canExecute/view` 绑定实际 Host 快照，不改变原配置资源的内容、值或 CAS 身份。
资源的 `relativePath` 来自实际声明及冻结历史，目录内相同字节的其他文件不会因此取得主文件写权限。
`CompilationResult.CanExecute=false` 保留完整只读产物供发现和配置修复；计划捕获与历史计划绑定独立拒绝其执行。
Jint 的 `nexusReadCompiled(assetName,jsonPointer)` 只读取该快照声明的资产；每次最多 256 KiB，累计 2 MiB、128 次，使用结束后关闭。
JSON pointer 使用精确对象键与规范数组下标，拒绝缺失、错误转义和未绑定资产；响应消息不能回传 `compiled`。
`ProjectChoiceStore` 将每个 binding 的明确来源选择保存为独立配置修订，绑定 compiler 清单及脚本/根修订；保存遵循原有 CAS 和幂等操作身份。
`UseSavedAsync` 读取保存选择，持有其不可变快照及 Reference 预留，并把选择修订加入编译指纹、读集及完整内容闭包。
`AdapterPlanCapture` 固定 consumer/provider 两份实际安装身份；接受计划保存编译快照、独立 compiler 安装锁及精确配置读资源。
同一个受理计划已持有的配置和物理预留通过 `ProjectReadLease` 借用，其余只读引用独立获取；父预留结束后读取随即失效。
持久引用发现包含整个编译图、保存配置和原生归档的全部 blob，完整接受计划不能只保留执行摘要。
历史 observe/retry 从原接受计划重开同一内容与执行视图，当前编译器重新编译不会代替历史证据。
实际完整历史运行/GC 资格、Maa 安装与浏览器联合读取及全套 native/Agent 运行仍待完成，不能以编译、绑定 helper 或仅受理的证据替代。

## 脚本项目编辑

项目命令以 `scriptId` 与 `expectedScriptRevision` 选择脚本来源，或以 `bindingId` 与
`expectedBindingRevision` 选择账号绑定来源；两种拥有者字段互斥。脚本编译读取已声明的真实脚本根和
配置资源，不创建虚拟账号或借用绑定快照。脚本选择以独立文档修订进行 CAS 保存，操作 ID 绑定调用者、
脚本修订、编译器提供者和完整选择；原操作重放返回已保存结果。

脚本与绑定编辑区共用受认证的项目端口、编译器和有界公开预览。脚本预览不构成执行权限，
账号绑定与脚本预览的会话身份互相隔离。脚本选择保存不改绑定选择、绑定配置快照或上游原文件；
受保护输入仍由账号绑定的秘密写入用例管理，脚本命令拒绝秘密写入及关闭秘密操作请求。

纯数据专项可声明 readOnlySources 的有限 JSON/JSONC 文件闭包。SelectedProjectSource 仍限制 128 文件、8 MiB 单文件和 32 MiB 总读取；文件在现有 blob owner 冻结，进入计划后观察及重试读取该冻结集合。
声明只读资料的执行使用 128 MiB 累计分配额度，包含宿主资料读取与解释器解析；其他调用保持 32 MiB，语句、深度、2 秒超时和输入输出预算保持不变。

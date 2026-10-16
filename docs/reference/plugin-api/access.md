# 请求来源与本机交互

Plugin API 2.2 只定义 `General`、`HostFilePicker`、`NativeConfigEditor` 三类访问。每条 `PluginWebApiRoute` 必须显式设置 `Access`；声明式 UI 的读取、保存和每个动作分别显式设置访问类，缺失或未知值使注册失败。`PluginWebApiRequest.ConnectionKind` 由实际传输对端确定为 `Local`、`Remote` 或 `Unknown`，不采信 Host、Origin、Referer 或转发头。General 在三种来源均可用；后两类只允许 Local，在读取参数、请求体或执行回调前拒绝 Remote/Unknown。SDK 包及程序集继续为 2.0.0，与接口 2.2 独立治理。

Frontend API 1.7 的 `host.getCapabilities(signal?)` 返回冻结只读的 schema 1 对象：`connectionKind` 为 local/remote/unknown，`operations` 精确包含 general、hostFilePicker、nativeConfigEditor，每项有 allowed 与 denyReason。拒绝原因分别为 host_file_picker_requires_local、native_config_editor_requires_local、client_origin_unverified。缓存绑定同源、认证和当前 Host 实例；重连或重启使旧读取失效。它用于呈现界面，后端每次操作仍重新判断来源。

原生文件选择只能禁用“浏览”按钮，路径输入仍可编辑并保存。普通账号、脚本、队列、计划、运行、历史、插件安装与诊断是 General；诊断 ZIP 写入宿主受控 staging，返回受管 artifact 身份供认证下载，不接受客户端指定宿主输出路径。脚本文件读取只在已批准脚本根、配置位置及游戏程序父目录范围内，拒绝链接与路径穿越，不枚举宿主磁盘。

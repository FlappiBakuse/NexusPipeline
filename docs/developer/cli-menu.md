# CLI 管理菜单

```text
NexusPipeline.exe control menu --address http://127.0.0.1:58731
NexusPipeline.exe control menu --address https://127.0.0.1:58731 --bearer-file <凭据文件>
```

菜单提供状态与诊断、脚本/用户/队列定义、执行与调度、历史、设置/通知、插件及更新/维护入口。
定义变更使用当前 `automation-change` JSON 合同；选择或按 ID/唯一名称解析继续交给现役用例。
菜单只收集命令所需选项，然后调用同一个 `ControlCli`，经同一 Control HTTP 用例和权限执行。
没有直连数据库、任意路由或另一份业务实现。

每层 `0` 或输入结束返回；参数空行取消本次操作；写入前必须输入完整 `yes`。
菜单及退出码写 stderr。每个已执行普通命令在 stdout 输出一个现役 JSON envelope，顺序对应用户选择；
取消不产生业务请求或 envelope，坏选项和失败响应显示后可继续选择。
菜单的 JSON 输入使用文件路径；`-` 被拒绝，避免请求读完菜单后续 stdin。
非交互命令继续支持 `--input -`、原退出码、权限、operationId、单 envelope 及显式二进制输出。

菜单入口不启动 Host 或创建运行根；Host 不可达时沿现役 transport 返回失败。
秘密仍使用独立的非交互秘密命令，不在管理菜单中回显、缓存或落盘。

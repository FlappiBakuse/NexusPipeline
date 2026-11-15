# Electron 桌面模块

`main.cjs` 组合窗口、托盘与原生桥；Host 通过当前实例的签名会话提供有限原生能力。Electron 从 Host 回环地址加载同一 Web 产物。

| 主题 | 权威说明 |
|---|---|
| GUI 入口、进度、托盘与可见性 | [桌面启动](../../docs/architecture/desktop-startup.md) |
| 实际 TCP 身份、签名、退出与维护许可 | [桌面授权](../../docs/reference/desktop-authorization.md) |
| 草稿与会话结束 | [Web 状态](../../docs/reference/frontend-state.md#编辑草稿) |
| 参数、Node 与原生浏览器 fixture | [前端验证](../../docs/development/frontend-testing.md) |
| 生产桌面选择背景 | [Electron 决策](../../docs/decisions/desktop-shell.md) |

## 装配

运行时来自 [runtime-lock.json](runtime-lock.json) 固定的官方 ZIP。ASAR 构建使用锁定的官方 producer，依赖在仓外工作区准备，不通过 npm 安装 Electron 运行时。

```text
python -B tooling/nexus.py desktop-bundle --runtime-archive <锁定官方ZIP绝对路径> --output <仓外桌面目录>
```

从仓库根执行。工具核验归档、完整成员、源码及 SHA256 integrity，写出装配回执；未知成员或字节不一致时拒绝。完整软件使用[产品装配](../../docs/reference/commands.md#完整软件)，单独 bundle 的受控 fixture 验收范围另记。

`nexusDesktopChannel.prepareExit` 返回 `ready` 或 `pending-writes`。普通草稿随成功退出、重载和模式切换结束会话；准备失败保留原内容。十秒准备、Host 签名许可和托盘动作锁统一由桌面授权页维护。

Renderer 无 Node 或通用命令执行能力。原生保存选择器只授权当前绑定文档所选的准确文件；日志流式写入，取消不输出日志字节。语言、窗口位置、模式与资源释放由各自生产 owner 管理。

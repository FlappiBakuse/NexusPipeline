# 安装 NexusPipeline 0.18

0.18 使用新的实例和插件协议，不提供 0.17 数据迁移。
现有安装、配置、账号数据和运行历史保持在原目录，继续由原版本读取。

1. 正常退出旧版宿主、桌面、托盘和任务，保留整个旧目录。
2. 从正式 Release 选择 Setup 或 Portable。Setup 选择新的空目录；Portable 解压到新的空目录。
3. 启动 0.18，创建新实例，重新登记脚本、账号和配置。不要将旧数据库、journal、插件或模板复制进新实例。
4. 核对新实例后再启用绑定；需要查看旧记录时使用保留的原版本。

跨代屏障会保留更新发现结果，并阻止内置下载和原位应用。
Update 包用于受支持的同代维护流程，不能作为首次安装或跨代覆盖包。
当前软件及正式渠道状态见[使用指南](README.md)和[已知边界](../status.md)。

## Install in a new directory

Version 0.18 requires a new instance. Data migration from 0.17 is not provided.
Exit the old Host, desktop, tray and tasks normally, and preserve the complete old directory.
Install Setup into a new empty directory, or extract Portable into a new empty directory.
Create a new instance and register scripts, accounts and configurations through the application.
Keep old databases, journals, plugins and templates with their original version.
The built-in update barrier prevents cross-generation download and in-place application.
The Update asset is for supported updates within the current generation.

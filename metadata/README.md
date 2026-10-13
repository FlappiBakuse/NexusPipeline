# 发行元数据

`updates/legacy-policy.json` 是旧更新屏障的唯一编辑来源；根目录 `update-policy.json` 是提供给已发布客户端固定 URL 的真实 JSON 投影。修改 canonical 后运行 `python tools/update_metadata.py generate`，通过正常 PR 一起提交。生产检查执行字节一致性校验，禁止单独修改投影；既有屏障保留原含义和顺序。

新的更新协议定义放在 `contracts/updates/`，渠道声明放在 `updates/channels.json`。渠道声明经正常 PR 审核，实际生效取决于对应 Release 的公开状态及完整资产校验；Host publisher 不回写源码分支。

`.NET` SDK 的 `global.json` 保持工具约定位置。默认对端测试输入位于 `tests/inputs.lock.json`，独立于预装插件的版本及包字节锁定。

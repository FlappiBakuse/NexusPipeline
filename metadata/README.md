# 已发布客户端发现入口

`updates/channels.json` 保留历史发现协议。
`updates/v2/channels.json` 描述三资产发现目标，0.x Host 使用 prerelease 通道。
`updates/legacy-policy.json` 是只追加更新屏障的权威；根 `update-policy.json` 是原字节投影。
这些公开文件服务既有客户端，当前运行时继续使用自己的数据、插件和签名协议。

```text
python -B tooling/legacy_updates.py generate
python -B tooling/legacy_updates.py check --base origin/main
```

0.18 屏障和[新目录安装说明](../docs/users/install-018.md)须先经受保护 PR 合入，
再从官方固定来源回读验证，最后才允许公开 0.18 资产。
本地检查不授予候选、tag 或发布资格。

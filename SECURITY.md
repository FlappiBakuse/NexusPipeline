# 安全策略

NexusPipeline 需要管理员权限运行，并提供本地/远程 Web 管理、Bearer token、外部进程控制、配置文件交换以及关机、重启和休眠等系统操作能力。安全问题请按照本文件处理。

## 支持范围

| 版本 | 安全修复支持 |
|---|---|
| 最新 v0.8.x Pre-release | ✅ |
| `main` 分支 | ✅ |
| 其他历史版本 | ❌ |

## 报告漏洞

请通过 GitHub 的[私密漏洞报告入口](https://github.com/FlappiBakuse/NexusPipeline/security/advisories/new)提交安全问题。安全报告应包含：

- 受影响的 NexusPipeline 版本；
- Windows 版本与运行方式；
- 可复现步骤或最小复现材料；
- 影响范围与潜在后果；
- 建议的修复方向（如有）。

请勿通过公开 Issue、Pull Request 或讨论区发布尚未修复的漏洞。

## 敏感信息处理

请在提交前移除或脱敏：

- Access token、Webhook 地址与签名密钥、SMTP 授权码；
- `config/` 下的用户配置与插件配置；
- 用户账号、脚本路径和完整运行日志；
- 可用于访问外部服务或本机的凭据。

普通功能缺陷请使用 Issue 模板提交；安全问题使用私密漏洞报告入口。

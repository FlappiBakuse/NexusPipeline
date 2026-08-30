# 贡献指南（Contribution Guidelines）

NexusPipeline（枢链）欢迎协作。开发环境与调试、协作与提交规范、发布流程统一维护在 [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)；测试层级与完整命令见 [docs/TESTING.md](docs/TESTING.md)；控制面能力现状见 [docs/CONTROL_PLANE.md](docs/CONTROL_PLANE.md)；当前计划与已知问题见 [docs/STATUS.md](docs/STATUS.md)；安全问题遵循 [SECURITY.md](SECURITY.md)。

要点速览：

- 提交信息采用 Conventional Commits（type 英文 + 描述中文）；
- 未经维护者明确授权，不执行 commit、push、tag、Pull Request 或 Release；
- 运行产物、用户配置、日志、密钥和运行时数据永不进入版本库；
- 测试失败时保留失败证据并修复根因，禁止用重试或跳过掩盖失败。

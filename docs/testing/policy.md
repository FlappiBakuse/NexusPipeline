# 测试归属与政策

## 测试归属与写法

### 最低充分层级

- 参数范围、数量上限、UTF-8 字节数、ID 规则、状态机、更新自动化、重试、调度计算、配置交换、快照同步、插件 capability、历史计算和通知选择进入 xUnit（`tests/NexusPipeline.Tests/`）。
- API payload、DTO、脱敏、secret 策略、候选输入名、CLI envelope 和退出码在前端 Vitest 或组件测试验证。
- 真实解释器、进程树、Job Object、端口回退、插件安装事务、模拟器命令序列、定期更新检查和更新恢复进入 System Smoke。
- UI Smoke 保留页面加载、主导航、典型 CRUD、用户绑定设置、队列调度、设置安全入口和插件浏览等用户工作流。

### UI Smoke 配额

当前浏览器验收保留 3 个用户工作流，硬上限 12 个：

```text
tests/e2e/tests/
└── app.smoke.spec.mjs                 3
```

UI Smoke 断言用户可观察的结果和稳定业务状态，优先使用稳定的 `data-testid`、ARIA 状态和业务 ID；现有用例仍可读取 `data-action` 作为定位属性，但它不是运行时行为契约。业务行为不使用 CSS/class/style、精确像素、SVG 数量、装饰性文案、源码字符串、随机 DOM 层级或完整磁盘文件内容作为质量判断。低层已能稳定证明的每个字段、密钥、选项和 payload 不重复占用浏览器配额。

LLM 与自动化代理不得新增持久化视觉回归测试、截图基线或像素/布局断言。临时浏览器验证脚本只能放在操作系统临时目录，验证结束后删除且不得加入 Git。持久化 UI 测试只覆盖功能结果、ARIA、焦点、状态、提交、路由、API 效果和生命周期。

### Web Logic 与测试文件组织

- Web Logic 只能导入生产 ES module 的纯函数；禁止读取生产源文本、按函数名切片、正则解析函数边界或把实现字符串当作行为证据。
- `frontend/` 的 `npm run typecheck`、`npm run test` 和 `npm run build` 验证 Vue/TypeScript 组件、公共 `nxp-*` 元素和静态构建产物。
- Frontend API 1.5 的宿主外部契约由 `frontend/src/plugin-bridge/contract.test.ts` 覆盖：精确版本匹配、`host.*` 能力面（含二进制 `api.blob` / `api.upload`）、18 个公开 slot 白名单、renderer surface context 与清理、生命周期订阅与释放。
- 声明式插件表单控件由 `frontend/src/plugin-bridge/controls.test.ts` 表驱动覆盖：字段类型到公开 `nxp-*` 元素的映射、初始值与约束传递、单选与多选交互、开关载荷、必填错误投影与清理。
- 服务重启恢复协议由 `frontend/src/platform/service-restart.test.ts` 覆盖：旧实例与无关 HTTP 服务被拒绝、同端口等待新实例、端口漂移与配置端口被占用时跳转到实际监听端口、超时与重试复用交接信息。
- SSE parser 的 chunk/CRLF/multiline data/UTF-8 行为由 `frontend/src/platform/events.test.ts` 覆盖。
- 宿主路由表结构由 `frontend/src/router.test.ts` 覆盖：宿主页面路由、插件 catch-all 路由、空 fallback 与按需加载方式。
- 前端候选配置请求由 `frontend/src/features/users/utils/configEditRequest.ts` 的 `buildConfigEditRequest` 负责构造，Vitest 直接验证输入与输出。
- 宿主语言资源以 `frontend/public/i18n/` 为唯一源。
- xUnit 文件按子系统目录组织并镜像模块，例如 `Scheduling/SchedulerTests.cs`、`Plugins/PluginRepositoryCatalogTests.cs`、`Configuration/ConfigSwapSyncTests.cs` 和 `Updates/UpdateServiceTests.cs`；同一文件的保留集可包含多个紧密相关的门面测试类。
- 测试替身显式实现当前接口；接口移除默认实现后同步所有 fakes。测试不为旧接口、旧 overload、旧数据形态或历史恢复路径保留兼容断言。
- 新增回归先放在最低有效层，再评估是否保留一个高层 smoke。测试应暴露根因，不通过 retries、无条件 sleep、自动重启或跳过断言掩盖不稳定性。

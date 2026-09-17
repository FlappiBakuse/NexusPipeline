# 数据专项插件契约

## resolve.json（推导配置）

```json
{
  "inputs": [
    { "name": "config", "label": "BAAH 配置文件名", "labelKey": "input.config.label", "description": "BAAH_CONFIGS 下的配置文件名（含 .json）", "descriptionKey": "input.config.description",
      "default": "config.json", "required": true, "pattern": "^[A-Za-z0-9_\\-]+\\.json$" }
  ],
  "require": [
    { "var": "launcher", "file": "March7th Launcher.exe" },
    { "var": "assistant", "file": "March7th Assistant.exe", "searchUpward": true }
  ],
  "paths": {
    "mainExe": "{launcher}",
    "args": "{rel:assistant}",
    "configPath": "config.yaml",
    "logPath": "logs/{YYYY-MM-DD}.log"
  }
}
```

- **inputs（可选）**：用户输入变量声明，供 paths/require 模板内联引用。`name` 必须是字母开头的字母/数字/下划线且不重复；`label`/`description` 为回退文本，`labelKey`/`descriptionKey` 可引用插件 `i18n/` 资源中的展示文字；`default` 为缺省值；`required` 表示缺失（且无 default 可回退）时推导失败；`pattern` 为可选的整串正则校验。仅声明未被模板引用的输入不参与推导。宿主对所有输入值做基线净化（禁止路径分隔符、冒号、相对路径段、通配符、花括号与控制字符），防止路径拼接越界。`configPath` 模板恰好引用一个输入且输入未提供/指向的目标不存在时，宿主枚举静态目录中匹配「静态前缀 + * + 静态后缀」的**文件与子目录**作为候选（目录候选服务于实例目录型配置），目录内唯一候选时自动绑定并跟随改名，多候选不猜测；`pattern` 同时用于枚举过滤（如实例目录名 `^\d{2}$` 可排除共享数据目录）。宿主按请求语言解析 `label`/`description` 并返回输入 DTO，输入 key 必须存在于所有 locale 资源中。输入值按用户保存在绑定（`configInputs`）上，运行/编辑/校验按用户绑定解析（接管哪个配置文件属于用户选择，多用户可各自接管不同配置）；脚本实例的 `pluginInputs` 仅作为未设置绑定输入时的回退，专项实例编辑弹窗不再渲染输入表单。
- **require**：全部满足才推导成功（替代 DLL 时代的 `File.Exists` 校验）。`file` 相对脚本根目录；`var` 将匹配到的绝对路径绑定为变量；`searchUpward: true` 时根目录找不到则逐级向上搜索（最多 4 层，March7th 管理端/执行端分离场景）。
- **paths**：`mainExe` / `args` / `configPath` / `logPath` 四项，另有可选 `extraConfigPaths` 数组。
  - 占位符 `{var}` = 绑定文件绝对路径；`{rel:var}` = 相对脚本根目录的相对路径（运行时启动目标语义，同目录结果带 `.\` 前缀）。**占位符仅整体替换**：整项命中即替换为该路径，不支持路径文本内嵌入拼接（如 `C:\dir\{var}` 的模板会丢弃前缀只保留 `{var}` 解析值）；需要组合路径时请用无占位符的相对拼接。绑定占位符每项最多 1 个，且不可与 `{input:名称}` 混用。
  - 占位符 `{input:名称}` = 用户输入值**内联替换**，可与相对路径文本自由组合（如 `BAAH_CONFIGS/{input:config}`、`--config {input:config}`）；引用未声明的输入、必填输入缺失且无 default、或值未通过 pattern 校验时整体推导失败。
  - 无占位符：路径字段按相对脚本根目录拼接；`args` 原样返回（参数文本）。`logPath` 允许为空：为空表示专项脚本无专用日志文件，判定日志改由进程标准输出提供。
  - `extraConfigPaths`（可选）：附加配置文件/文件夹路径数组（相对脚本根目录，支持 `{input:名称}`）。附加路径与主配置路径一样按用户快照隔离交换（运行前快照覆盖现场、运行后与编辑提交差异入库），但**判定脚本始终不可见**——`input.files`、`replaceConfigs` 与 config-restore 只作用于主 `configPath`。适用对象是软件级配置（如 BAAH 的 `DATA/CONFIGS/software_config.json`、BetterGI 的 `User/config.json`）。快照缺失宽容：现场也不存在时保持为空，等现场生成后自动采用。
  - `mainExe` 推导后必须存在（require 覆盖或文件真实存在），否则推导失败（前端保存被拒）。

附加配置路径的运行准备与快照同步采用带 manifest 的 stage/backup/commit 事务；准备失败会回滚已处理路径并阻断本次运行，启动恢复和运行收尾会处理未提交现场，无法确认的现场保留并告警。



## 判断脚本

- 契约与通用判断脚本一致：输入 `__NEXUS_INPUT__`（JS）/ 输入 JSON 路径（Python），包含宿主当前 `locale`（规范化 BCP 47 语言标识），输出 stdout 尾行 `{"status":"success|partial|failed","reason":"…","notifyText":"…","notifyScreenshotId":"…","replaceConfigs":[…]}`；`partial` 只能由判断脚本主动返回，属于终局结果且不触发重试、不计入每日成功次数；`replaceConfigs` 仅在 `failed` 结果下为下一次重试应用。宿主在当前 profile 解析成功后将 `judgeScript` 作为本次操作的有效判断脚本，用户不可编辑（专项弹窗不渲染自定义完成标志区）。
- 语言按扩展名自动识别：`.js`（内置 Jint 引擎）/ `.py`（系统 python.exe）。



### 判断脚本只读探针

JavaScript 判断脚本可使用以下同步 API：

```js
const processes = nexus.listProcesses({ nameContains: "game" });
const windows = nexus.listWindows({ titleContains: "Game" });
const health = nexus.httpGet("https://example.com/health", {
  timeoutMs: 5000,
  maxBytes: 65536,
});
```

`listProcesses` 返回 `{ processes, truncated }`。每项包含 `pid`、`ppid`、`name` 和可为空的 `startTimeUtc`；可按 `nameContains` 或 `pid` 筛选，结果最多 2048 项。

`listWindows` 返回 `{ windows, truncated }`。每项包含字符串形式的 `hwnd`、`pid`、`name`、`title` 和 `foreground`；结果来自可见顶层窗口，可按 `titleContains` 或 `pid` 筛选，结果最多 512 项。

`httpGet` 只接受长度不超过 2048 的绝对 `http`/`https` URL，执行 GET 并返回 `{ ok, status, body, truncated, error? }`。默认超时 10 秒，单次请求最多 15 秒；响应正文默认及硬上限为 2 MiB。错误使用 `invalid_url`、`timeout` 或 `network_error`。

Python 判断脚本通过输入中的 `probeApi.endpoint` 与 `probeApi.token` 访问同一组探针。请求必须使用 `POST` 和 `X-Nexus-Judge-Token` 请求头：

```python
import json
import urllib.request

api = input_data.get("probeApi")
if api:
    def probe(path, payload):
        request = urllib.request.Request(
            api["endpoint"] + path,
            data=json.dumps(payload).encode("utf-8"),
            method="POST",
            headers={
                "Content-Type": "application/json",
                "X-Nexus-Judge-Token": api["token"],
            },
        )
        with urllib.request.urlopen(request, timeout=10) as response:
            return json.load(response)

    processes = probe("/processes", {"nameContains": "game"})
    windows = probe("/windows", {})
    health = probe("/http-probe", {"url": "https://example.com/health"})
```

探针桥接仅监听本机回环地址，令牌只在当前判断脚本调用期间有效。进程与窗口接口提供快照读取；HTTP 接口提供受限 GET 读取。脚本运行时保持文件读写、进程控制、窗口控制、命令执行和 HTTP 写入权限关闭。



### 判断脚本截图

历史详情中的受保护图片由宿主前端通过带 Bearer 认证的 Blob 请求加载，再以弹窗生命周期管理 Object URL；插件无需获得历史文件鉴权令牌。

一次「脚本实例 × 用户」运行按 Attempt 分别维护内存截图池；每个 Attempt 最多保存 8 张，第 9 张加入时移除该 Attempt 最早的一张。运行收尾时，当前保留截图会写入本轮运行的 history 目录。

- 截图来源为游戏窗口客户区或模拟器画面，保留采集到的原始像素宽高，编码为高质量 JPEG。
- 关键字模式在首次接受成功/失败关键字判定时自动截图；判断脚本模式在首次接受 `status: "success"` / `"partial"` / `"failed"` 时自动截图。关键字模式不能产生 `partial`。
- JavaScript 判断脚本可随时调用 `nexus.captureScreenshot()`，返回截图 ID；Python 判断脚本可使用输入中的 `screenshotApi.endpoint` 和 `screenshotApi.token`，向 endpoint 发送带 `X-Nexus-Screenshot-Token` 请求头的 `POST` 请求来截图。该地址仅绑定本机回环，并随当前判断脚本调用结束失效。
- PC 游戏运行期间由宿主约每秒维护一张最近有效帧（最多保留 2 秒，按 Attempt 隔离）。截图请求先采集当前窗口；如果判断脚本或关键字生效后窗口已经消失，宿主直接回退到有效缓存帧，不增加关闭游戏前的等待阶段。插件无需自行检测窗口、维护截图缓存或实现重试。
- 输入中的 `screenshots` 仅包含 ID、序号、时间、尝试次数、尺寸、来源和触发类型等元数据，不包含图片字节。
- 输出的 `notifyScreenshotId` 指定最终 Attempt 的脚本通知附带截图。留空时选择最终 Attempt 当前仍保留的最新截图；填写已被淘汰、属于其他 Attempt 或不存在的 ID 时不附图，并记录警告。脚本通知发送后截图池释放；队列汇总通知不附图。



## 配置还原描述（config-restore.json）

**自动更新配置**（专项恒开）下，判断脚本插队文件（`replaceConfigs` 目标）在运行收尾同步快照前，宿主会按还原描述把任务启停字段还原为初始值，再连同运行后计数/其他字段一并写入用户快照 store（保留游戏脚本自身写入的完成记录/计数/新任务）。

- **写入时机**：判断脚本**首次触发**时（任意判定前）用 `nexus.writeFile("config-restore.json", ...)` 写入 script 目录根；跨尝试只写一次（以 `nexus.listFiles()` 检查存在性）。文件随运行结束自动清空。
- **提取内容**：读取 config 中「初始任务启停映射」——array 型取任务数组全部 `keyField → enabled`；map 型取启停对象全部键值。
- **契约格式**：

```json
{
  "files": [
    {
      "file": "mxu-MaaEnd.json",
      "toggles": [
        {
          "type": "array",
          "path": "instances[id=main].tasks",
          "keyField": "id",
          "enabledField": "enabled",
          "initial": { "t1": true, "t2": true, "t3": true }
        }
      ]
    },
    {
      "file": "默认配置.json",
      "toggles": [
        { "type": "map", "path": "TaskEnabledList", "initial": { "<guid>": true } }
      ]
    }
  ]
}
```

- `file`：相对 config 的路径（目录型 ConfigPath 相对路径；文件型 = 文件名，须与 `replaceConfigs` 项一致）。
- array 型：按 `path` 定位 JSON 数组（DSL 支持 `标识符[下标].标识符` 与 `标识符[key=value].标识符` 链），元素取 `keyField` 查 `initial`，命中则设 `enabledField` 为对应布尔；**未覆盖元素保持当前值**（脚本更新新增的任务不被误改）。优先使用稳定 ID 选择实例，避免实例数组重排导致还原错误。
- map 型：`path` 为 JSON 对象键，遍历 `initial` 逐键设布尔；**未覆盖键保持当前值**。
- boolArray 型：`path` 定位布尔数组（如 BAAH 平行数组 `TASK_ORDER_GROUP.ALL_PIPELINES[0].TASK_ONOFF`），`initial` 为布尔数组，按下标逐位还原；**超出 initial 的尾部元素保持当前值**；目标数组短于 `initial` 时视为应用失败（该文件不入快照）。
- 仅作用于插队文件（`replaceConfigs` 清单内）；还原描述缺失/解析失败/应用失败时，该文件按「无还原描述」处理（不写入快照）。
- 现有专项实现参考：`maaend/data/judge.js`（array 型，`instances[id=...].tasks`）、`bettergi/data/judge.js`（map 型，`TaskEnabledList`）、`baah/data/judge.js`（boolArray 型，平行数组 `TASK_ONOFF`）。

插件机器标识参与脚本实例、配置、密钥、作用域和用户偏好隔离；artifactName 参与源码、安装和发行文件系统路径。发布后应保持两者稳定；变更身份时按新插件重新配置用户绑定和插件设置。

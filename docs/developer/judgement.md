# 判定脚本

JavaScript 定义 `nexusJudge(input)`，Python 定义 `nexus_judge(input)`。
返回对象包含 `outcome` 与 `reason`；结果为 `unverified`、`succeeded`、`partial` 或 `failed`。
非未核验结果需要非空原因，原因最多 4096 个字符。

输入归实际 attempt、步骤、Core owner 与调用代次所有。
父进程保存不可变输入与响应，历史读取校验原始内容和绑定，不再次执行脚本。
每次 JavaScript 调用使用新引擎；每次 Python 调用使用单独受理的新解释器进程。
取消先停止真实进程族并收拢 IPC，再允许配置恢复。

判定和截图 broker 的冻结运行依赖最多 512 个文件，覆盖自包含 Host 的共享框架闭包。
计划与 worker 使用同一容量；每个文件仍核对路径、真实文件身份、大小和摘要，超限在准入前拒绝。

## JavaScript 文件函数

`nexus.files` 提供以下函数，参数与返回值为原生 JavaScript 值。

| 函数 | 行为 |
|---|---|
| `readText(path)` | 读取脚本目录或本次绑定配置范围内的文本，失败返回 `null` |
| `writeText(relativePath, text)` | 在脚本目录写入 UTF-8 文本，返回是否完成 |
| `list()` | 返回本次输入中已采样的文件元数据数组 |

相对路径使用 `/`；写入不接受绝对路径、设备路径、数据流或 `.`、`..` 段。
读取可使用允许范围内的绝对路径，也可使用相对脚本目录的路径。
单次读取或写入最多 2 MiB；超限写入在创建文件或截断已有内容前拒绝。
读取识别文本 BOM。不存在、不可读取或不被允许的文件返回 `null`。
写入可创建脚本目录内的父目录；配置范围始终只读，包括位于脚本目录内的配置。
访问使用真实文件身份、目录租约及现役物理资源键检查，拒绝 reparse point 和多硬链接文件。
写入失败可能已经改变脚本目录内的文件，任务恢复不会将用户脚本副作用冒充为无副作用。

`list()` 的每项含 `root`、`path`、`absolutePath`。
数组为本次输入的采样视图，修改它不会修改宿主元数据，也不重新枚举当前目录。
Python 的直接文件操作由用户脚本执行，不经过这个 JavaScript 文件门面。

```javascript
function nexusJudge(input) {
  const text = nexus.files.readText('result.txt');
  return {
    outcome: text === 'done' ? 'succeeded' : 'unverified',
    reason: text === 'done' ? '结果文件已确认' : ''
  };
}
```

完整运行、文件日志、游戏及模拟器集成仍需按执行任务门槛验证；独立 worker 用例不代替完整产品验收。

## 控制台诊断

JavaScript 提供 `console.log/info/warn/error/debug`，以空格连接参数的字符串表示。
Python 捕获本次执行的标准输出与标准错误，分别记录为 `log` 与 `error` 文本段。
它们属于对应调用的响应证据，不参与业务结果解析；结果必须由判定函数返回。

每次调用合计最多 16 KiB UTF-8 文本、128 段，超限使本次判定返回执行故障，不补业务成功。
每次调用使用独立额度；响应最多 128 KiB，输入、包含诊断的响应及探测观察记录共同计入每步骤 64 MiB 证据预算。
周期调用为唯一最终输入、响应和探测额度保留空间。诊断只随所属响应保存，历史读取不执行脚本。
历史页的诊断展示与统一脱敏仍由 Records 接入。

## 进程与窗口探测

JS 与 Python 均提供 `nexus.probes.processes(options)` 和 `nexus.probes.windows(options)`。
省略 options 等同空对象；进程选项为正整数 `pid` 和字符串 `nameContains`，窗口选项为 `pid` 和 `titleContains`。
过滤串最多 256 个字符，匹配忽略大小写；不接受未声明的字段或类型。

进程响应包含 `processes` 与 `truncated`；每项为 `pid`、`ppid`、`name`、可空的 `startTimeUtc`。
窗口响应包含 `windows` 与 `truncated`；每项为十进制字符串 `hwnd`、`pid`、`name`、`title`、`foreground`。
最多返回 2048 个进程或 512 个可见顶层窗口，窗口标题最多 256 个字符。
这些是易变观察，不能作为停止进程、保留游戏或恢复配置的拥有权凭证。

每次判定最多 16 次探测。普通进程、窗口帧保持 2 MiB 上限；
父进程按本代实际输入和剩余步骤预算分配观察额度，最多 16 MiB，计入原有 64 MiB 步骤证据预算。
无效选项、无法取得快照或额度耗尽产生探测故障；未捕获的故障使本次判定无业务结论。
每代额度独立。取消后的迟到观察不能授权结果；历史读取只校验保存的观察，不重新枚举系统。

## HTTP GET

两种语言均提供 `nexus.probes.httpGet({url, timeoutMs, maxBytes})`。
URL 只接受 HTTP/HTTPS，最多 2048 个字符，不接受用户信息、片段、控制字符或未声明的选项。
超时默认 10 秒、最多 15 秒，正文默认及最多 2 MiB；数值选项钳制在 1 到对应上限。
请求使用冻结的宿主代理设置和不透明密码引用；回环地址逐跳绕过代理。
保留系统 TLS 校验、最多 50 次自动重定向、单次请求 Cookie 及 gzip/deflate 解压。

响应包含 `ok`、`status`、`body`、`truncated`、`error`。
正文在解压后按字节限额读取，使用 UTF-8 替换无效字节；只有额外读到一个字节才标记截断。
HTTP 非成功状态仍保留实际状态及正文。传输错误仅返回 `network_error`，请求期限返回 `timeout`；父取消直接中断调用。
这些观察不会自动生成业务成功，业务结果仍须由判定函数返回。

HTTP 正文经过 JSON 转义后可使用最多 16 MiB 的专用私有帧；普通帧与通用 JSON 边界保持原限额。
输入和探测帧使用最多 1 MiB 的不可变内容块，计划元数据仍最多 2 MiB。
实际编码的输入、响应和探测帧共同计入步骤预算，周期调用为最终输入、响应与故障帧留出空间。
历史重放校验保存的帧及观察；引用扫描只遍历元数据和内容块引用，不重新请求网络。

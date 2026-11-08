# 命令与源码拥有权参考

命令形状按所选任务使用，统一执行顺序见[验证规范](validation.md)。路径换成本轮显式仓外目录；本机临时输出使用 Temp。

## README 命令

```text
python tooling/nexus.py snapshot --output <仓外快照目录>
python tooling/nexus.py check --scope source
python tooling/nexus.py check --scope contracts
python tooling/nexus.py build --profile test --output <仓外构建目录>
python tooling/nexus.py build --profile production --output <仓外构建目录>
python tooling/nexus.py sdk-packages --build-root <同源构建目录> --consumer-root <Plugins仓库/tests/consumers> --output <仓外SDK工作目录> --cache <共用缓存目录>
python tooling/nexus.py test --group contracts --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group core --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group windows --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group tooling --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group host-boundary --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group storage --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group storage-file-logs --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group storage-checkin --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py test --group storage-config-cross-volume --fixture-volume-root <第二个真实卷的现有测试目录> --build-root <仓外Test构建目录> --output <仓外报告根>
python tooling/nexus.py storage-experiment --build-root <仓外Test构建目录> --output <仓外报告根>
```

## README 命令

```text
python tooling/web_build.py --prepare-only --output <仓外构建槽>
node tooling/web_contracts.mjs --root <源码根> --modules <构建槽/web-workspace/apps/web/node_modules> --suite automation --suite account-summary --output <新报告目录>
node tooling/web_browser.mjs --root <源码根> --modules <同一modules> --browser <浏览器绝对路径> --scenario controls --runtime <构建槽> --output <新报告目录>
```

## workflow 命令

```text
$workRoot = 'D:\Projects\Temp\nexus-validation'
New-Item -ItemType Directory -Force "$workRoot\tmp" | Out-Null
$env:TEMP = "$workRoot\tmp"
$env:TMP = $env:TEMP
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:npm_config_cache = "$workRoot\cache\npm"
python -B tooling/nexus.py snapshot --output "$workRoot\clean-host"
```

## workflow 命令

```text
python -B tooling/nexus.py test --group core --method NexusPipeline.CoreTests.RetryBudgetTests.KeepsOriginalDeadline --build-root "$workRoot\backend" --output "$workRoot\reports"
```

## workflow 命令

```text
python -B tooling/nexus.py product-publish --build-root "$workRoot\backend" --output "$workRoot\product" --runtime-archive '<锁定 Electron ZIP>' --profile test
python -B tooling/nexus.py product-assemble --publication-root '<本次发布目录>' --output "$workRoot\acceptance" --bundle-root '<本次已验签 bundle>'
```

## 产物、来源与依赖

Host/Update 的 Test 清单使用 asInvoker，Production 保持 requireAdministrator。每用户 Installer worker 使用 asInvoker，仅承担安装生命周期，不进入公开 ZIP。
源码快照算法为 nexus-source-manifest-sha256-v2-lf：相对路径按 UTF-8 字节排序，记录原字节大小、SHA256 与模式；manifest 使用 UTF-8/LF、两空格缩进，receipt 单列 manifest 摘要。
不可变 manifest 与按 SHA256 去重的原字节由仓外构建槽拥有；更新前核对旧证据。组件复用核验项目闭包、原始回执、全部产物集合和字节；未知 MSBuild 输入拒绝复用。
原生测试核对产物运行前后字节，保留真实 TRX、退出码及计时。精确方法选择直接消费原生发现，不再维护参数用例计数清单。
storage-checkin 使用显式 NEXUS_PACKAGED_EXTENSION_ROOT 和 NEXUS_PACKAGED_CHECKIN_RECEIPT，安装真实 ZIP；受控 HTTP 和原生会话使用测试账号。
跨卷测试要求显式第二个真实卷目录，缺环境记未运行。维护无 Job 正例由产品核对实际进程拥有权，不绕过现有 Job。
依赖更新使用上面的 dependencies --update-locks；日常构建锁定解析，不自动改锁。Web 原生锁由 web_build.py --update-lock 更新。

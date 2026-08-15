using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 安卓模拟器适配（v0.7.0+）：adb 命令执行、前台应用检测、模拟器关闭。
/// 关闭链路：MuMu 专项（MuMuManager info 反查 vmindex → control shutdown，官方优雅退出）→ 回退 adb shell reboot -p（Android 系统关机）。
/// adb 命令均重定向 stdio 并消费（规避 0x800700E8），单命令超时强杀。
/// </summary>
internal static class EmulatorSupport
{
    private static string? _adbExe;

    private static string? _muMuManager;

    public static bool IsEmulator(ScriptInstance script)
    {
        return script.GameMode == "emulator";
    }

    /// <summary>ADB 地址格式校验：host:port（host 非空，port 1-65535）。</summary>
    public static bool IsValidAdbAddress(string? address)
    {
        return ParseAdbPort(address) is not null;
    }

    /// <summary>解析 ADB 地址端口（host:port）；格式不合法返回 null。</summary>
    public static int? ParseAdbPort(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }
        string trimmed = address.Trim();
        int colon = trimmed.LastIndexOf(':');
        if (colon <= 0 || colon == trimmed.Length - 1)
        {
            return null;
        }
        if (int.TryParse(trimmed[(colon + 1)..], out int port) && port >= 1 && port <= 65535)
        {
            return port;
        }
        return null;
    }

    /// <summary>解析 am start 参数中的目标包名（-n 包名/Activity 或 -n 包名）；找不到返回 null。</summary>
    public static string? ParseAmStartPackage(string args)
    {
        string[] tokens = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            if (string.Equals(tokens[i], "-n", StringComparison.Ordinal))
            {
                string target = tokens[i + 1];
                int slash = target.IndexOf('/');
                string pkg = slash >= 0 ? target[..slash] : target;
                return string.IsNullOrWhiteSpace(pkg) ? null : pkg;
            }
        }
        return null;
    }

    /// <summary>解析 dumpsys window 输出中的前台应用包名（mCurrentFocus 行，兜底 topResumedActivity 行）；解析不到返回 null。</summary>
    public static string? ParseForegroundPackage(string dumpsysOutput)
    {
        foreach (string rawLine in dumpsysOutput.Split('\n'))
        {
            string line = rawLine.Trim();
            string? target = null;
            if (line.StartsWith("mCurrentFocus=", StringComparison.Ordinal))
            {
                int focusStart = line.IndexOf('{');
                int focusEnd = line.IndexOf('}');
                if (focusStart >= 0 && focusEnd > focusStart)
                {
                    // 格式：Window{253a256 u0 com.android.settings/com.android.settings.Settings}
                    string inside = line[(focusStart + 1)..focusEnd];
                    int userMark = inside.LastIndexOf(" u0 ", StringComparison.Ordinal);
                    if (userMark < 0)
                    {
                        userMark = inside.LastIndexOf(" u1 ", StringComparison.Ordinal);
                    }
                    target = userMark >= 0 ? inside[(userMark + 4)..] : inside;
                }
            }
            else if (line.StartsWith("topResumedActivity=", StringComparison.Ordinal))
            {
                int recordStart = line.IndexOf('{');
                int recordEnd = line.IndexOf('}');
                if (recordStart >= 0 && recordEnd > recordStart)
                {
                    string inside = line[(recordStart + 1)..recordEnd];
                    string[] parts = inside.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    target = parts.Length >= 2 ? parts[parts.Length - 2] : null;
                }
            }
            if (target is null)
            {
                continue;
            }
            int slash = target.IndexOf('/');
            string pkg = (slash >= 0 ? target[..slash] : target).Trim();
            if (pkg.Length > 0)
            {
                return pkg;
            }
        }
        return null;
    }

    /// <summary>解析 MuMuManager info 输出中 adb 端口对应的实例索引；找不到返回 null。</summary>
    public static string? ParseMuMuVmIndex(string infoJson, int adbPort)
    {
        try
        {
            if (JsonNode.Parse(infoJson) is not JsonObject root)
            {
                return null;
            }
            foreach (KeyValuePair<string, JsonNode?> kv in root)
            {
                if (kv.Value is JsonObject obj && obj["adb_port"]?.GetValue<int>() == adbPort)
                {
                    return kv.Key;
                }
            }
        }
        catch
        {
            // 解析失败按找不到处理（调用方记日志）
        }
        return null;
    }

    /// <summary>解析 adb 可执行文件：测试钩子 NEXUS_ADB_EXE 优先，其次 PATH，再其次 MuMu 常见安装目录；找不到返回 null（结果缓存）。</summary>
    public static string? ResolveAdbExe()
    {
        if (_adbExe is not null)
        {
            return _adbExe.Length == 0 ? null : _adbExe;
        }
        string? found = TestHooks.AdbExe;
        if (!string.IsNullOrWhiteSpace(found) && File.Exists(found))
        {
            _adbExe = found;
            Logger.Info($"[模拟器] adb 解析：测试钩子 NEXUS_ADB_EXE → {found}");
            return found;
        }
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            string candidate = Path.Combine(dir.Trim(), "adb.exe");
            if (!string.IsNullOrWhiteSpace(dir) && File.Exists(candidate))
            {
                found = candidate;
                break;
            }
        }
        if (found is null)
        {
            string[] roots =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Netease"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Netease"),
            };
            string[] candidates =
            {
                @"MuMu\nx_main\adb.exe",
                @"MuMuPlayer-12.0\shell\adb.exe",
                @"MuMuPlayer-6.0\vmonitor\bin\adb_server.exe",
            };
            foreach (string root in roots)
            {
                foreach (string rel in candidates)
                {
                    string candidate = Path.Combine(root, rel);
                    if (File.Exists(candidate))
                    {
                        found = candidate;
                        break;
                    }
                }
                if (found is not null)
                {
                    break;
                }
            }
        }
        _adbExe = found ?? "";
        return found;
    }

    /// <summary>解析 MuMuManager 可执行文件（MuMu 12 官方工具，与 adb 同目录或常见安装目录）；找不到返回 null（结果缓存）。</summary>
    public static string? ResolveMuMuManager()
    {
        if (_muMuManager is not null)
        {
            return _muMuManager.Length == 0 ? null : _muMuManager;
        }
        string? found = null;
        string? adbDir = string.IsNullOrWhiteSpace(_adbExe) ? null : Path.GetDirectoryName(_adbExe);
        if (adbDir is not null)
        {
            string candidate = Path.Combine(adbDir, "MuMuManager.exe");
            if (File.Exists(candidate))
            {
                found = candidate;
            }
        }
        if (found is null)
        {
            string[] roots =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Netease"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Netease"),
            };
            string[] candidates =
            {
                @"MuMu\nx_main\MuMuManager.exe",
                @"MuMuPlayer-12.0\shell\MuMuManager.exe",
            };
            foreach (string root in roots)
            {
                foreach (string rel in candidates)
                {
                    string candidate = Path.Combine(root, rel);
                    if (File.Exists(candidate))
                    {
                        found = candidate;
                        break;
                    }
                }
                if (found is not null)
                {
                    break;
                }
            }
        }
        _muMuManager = found ?? "";
        return found;
    }

    /// <summary>执行 adb -s &lt;addr&gt; shell &lt;args...&gt;；返回 (是否成功, 输出)。</summary>
    public static Task<(bool Ok, string Output)> AdbShellAsync(string adbExe, string address, string[] shellArgs, int timeoutSeconds, CancellationToken token)
    {
        var args = new List<string> { "-s", address, "shell" };
        args.AddRange(shellArgs);
        return RunCommandAsync(adbExe, args, timeoutSeconds, token);
    }

    /// <summary>
    /// 执行 adb connect &lt;addr&gt;；返回 (是否成功, 输出)。
    /// adb connect 对拒绝连接的目标（模拟器未开/地址错误）退出码仍为 0（实测 10061 拒绝 exit 0），
    /// 须按输出失败标记识别（"cannot connect" 前缀固定英文，错误描述本地化不依赖）。
    /// </summary>
    public static async Task<(bool Ok, string Output)> AdbConnectAsync(string adbExe, string address, CancellationToken token)
    {
        (bool ok, string output) = await RunCommandAsync(adbExe, new[] { "connect", address }, 30, token).ConfigureAwait(false);
        if (ok && ConnectFailed(output))
        {
            return (false, output);
        }
        return (ok, output);
    }

    /// <summary>adb connect 输出是否含失败标记（无法连接目标）。</summary>
    private static bool ConnectFailed(string output)
    {
        return output.Contains("cannot connect", StringComparison.OrdinalIgnoreCase)
            || output.Contains("failed to connect", StringComparison.OrdinalIgnoreCase)
            || output.Contains("unable to connect", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// am start 输出是否表明启动失败（"Error" 字样）。am 工具对 Activity 不存在/意图无法解析等错误退出码仍为 0
    /// （实测 "Error: Activity class ... does not exist." exit 0），须按输出识别；
    /// "Warning: Activity not started..."（重复启动交回当前任务）属正常，不含 Error 字样不受影响。
    /// </summary>
    public static bool AmStartFailed(string output)
    {
        return output.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>查询模拟器当前前台应用包名（dumpsys window）；查询失败返回 null。</summary>
    public static async Task<string?> GetForegroundPackageAsync(string adbExe, string address, CancellationToken token)
    {
        (bool ok, string output) = await AdbShellAsync(adbExe, address, new[] { "dumpsys", "window" }, 30, token).ConfigureAwait(false);
        return ok ? ParseForegroundPackage(output) : null;
    }

    /// <summary>关闭模拟器当前前台应用（am force-stop）；桌面与系统界面跳过。失败仅记警告。</summary>
    public static async Task ForceStopForegroundAppAsync(string adbExe, string address, CancellationToken token)
    {
        string? pkg = await GetForegroundPackageAsync(adbExe, address, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pkg))
        {
            Logger.Info($"[模拟器] 未能识别模拟器当前前台应用，跳过关闭。");
            return;
        }
        if (pkg is "com.android.systemui" or "com.android.launcher" or "app.lawnchair" or "com.mumu.launcher")
        {
            Logger.Info($"[模拟器] 当前前台为桌面/系统界面（{pkg}），跳过关闭。");
            return;
        }
        (bool ok, string output) = await AdbShellAsync(adbExe, address, new[] { "am", "force-stop", pkg }, 30, token).ConfigureAwait(false);
        if (ok)
        {
            Logger.Info($"[模拟器] 已关闭前台应用：{pkg}。");
        }
        else
        {
            Logger.Warn($"[模拟器] 关闭前台应用 {pkg} 失败：{Truncate(output)}");
        }
    }

    /// <summary>
    /// 关闭整个模拟器（v0.7.0+）：优先 MuMu 专项（MuMuManager info 按 adb 端口反查实例索引 → control shutdown，
    /// 官方优雅退出且进程完全退出）；MuMuManager 不可用/非 MuMu 模拟器时回退 adb shell reboot -p（Android 系统关机）。
    /// 每条关闭路径均以轮询确认离线为成功凭据（MuMuManager 退出码不可信时避免虚假成功）。
    /// 返回 (是否成功, 说明)。
    /// </summary>
    public static async Task<(bool Ok, string Message)> ShutdownEmulatorAsync(string adbExe, string address, CancellationToken token)
    {
        if (ParseAdbPort(address) is int port)
        {
            string? mm = ResolveMuMuManager();
            if (mm is not null)
            {
                (bool infoOk, string infoOutput) = await RunCommandAsync(mm, new[] { "info", "-v", "all" }, 30, token).ConfigureAwait(false);
                string? index = infoOk ? ParseMuMuVmIndex(infoOutput, port) : null;
                if (index is not null)
                {
                    (bool shutdownOk, string shutdownOutput) = await RunCommandAsync(mm, new[] { "control", "-v", index, "shutdown" }, 30, token).ConfigureAwait(false);
                    if (shutdownOk)
                    {
                        if (await WaitEmulatorOfflineAsync(adbExe, address, token).ConfigureAwait(false))
                        {
                            return (true, $"已通过 MuMuManager 关闭模拟器（实例索引 {index}）");
                        }
                        Logger.Warn($"[模拟器] MuMuManager 已发送关闭指令（实例索引 {index}），但等待超时仍未确认模拟器离线。");
                    }
                    else
                    {
                        Logger.Warn($"[模拟器] MuMuManager 关闭实例 {index} 失败：{Truncate(shutdownOutput)}");
                    }
                }
            }
        }
        (bool ok, string output) = await AdbShellAsync(adbExe, address, new[] { "reboot", "-p" }, 30, token).ConfigureAwait(false);
        if (ok)
        {
            if (await WaitEmulatorOfflineAsync(adbExe, address, token).ConfigureAwait(false))
            {
                return (true, "已通过 adb shell reboot -p 关闭模拟器");
            }
            return (false, "已执行 adb shell reboot -p，但等待超时仍未确认模拟器离线");
        }
        return (false, $"关闭模拟器失败：{Truncate(output)}");
    }

    /// <summary>轮询确认模拟器离线（adb shell echo 失败即离线），上限 60 秒（随 NEXUS_TIME_SCALE 缩放）；离线返回 true。</summary>
    private static async Task<bool> WaitEmulatorOfflineAsync(string adbExe, string address, CancellationToken token)
    {
        DateTime deadline = DateTime.Now.AddSeconds(TestHooks.ScaledSeconds(60));
        while (DateTime.Now < deadline)
        {
            (bool ok, string output) = await RunCommandAsync(adbExe, new[] { "-s", address, "shell", "echo", "ok" }, 10, token).ConfigureAwait(false);
            if (!ok)
            {
                return true;
            }
            try
            {
                await Task.Delay(TestHooks.ScaledMs(1000), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>执行外部命令（重定向 stdio 并消费，规避 0x800700E8；bat/cmd 经 cmd.exe /d /s /c 包装）；超时强杀进程；返回 (退出码为 0, 输出)。</summary>
    private static async Task<(bool Ok, string Output)> RunCommandAsync(string exe, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken token)
    {
        ProcessStartInfo psi;
        if (SystemActions.IsCommandFile(exe))
        {
            // 与 BuildScriptStartInfo 同构：/d /s /c ""<exe>" "arg1" ... "argN""（开头双引号 + 单收尾引号，
            // cmd /s 删除首尾引号后参数恰好平衡；结尾多一个引号会成奇数引号导致「文件名、目录名或卷标语法不正确」）。
            var sb = new StringBuilder();
            sb.Append("/d /s /c \"\"").Append(exe).Append('"');
            foreach (string arg in args)
            {
                sb.Append(" \"").Append(arg.Replace("\"", "\\\"")).Append('"');
            }
            sb.Append('"');
            psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", sb.ToString());
        }
        else
        {
            psi = new ProcessStartInfo(exe);
            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
        }
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.CreateNoWindow = true;
        Process? process = null;
        Logger.Debug($"[模拟器] 执行命令：{psi.FileName} {psi.Arguments}");
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        using (Process proc = process!)
        {
            try
            {
                proc.StandardInput.Close();
            }
            catch
            {
                // 忽略：输入句柄关闭失败不影响命令执行
            }
            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
            Task exitTask = proc.WaitForExitAsync(token);
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            Task completed = await Task.WhenAny(exitTask, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                TryKill(proc);
                return (false, $"命令执行超时（{timeoutSeconds} 秒）");
            }
            await exitTask.ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            string output = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}".Trim();
            return (proc.ExitCode == 0, output);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能已退出
        }
    }

    private static string Truncate(string text, int max = 300)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }
}

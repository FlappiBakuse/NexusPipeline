using System.Diagnostics;
using System.Text;

namespace NexusPipeline.Utilities;

internal static class ProcessLaunch
{
    public static bool IsCommandFile(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".bat" or ".cmd" or ".com";
    }

    /// <summary>
    /// 解析脚本自启动参数是否为「运行时启动目标 + 参数」（管理端/执行端分离场景）。
    /// 仅当 Args 以显式路径特征开头（盘符 X:\、UNC \\、.\ 或 ..\）时按此语义处理：
    /// 整段到「?」为止为启动目标路径（路径段去除尾随空格），相对工作目录（脚本根目录）按标准 Windows 相对路径语义解析，
    /// 含空格无需引号；「?」之后按普通参数规则拆分为启动目标参数（无「?」则无参数）。
    /// 引号一律视为普通参数内容（不在参数里使用引号包裹路径，避免歧义）。
    /// 其余情况（普通参数开头）原样全部传给主程序；路径解析失败回退主程序并警告。
    /// </summary>
    public static (string ExePath, List<string> Args) ResolveLaunchTarget(string mainExe, string workingDir, string argsText)
    {
        if (string.IsNullOrWhiteSpace(argsText))
        {
            return (mainExe, new List<string>());
        }
        string trimmed = argsText.Trim();
        bool absolute = (trimmed.Length >= 3 && char.IsLetter(trimmed[0]) && trimmed[1] == ':' && (trimmed[2] == '\\' || trimmed[2] == '/'))
            || trimmed.StartsWith("\\\\", StringComparison.Ordinal);
        bool relative = trimmed.StartsWith(".\\", StringComparison.Ordinal)
            || trimmed.StartsWith("./", StringComparison.Ordinal)
            || trimmed.StartsWith("..\\", StringComparison.Ordinal)
            || trimmed.StartsWith("../", StringComparison.Ordinal);
        if (!absolute && !relative)
        {
            return (mainExe, TextRules.SplitArgs(argsText));
        }
        string targetPart = trimmed;
        string targetArgsPart = "";
        int question = trimmed.IndexOf('?');
        if (question >= 0)
        {
            targetPart = trimmed[..question].TrimEnd();
            targetArgsPart = trimmed[(question + 1)..].Trim();
        }
        string candidate;
        try
        {
            candidate = absolute ? targetPart : Path.GetFullPath(Path.Combine(workingDir, targetPart));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 脚本自启动参数含显式路径但解析异常，按普通参数传给主程序：{ex.Message}");
            return (mainExe, TextRules.SplitArgs(argsText));
        }
        if (TextRules.IsExecutable(candidate))
        {
            if (!string.IsNullOrWhiteSpace(targetPart))
            {
                Logger.Info($"[解析] 脚本自启动参数为显式路径，运行时启动目标改为：{candidate}");
            }
            return (candidate, TextRules.SplitArgs(targetArgsPart));
        }
        Logger.Warn($"[警告] 脚本自启动参数含显式路径但无法解析为可执行文件（{candidate}），按普通参数传给主程序。");
        return (mainExe, TextRules.SplitArgs(argsText));
    }

    /// <summary>
    /// 构建脚本启动信息：.bat/.cmd/.com 一律经 cmd.exe /d /s /c 包装（UseShellExecute=false 的 CreateProcess 路径），
    /// 完全规避 ShellExecute 对批处理文件的关联启动（避免系统“出现错误 0x800700E8”弹窗）；exe 直接启动。
    /// </summary>
    public static ProcessStartInfo BuildScriptStartInfo(string exePath, string workingDir, IEnumerable<string> args, bool noWindow, bool redirect)
    {
        if (IsCommandFile(exePath))
        {
            var sb = new StringBuilder();
            sb.Append("/d /s /c \"\"").Append(exePath).Append('"');
            foreach (string arg in args)
            {
                sb.Append(" \"").Append(arg.Replace("\"", "\\\"")).Append('"');
            }
            sb.Append('"');
            return new ProcessStartInfo("cmd.exe", sb.ToString())
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = noWindow,
                RedirectStandardOutput = redirect,
                RedirectStandardError = redirect,
            };
        }
        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = noWindow,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        return psi;
    }

    /// <summary>启动进程并持续消费已重定向的标准输出，避免子进程因管道写满而阻塞。</summary>
    public static Process? StartWithOutputDrain(ProcessStartInfo psi, bool disposeWhenExited = false)
    {
        Process? process = Process.Start(psi);
        if (process is not null)
        {
            BeginOutputDrain(process, psi);
            if (disposeWhenExited)
            {
                process.Exited += (_, _) => process.Dispose();
                process.EnableRaisingEvents = true;
            }
        }
        return process;
    }

    /// <summary>启动宿主拥有的进程并立即加入本次 Attempt 的 Job Object。</summary>
    public static Process? StartOwnedProcess(ProcessStartInfo psi, ProcessOwnership? ownership)
    {
        Process? process = Process.Start(psi);
        if (process is not null)
        {
            ownership?.TryAssign(process);
        }
        return process;
    }

    private static void BeginOutputDrain(Process process, ProcessStartInfo psi)
    {
        if (psi.RedirectStandardOutput)
        {
            process.OutputDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
        }
        if (psi.RedirectStandardError)
        {
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
        }
    }

    /// <summary>
    /// 可见窗口启动（编辑配置模式）。所有路径都通过 CreateProcess + 重定向管道启动：
    /// 无控制台父进程也能为 cmd.exe 等控制台程序提供有效 stdio，避免 ERROR_NO_DATA(0x800700E8)。
    /// 批处理仅作为启动器静默运行，直接启动的编辑器保留可见窗口。
    /// NexusPipeline 已强制以管理员身份运行，目标程序要求管理员权限时（740）直接报错，不再降级提权。
    /// </summary>
    public static Process? StartVisible(string exePath, string workingDir)
    {
        return StartVisible(exePath, workingDir, ownership: null);
    }

    /// <summary>启动可见编辑进程并立即加入当前编辑会话的 Job Object（不可用时由调用方回退到进程身份清理）。</summary>
    public static Process? StartVisible(
        string exePath,
        string workingDir,
        ProcessOwnership? ownership)
    {
        bool commandFile = IsCommandFile(exePath);
        var psi = BuildScriptStartInfo(exePath, workingDir, Array.Empty<string>(), noWindow: commandFile, redirect: true);
        try
        {
            Process? process = StartWithOutputDrain(psi);
            if (process is not null)
            {
                ownership?.TryAssign(process);
            }
            return process;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            throw new InvalidOperationException($"程序启动失败（{exePath}）：目标程序要求管理员权限，但 NexusPipeline 已以管理员身份运行仍被拒绝，请检查目标程序的权限配置", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"程序启动失败（{exePath}）：{ex.Message}", ex);
        }
    }

}

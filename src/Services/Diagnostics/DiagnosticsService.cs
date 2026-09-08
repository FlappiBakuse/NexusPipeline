using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Persistence;
using NexusPipeline.Plugins;
using NexusPipeline.Services.Execution;
using NexusPipeline.Services.Update;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Diagnostics;

internal sealed record DiagnosticCheck(
    string Id,
    string Category,
    string Status,
    string Summary,
    string Detail,
    string Remediation);

internal sealed record DiagnosticSnapshot(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    string HostVersion,
    string OverallStatus,
    IReadOnlyList<DiagnosticCheck> Checks);

internal sealed record DiagnosticBundleResult(string Path, long SizeBytes);

/// <summary>
/// 环境诊断与脱敏支持包导出。诊断不自动修复、不改变用户数据、不读取设置密钥，也不发起网络请求；
/// 安装目录写权限检查仅使用受控临时探针并在检查后清理。
/// </summary>
internal sealed class DiagnosticsService
{
    private const int BundleMaxBytes = 8 * 1024 * 1024;
    private const int RecentLogMaxBytes = 2 * 1024 * 1024;

    private sealed record RecentLogPayload(string Content, bool Truncated);

    private readonly ISettingsProvider _settings;
    private readonly IScriptRepository _scripts;
    private readonly PluginManager _plugins;
    private readonly ExecutionStateStore _executionState;
    private readonly Scheduler _scheduler;
    private readonly UpdateService _updates;

    public DiagnosticsService(
        ISettingsProvider settings,
        IScriptRepository scripts,
        PluginManager plugins,
        ExecutionStateStore executionState,
        Scheduler scheduler,
        UpdateService updates)
    {
        _settings = settings;
        _scripts = scripts;
        _plugins = plugins;
        _executionState = executionState;
        _scheduler = scheduler;
        _updates = updates;
    }

    public DiagnosticSnapshot CreateSnapshot()
    {
        var checks = new List<DiagnosticCheck>();
        Add(checks, CheckHostVersion);
        Add(checks, CheckAdministrator);
        Add(checks, CheckInstallWrite);
        Add(checks, CheckWebListener);
        Add(checks, CheckMcpListener);
        Add(checks, CheckUpdateTransaction);
        Add(checks, CheckConfigRecovery);
        Add(checks, CheckExecutionState);
        Add(checks, CheckScheduler);
        Add(checks, CheckPlugins);
        Add(checks, CheckPluginPending);
        Add(checks, CheckPython);
        Add(checks, CheckAdb);
        Add(checks, CheckLogs);

        string overall = checks.Any(check => check.Status == "fail")
            ? "fail"
            : checks.Any(check => check.Status == "warn")
                ? "warn"
                : "pass";
        return new DiagnosticSnapshot(
            1,
            DateTimeOffset.Now,
            UpdateService.CurrentVersion,
            overall,
            checks);
    }

    public DiagnosticBundleResult ExportSupportBundle(string? outputPath = null)
    {
        DiagnosticSnapshot snapshot = CreateSnapshot();
        string path = ResolveOutputPath(outputPath);
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException("诊断包输出路径必须包含有效目录");
        }
        Directory.CreateDirectory(parent);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            IReadOnlyList<PluginManagementView> plugins = ReadPluginViews();
            UpdateStatusSnapshot update = _updates.GetStatus();
            ExecutionStateSnapshot execution = _executionState.SnapshotState();
            (string QueueName, DateTime TriggerTime)? next = _scheduler.NextTrigger();
            IReadOnlyList<PluginPendingOperation> pending = ReadPending();

            var environment = new
            {
                os = RuntimeInformation.OSDescription,
                framework = RuntimeInformation.FrameworkDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                isWindows = OperatingSystem.IsWindows(),
                hostVersion = UpdateService.CurrentVersion,
                baseDirectory = AppPaths.AppRoot,
            };
            var runtimeState = new
            {
                execution,
                update = new
                {
                    state = update.State.ToString(),
                    update.Current,
                    update.Latest,
                    update.Channel,
                    update.Available,
                    update.HasChecked,
                    update.Error,
                },
                scheduler = new
                {
                    running = _scheduler.IsRunning,
                    nextTrigger = next is null
                        ? null
                        : new { queueName = next.Value.QueueName, time = next.Value.TriggerTime },
                },
                pendingPluginOperations = pending.Count,
            };
            RecentLogPayload recentLog = ReadRecentLog();

            var manifest = new
            {
                schemaVersion = 1,
                generatedAt = snapshot.GeneratedAt,
                hostVersion = snapshot.HostVersion,
                files = new[]
                {
                    "manifest.json",
                    "diagnostics.json",
                    "environment.json",
                    "plugins.json",
                    "runtime-state.json",
                    "recent-log.txt",
                },
                recentLog = new { truncated = recentLog.Truncated },
                limits = new { maxBundleBytes = BundleMaxBytes, maxRecentLogBytes = RecentLogMaxBytes },
            };

            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddJson(archive, "manifest.json", manifest);
                AddJson(archive, "diagnostics.json", snapshot);
                AddJson(archive, "environment.json", environment);
                AddJson(archive, "plugins.json", plugins);
                AddJson(archive, "runtime-state.json", runtimeState);
                AddText(archive, "recent-log.txt", recentLog.Content);
            }

            long size = new FileInfo(temporary).Length;
            if (size > BundleMaxBytes)
            {
                throw new InvalidOperationException($"诊断包超过大小上限（{BundleMaxBytes / (1024 * 1024)} MiB）");
            }
            File.Move(temporary, path, overwrite: true);
            return new DiagnosticBundleResult(path, size);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private DiagnosticCheck CheckHostVersion()
    {
        return Pass(
            "host.version",
            "host",
            $"宿主版本 v{UpdateService.CurrentVersion}，运行时为 {RuntimeInformation.FrameworkDescription}");
    }

    private DiagnosticCheck CheckAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Skipped("host.admin-integrity", "host", "管理员权限检查仅适用于 Windows");
        }
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            return admin
                ? Pass("host.admin-integrity", "host", "当前进程具备管理员权限")
                : Fail("host.admin-integrity", "host", "当前进程不具备管理员权限", "请使用管理员权限启动 NexusPipeline");
        }
        catch (Exception ex)
        {
            return Warn("host.admin-integrity", "host", "无法确认当前进程权限", ex.Message, "请确认宿主以管理员权限运行");
        }
    }

    private DiagnosticCheck CheckInstallWrite()
    {
        string probe = Path.Combine(AppPaths.AppRoot, $".nxp-doctor-write-{Guid.NewGuid():N}.tmp");
        try
        {
            if (!Directory.Exists(AppPaths.AppRoot))
            {
                return Fail("host.install-write", "host", "宿主安装目录不存在", "检查安装路径或重新安装宿主");
            }

            // 写入并立即删除一个临时探针，验证更新 worker 和运行时所需的安装目录权限。
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
            {
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }
            File.Delete(probe);
            return Pass("host.install-write", "host", "宿主安装目录可写");
        }
        catch (Exception ex)
        {
            TryDelete(probe);
            return Fail("host.install-write", "host", "宿主安装目录不可写", "检查安装目录权限或使用管理员权限启动宿主", ex.Message);
        }
    }

    private DiagnosticCheck CheckWebListener()
    {
        Web.WebServer? server = Web.WebServer.Current;
        return server is { IsRunning: true }
            ? Pass("web.listener", "network", $"Web 服务正在 127.0.0.1:{server.Port} 监听")
            : Warn("web.listener", "network", "Web 服务当前未监听", "没有发现活动 Web 服务实例", "启动宿主 Web 服务或检查端口占用");
    }

    private DiagnosticCheck CheckMcpListener()
    {
        if (!_settings.Current.McpEnabled)
        {
            return Skipped("mcp.listener", "network", "MCP 服务已在设置中关闭");
        }
        return Mcp.McpHost.Current is { IsRunning: true } host
            ? Pass("mcp.listener", "network", $"MCP 服务正在 127.0.0.1:{host.Port} 监听")
            : Warn("mcp.listener", "network", "MCP 已启用但当前未监听", "没有发现活动 MCP 服务实例", "检查 MCP 端口和服务启动日志");
    }

    private DiagnosticCheck CheckUpdateTransaction()
    {
        UpdateStatusSnapshot status = _updates.GetStatus();
        bool artifacts = File.Exists(AppPaths.UpdateTaskFile)
            || Directory.Exists(AppPaths.UpdateBackupDir)
            || File.Exists(AppPaths.UpdateVersionFile);
        if (status.State == UpdateState.RecoveryPending || artifacts)
        {
            return status.State == UpdateState.RecoveryPending
                ? Fail("update.transaction", "recovery", "发现待恢复的更新事务现场", "检查更新目录并按更新恢复流程处理", "更新状态仍处于 RecoveryPending")
                : Warn("update.transaction", "recovery", "更新目录存在事务痕迹", "当前更新状态未完成清理", "确认更新事务已完成后再进行下一次更新");
        }
        return Pass("update.transaction", "recovery", "未发现待处理的更新事务现场");
    }

    private DiagnosticCheck CheckConfigRecovery()
    {
        try
        {
            int editSessions = UserConfigManager.EditSessions.Count;
            int blocked = Directory.Exists(AppPaths.DataDir)
                ? Directory.EnumerateFiles(AppPaths.DataDir, ".store-txn-blocked.json", SearchOption.AllDirectories).Count()
                : 0;
            int workDirs = Directory.Exists(AppPaths.DataDir)
                ? Directory.EnumerateDirectories(AppPaths.DataDir, ConfigSwapPaths.WorkDirName, SearchOption.AllDirectories).Count()
                : 0;
            if (blocked > 0)
            {
                return Fail("config.recovery", "recovery", "发现被阻断的配置快照事务", "人工核查配置快照事务现场后再解除阻断", $"阻断标记 {blocked} 个");
            }
            if (editSessions > 0 || workDirs > 0)
            {
                return Warn("config.recovery", "recovery", "配置系统存在活动或未清理现场", $"编辑会话 {editSessions} 个，工作目录 {workDirs} 个", "完成或取消配置编辑，并确认恢复循环已收敛");
            }
            return Pass("config.recovery", "recovery", "未发现待处理的配置恢复现场");
        }
        catch (Exception ex)
        {
            return Warn("config.recovery", "recovery", "无法完整扫描配置恢复现场", ex.Message, "检查数据目录访问权限");
        }
    }

    private DiagnosticCheck CheckExecutionState()
    {
        ExecutionStateSnapshot state = _executionState.SnapshotState();
        if (state.PendingSystemAction || state.GroupState is ExecutionGroupState.Closing or ExecutionGroupState.ActionPending or ExecutionGroupState.Cancelling)
        {
            return Warn("execution.state", "execution", "运行组正在收尾或等待系统操作", $"活动运行 {state.ActiveCount} 个，待处理系统操作 {state.PendingSystemAction}", "等待运行组完成，或从运行页面取消可取消的系统操作");
        }
        if (state.MaintenanceActive)
        {
            return Warn("execution.state", "execution", "宿主维护租约正在生效", "新的执行和配置编辑已暂时冻结", "等待维护操作完成");
        }
        return Pass("execution.state", "execution", $"执行准入状态正常，活动运行 {state.ActiveCount} 个");
    }

    private DiagnosticCheck CheckScheduler()
    {
        (string QueueName, DateTime TriggerTime)? next = _scheduler.NextTrigger();
        return _scheduler.IsRunning
            ? Pass("scheduler.state", "scheduler", next is null ? "调度器正在运行，当前没有下一次触发" : $"调度器正在运行，下一次触发队列「{next.Value.QueueName}」")
            : Warn("scheduler.state", "scheduler", "调度器当前未运行", "没有发现调度循环任务", "启动宿主调度服务并检查启动日志");
    }

    private DiagnosticCheck CheckPlugins()
    {
        try
        {
            IReadOnlyList<PluginManagementView> plugins = _plugins.PluginManagementViews;
            if (plugins.Count == 0)
            {
                return Skipped("plugin.runtime", "plugins", "当前没有已发现的插件");
            }
            string[] unhealthy = plugins
                .Where(plugin => !string.Equals(plugin.State, "Active", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(plugin.State, "Shutdown", StringComparison.OrdinalIgnoreCase))
                .Select(plugin => plugin.Name)
                .Take(8)
                .ToArray();
            return unhealthy.Length == 0
                ? Pass("plugin.runtime", "plugins", $"已发现 {plugins.Count} 个插件，运行状态正常")
                : Warn("plugin.runtime", "plugins", $"发现 {unhealthy.Length} 个插件状态需要关注", string.Join("、", unhealthy), "检查插件状态、API 版本和最近错误");
        }
        catch (Exception ex)
        {
            return Warn("plugin.runtime", "plugins", "无法读取插件运行状态", ex.Message, "检查插件目录和插件状态文件");
        }
    }

    private DiagnosticCheck CheckPluginPending()
    {
        try
        {
            IReadOnlyList<PluginPendingOperation> pending = PluginInstallRecovery.ReadPending();
            return pending.Count == 0
                ? Pass("plugin.pending", "plugins", "未发现待应用的插件事务")
                : Warn("plugin.pending", "plugins", $"发现 {pending.Count} 个待应用的插件事务", "插件安装/更新现场仍在 pending 阶段", "确认插件事务可继续应用或按恢复流程处理");
        }
        catch (Exception ex)
        {
            return Warn("plugin.pending", "plugins", "无法读取插件事务状态", ex.Message, "检查插件状态目录访问权限");
        }
    }

    private DiagnosticCheck CheckPython()
    {
        bool needed = _scripts.Snapshot().Any(script =>
            script.JudgeScriptEnabled
            && string.Equals(script.JudgeScriptLanguage, "python", StringComparison.OrdinalIgnoreCase));
        if (!needed)
        {
            return Skipped("dependency.python", "dependencies", "当前没有启用 Python 判断脚本");
        }
        return FindOnPath("python.exe") is not null
            ? Pass("dependency.python", "dependencies", "已找到 Python 解释器")
            : Fail("dependency.python", "dependencies", "未找到 Python 解释器", "启用的判断脚本需要 Python，但 PATH 中没有 python.exe");
    }

    private DiagnosticCheck CheckAdb()
    {
        bool needed = _scripts.Snapshot().Any(EmulatorSupport.IsEmulator);
        if (!needed)
        {
            return Skipped("dependency.adb", "dependencies", "当前没有使用安卓模拟器脚本");
        }
        return FindOnPath("adb.exe") is not null
            ? Pass("dependency.adb", "dependencies", "已找到 ADB 依赖")
            : Warn("dependency.adb", "dependencies", "未在 PATH 中找到 adb.exe", "模拟器脚本可能无法连接设备", "安装 Android platform-tools 或配置 ADB 路径");
    }

    private DiagnosticCheck CheckLogs()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogDir))
            {
                return Skipped("logs.recent", "logs", "日志目录尚未创建");
            }
            FileInfo? latest = new DirectoryInfo(AppPaths.LogDir)
                .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            return latest is null
                ? Skipped("logs.recent", "logs", "当前没有可供诊断的日志文件")
                : Pass("logs.recent", "logs", $"最近日志文件可访问，大小 {latest.Length} 字节");
        }
        catch (Exception ex)
        {
            return Warn("logs.recent", "logs", "无法读取最近日志状态", ex.Message, "检查日志目录访问权限");
        }
    }

    private static void Add(List<DiagnosticCheck> checks, Func<DiagnosticCheck> factory)
    {
        try
        {
            checks.Add(factory());
        }
        catch (Exception ex)
        {
            checks.Add(Warn("diagnostics.internal", "diagnostics", "诊断项执行失败", ex.Message, "检查宿主错误日志"));
        }
    }

    private IReadOnlyList<PluginManagementView> ReadPluginViews()
    {
        try
        {
            return _plugins.PluginManagementViews;
        }
        catch
        {
            return Array.Empty<PluginManagementView>();
        }
    }

    private static IReadOnlyList<PluginPendingOperation> ReadPending()
    {
        try
        {
            return PluginInstallRecovery.ReadPending();
        }
        catch
        {
            return Array.Empty<PluginPendingOperation>();
        }
    }

    private static string ResolveOutputPath(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            Directory.CreateDirectory(AppPaths.RuntimeStagingDir);
            return Path.Combine(
                AppPaths.RuntimeStagingDir,
                $"nexus-pipeline-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        }
        string path = Path.GetFullPath(requested.Trim());
        if (Path.GetPathRoot(path)?.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException("诊断包输出路径不能是文件系统根目录");
        }
        return path;
    }

    private static RecentLogPayload ReadRecentLog()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogDir))
            {
                return new RecentLogPayload("", false);
            }
            FileInfo? latest = new DirectoryInfo(AppPaths.LogDir)
                .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is null)
            {
                return new RecentLogPayload("", false);
            }
            using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long skip = Math.Max(0, stream.Length - RecentLogMaxBytes);
            stream.Seek(skip, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string safe = DiagnosticRedactor.RedactText(reader.ReadToEnd());
            safe = DiagnosticRedactor.LimitUtf8(safe, RecentLogMaxBytes, out bool redactedTruncated);
            return new RecentLogPayload(safe, skip > 0 || redactedTruncated);
        }
        catch
        {
            return new RecentLogPayload("最近日志不可读取。", false);
        }
    }

    private static void AddJson(ZipArchive archive, string name, object value)
    {
        string json = DiagnosticRedactor.RedactText(JsonSerializer.Serialize(value, JsonOpts.Indented));
        EnsureSafe(json, name);
        AddText(archive, name, json);
    }

    private static void AddText(ZipArchive archive, string name, string value)
    {
        string safe = DiagnosticRedactor.RedactText(value);
        EnsureSafe(safe, name);
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(safe);
    }

    private static void EnsureSafe(string value, string entryName)
    {
        if (DiagnosticRedactor.ContainsSecretLikeValue(value))
        {
            throw new InvalidOperationException($"诊断包条目未通过敏感信息检查：{entryName}");
        }
    }

    private static string? FindOnPath(string executable)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(raw.Trim(), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private static DiagnosticCheck Pass(string id, string category, string summary, string detail = "") =>
        new(id, category, "pass", summary, detail, "");

    private static DiagnosticCheck Warn(string id, string category, string summary, string detail, string remediation) =>
        new(id, category, "warn", summary, detail, remediation);

    private static DiagnosticCheck Fail(string id, string category, string summary, string remediation, string detail = "") =>
        new(id, category, "fail", summary, detail, remediation);

    private static DiagnosticCheck Skipped(string id, string category, string summary, string detail = "") =>
        new(id, category, "skipped", summary, detail, "");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

/// <summary>支持包统一脱敏器；输入只允许进入内存，输出前再次执行敏感信息 canary 检查。</summary>
internal static class DiagnosticRedactor
{
    private static readonly Regex Bearer = new(
        @"(?i)(Bearer\s+)[^\s""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SecretAssignment = new(
        @"(?i)([""']?(?:authorization|accessToken|refreshToken|token|secret|password|passwd|cookie|set-cookie|webhook|api[-_]?key|smtp[-_]?password)[""']?\s*[:=]\s*)(?!(?:[""']?(?:<REDACTED>|<REDACTED_URL>)))(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s,}\r\n""']+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SensitiveUrl = new(
        @"(?i)https?://[^\s""']*(?:webhook|token|secret|password|apikey)[^\s""']*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UserPath = new(
        @"(?i)[A-Z]:\\Users\\[^\s""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string RedactText(string? value)
    {
        string text = value ?? "";
        text = Bearer.Replace(text, "$1<REDACTED>");
        text = SensitiveUrl.Replace(text, "<REDACTED_URL>");
        text = SecretAssignment.Replace(text, "$1<REDACTED>");
        return UserPath.Replace(text, "<USER_PATH>");
    }

    public static bool ContainsSecretLikeValue(string? value)
    {
        string text = value ?? "";
        return Bearer.IsMatch(text)
            || SensitiveUrl.IsMatch(text)
            || SecretAssignment.IsMatch(text);
    }

    public static string LimitUtf8(string value, int maxBytes, out bool truncated)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            truncated = false;
            return value;
        }

        int start = bytes.Length - maxBytes;
        while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
        {
            start++;
        }
        truncated = true;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }
}

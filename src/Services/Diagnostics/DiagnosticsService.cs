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
    string SummaryCode,
    IReadOnlyDictionary<string, object?> SummaryArgs,
    string? DetailCode,
    IReadOnlyDictionary<string, object?> DetailArgs,
    string? RemediationCode,
    IReadOnlyDictionary<string, object?> RemediationArgs);

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
            2,
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
            Args(
                ("version", UpdateService.CurrentVersion),
                ("framework", RuntimeInformation.FrameworkDescription)));
    }

    private DiagnosticCheck CheckAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Skipped("host.admin-integrity", "host");
        }
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            return admin
                ? Pass("host.admin-integrity", "host")
                : Fail(
                    "host.admin-integrity",
                    "host",
                    remediationCode: "diagnostics.host_admin_integrity.remediation.admin");
        }
        catch (Exception ex)
        {
            return Warn(
                "host.admin-integrity",
                "host",
                Args(("exceptionType", ex.GetType().Name)),
                "diagnostics.host_admin_integrity.detail.exception",
                remediationCode: "diagnostics.host_admin_integrity.remediation.admin");
        }
    }

    private DiagnosticCheck CheckInstallWrite()
    {
        string probe = Path.Combine(AppPaths.AppRoot, $".nxp-doctor-write-{Guid.NewGuid():N}.tmp");
        try
        {
            if (!Directory.Exists(AppPaths.AppRoot))
            {
                return Fail(
                    "host.install-write",
                    "host",
                    remediationCode: "diagnostics.host_install_write.remediation.missing");
            }

            // 写入并立即删除一个临时探针，验证更新 worker 和运行时所需的安装目录权限。
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
            {
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }
            File.Delete(probe);
            return Pass("host.install-write", "host");
        }
        catch (Exception ex)
        {
            TryDelete(probe);
            return Fail(
                "host.install-write",
                "host",
                Args(("exceptionType", ex.GetType().Name)),
                "diagnostics.host_install_write.detail.exception",
                remediationCode: "diagnostics.host_install_write.remediation.permission");
        }
    }

    private DiagnosticCheck CheckWebListener()
    {
        Web.WebServer? server = Web.WebServer.Current;
        return server is { IsRunning: true }
            ? Pass("web.listener", "network", Args(("port", server.Port)))
            : Warn(
                "web.listener",
                "network",
                detailCode: "diagnostics.web_listener.detail.not_listening",
                remediationCode: "diagnostics.web_listener.remediation.start");
    }

    private DiagnosticCheck CheckMcpListener()
    {
        if (!_settings.Current.McpEnabled)
        {
            return Skipped("mcp.listener", "network");
        }
        return Mcp.McpHost.Current is { IsRunning: true } host
            ? Pass("mcp.listener", "network", Args(("port", host.Port)))
            : Warn(
                "mcp.listener",
                "network",
                detailCode: "diagnostics.mcp_listener.detail.not_listening",
                remediationCode: "diagnostics.mcp_listener.remediation.start");
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
                ? Fail(
                    "update.transaction",
                    "recovery",
                    detailCode: "diagnostics.update_transaction.detail.recovery_pending",
                    remediationCode: "diagnostics.update_transaction.remediation.recover")
                : Warn(
                    "update.transaction",
                    "recovery",
                    detailCode: "diagnostics.update_transaction.detail.artifacts",
                    remediationCode: "diagnostics.update_transaction.remediation.recover");
        }
        return Pass("update.transaction", "recovery");
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
                return Fail(
                    "config.recovery",
                    "recovery",
                    Args(("blocked", blocked)),
                    "diagnostics.config_recovery.detail.blocked",
                    remediationCode: "diagnostics.config_recovery.remediation.blocked");
            }
            if (editSessions > 0 || workDirs > 0)
            {
                return Warn(
                    "config.recovery",
                    "recovery",
                    Args(("editSessions", editSessions), ("workDirs", workDirs)),
                    "diagnostics.config_recovery.detail.active",
                    remediationCode: "diagnostics.config_recovery.remediation.active");
            }
            return Pass("config.recovery", "recovery");
        }
        catch (Exception ex)
        {
            return Warn(
                "config.recovery",
                "recovery",
                Args(("exceptionType", ex.GetType().Name)),
                "diagnostics.config_recovery.detail.exception",
                remediationCode: "diagnostics.config_recovery.remediation.permission");
        }
    }

    private DiagnosticCheck CheckExecutionState()
    {
        ExecutionStateSnapshot state = _executionState.SnapshotState();
        if (state.PendingSystemAction || state.GroupState is ExecutionGroupState.Closing or ExecutionGroupState.ActionPending or ExecutionGroupState.Cancelling)
        {
            return Warn(
                "execution.state",
                "execution",
                Args(("activeCount", state.ActiveCount), ("pendingSystemAction", state.PendingSystemAction)),
                "diagnostics.execution_state.detail.closing",
                remediationCode: "diagnostics.execution_state.remediation.closing");
        }
        if (state.MaintenanceActive)
        {
            return Warn(
                "execution.state",
                "execution",
                Args(("activeCount", state.ActiveCount), ("pendingSystemAction", state.PendingSystemAction)),
                detailCode: "diagnostics.execution_state.detail.maintenance",
                remediationCode: "diagnostics.execution_state.remediation.maintenance");
        }
        return Pass("execution.state", "execution", Args(("activeCount", state.ActiveCount)));
    }

    private DiagnosticCheck CheckScheduler()
    {
        (string QueueName, DateTime TriggerTime)? next = _scheduler.NextTrigger();
        return _scheduler.IsRunning
            ? next is null
                ? Pass("scheduler.state", "scheduler")
                : Pass(
                    "scheduler.state",
                    "scheduler",
                    detailCode: "diagnostics.scheduler_state.detail.next",
                    detailArgs: Args(("queueName", next.Value.QueueName)))
            : Warn(
                "scheduler.state",
                "scheduler",
                detailCode: "diagnostics.scheduler_state.detail.not_running",
                remediationCode: "diagnostics.scheduler_state.remediation.start");
    }

    private DiagnosticCheck CheckPlugins()
    {
        try
        {
            IReadOnlyList<PluginManagementView> plugins = _plugins.PluginManagementViews;
            if (plugins.Count == 0)
            {
                return Skipped("plugin.runtime", "plugins");
            }
            string[] unhealthy = plugins
                .Where(plugin => !string.Equals(plugin.State, "Active", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(plugin.State, "Shutdown", StringComparison.OrdinalIgnoreCase))
                .Select(plugin => plugin.Name)
                .Take(8)
                .ToArray();
            return unhealthy.Length == 0
                ? Pass("plugin.runtime", "plugins", Args(("count", plugins.Count)))
                : Warn(
                    "plugin.runtime",
                    "plugins",
                    Args(("count", unhealthy.Length)),
                    "diagnostics.plugin_runtime.detail.unhealthy",
                    Args(("names", string.Join(", ", unhealthy))),
                    "diagnostics.plugin_runtime.remediation.inspect");
        }
        catch (Exception ex)
        {
            return Warn(
                "plugin.runtime",
                "plugins",
                Args(("exceptionType", ex.GetType().Name)),
                "diagnostics.plugin_runtime.detail.exception",
                remediationCode: "diagnostics.plugin_runtime.remediation.inspect");
        }
    }

    private DiagnosticCheck CheckPluginPending()
    {
        try
        {
            IReadOnlyList<PluginPendingOperation> pending = PluginInstallRecovery.ReadPending();
            return pending.Count == 0
                ? Pass("plugin.pending", "plugins")
                : Warn(
                    "plugin.pending",
                    "plugins",
                    Args(("count", pending.Count)),
                    detailCode: "diagnostics.plugin_pending.detail.pending",
                    remediationCode: "diagnostics.plugin_pending.remediation.recover");
        }
        catch (Exception ex)
        {
            return Warn(
                "plugin.pending",
                "plugins",
                Args(("exceptionType", ex.GetType().Name)),
                "diagnostics.plugin_pending.detail.exception",
                remediationCode: "diagnostics.plugin_pending.remediation.permission");
        }
    }

    private DiagnosticCheck CheckPython()
    {
        bool needed = _scripts.Snapshot().Any(script =>
            script.JudgeScriptEnabled
            && string.Equals(script.JudgeScriptLanguage, "python", StringComparison.OrdinalIgnoreCase));
        if (!needed)
        {
            return Skipped("dependency.python", "dependencies");
        }
        return FindOnPath("python.exe") is not null
            ? Pass("dependency.python", "dependencies")
            : Fail(
                "dependency.python",
                "dependencies",
                remediationCode: "diagnostics.dependency_python.remediation.install");
    }

    private DiagnosticCheck CheckAdb()
    {
        bool needed = _scripts.Snapshot().Any(EmulatorSupport.IsEmulator);
        if (!needed)
        {
            return Skipped("dependency.adb", "dependencies");
        }
        return FindOnPath("adb.exe") is not null
            ? Pass("dependency.adb", "dependencies")
            : Warn(
                "dependency.adb",
                "dependencies",
                detailCode: "diagnostics.dependency_adb.detail.missing",
                remediationCode: "diagnostics.dependency_adb.remediation.install");
    }

    private DiagnosticCheck CheckLogs()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogDir))
            {
                return Skipped(
                    "logs.recent",
                    "logs",
                    detailCode: "diagnostics.logs_recent.detail.directory");
            }
            FileInfo? latest = new DirectoryInfo(AppPaths.LogDir)
                .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            return latest is null
                ? Skipped(
                    "logs.recent",
                    "logs",
                    detailCode: "diagnostics.logs_recent.detail.empty")
                : Pass("logs.recent", "logs", Args(("sizeBytes", latest.Length)));
        }
        catch (Exception ex)
        {
            return Warn(
                "logs.recent",
                "logs",
                Args(("exceptionType", ex.GetType().Name)),
                "diagnostics.logs_recent.detail.exception",
                remediationCode: "diagnostics.logs_recent.remediation.permission");
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
            checks.Add(Warn(
                "diagnostics.internal",
                "diagnostics",
                Args(("exceptionType", ex.GetType().Name)),
                "diagnostics.internal.detail.exception",
                remediationCode: "diagnostics.internal.remediation.logs"));
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

    private static DiagnosticCheck Pass(
        string id,
        string category,
        IReadOnlyDictionary<string, object?>? summaryArgs = null,
        string? detailCode = null,
        IReadOnlyDictionary<string, object?>? detailArgs = null,
        string? remediationCode = null,
        IReadOnlyDictionary<string, object?>? remediationArgs = null) =>
        CreateStructuredCheck(id, category, "pass", summaryArgs, detailCode, detailArgs, remediationCode, remediationArgs);

    private static DiagnosticCheck Warn(
        string id,
        string category,
        IReadOnlyDictionary<string, object?>? summaryArgs = null,
        string? detailCode = null,
        IReadOnlyDictionary<string, object?>? detailArgs = null,
        string? remediationCode = null,
        IReadOnlyDictionary<string, object?>? remediationArgs = null) =>
        CreateStructuredCheck(id, category, "warn", summaryArgs, detailCode, detailArgs, remediationCode, remediationArgs);

    private static DiagnosticCheck Fail(
        string id,
        string category,
        IReadOnlyDictionary<string, object?>? summaryArgs = null,
        string? detailCode = null,
        IReadOnlyDictionary<string, object?>? detailArgs = null,
        string? remediationCode = null,
        IReadOnlyDictionary<string, object?>? remediationArgs = null) =>
        CreateStructuredCheck(id, category, "fail", summaryArgs, detailCode, detailArgs, remediationCode, remediationArgs);

    private static DiagnosticCheck Skipped(
        string id,
        string category,
        IReadOnlyDictionary<string, object?>? summaryArgs = null,
        string? detailCode = null,
        IReadOnlyDictionary<string, object?>? detailArgs = null) =>
        CreateStructuredCheck(id, category, "skipped", summaryArgs, detailCode, detailArgs, null, null);

    private static DiagnosticCheck CreateStructuredCheck(
        string id,
        string category,
        string status,
        IReadOnlyDictionary<string, object?>? summaryArgs,
        string? detailCode,
        IReadOnlyDictionary<string, object?>? detailArgs,
        string? remediationCode,
        IReadOnlyDictionary<string, object?>? remediationArgs)
    {
        string key = id.Replace('.', '_');
        IReadOnlyDictionary<string, object?> empty = new Dictionary<string, object?>();
        return new DiagnosticCheck(
            id,
            category,
            status,
            $"diagnostics.{key}.summary.{status}",
            summaryArgs ?? empty,
            detailCode,
            detailArgs ?? empty,
            remediationCode,
            remediationArgs ?? empty);
    }

    private static IReadOnlyDictionary<string, object?> Args(
        params (string Key, object? Value)[] values)
    {
        return values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

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

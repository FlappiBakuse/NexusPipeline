using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Execution;

/// <summary>队列级并行资格分类。EmulatorOnly 仅表示已证明全部任务为安卓模拟器脚本。</summary>
internal enum ExecutionConcurrencyClass
{
    EmulatorOnly,
    Standard,
}

/// <summary>
/// 一次执行在准入时需要占用的物理资源集合。
/// 集合按 Windows 不区分大小写语义比较；ConfigPaths 另外按父子目录关系比较。
/// </summary>
internal sealed record ExecutionResourceSet(
    IReadOnlySet<string> ScriptIds,
    IReadOnlySet<string> ExecutablePaths,
    IReadOnlySet<string> ProcessNames,
    IReadOnlyList<string> ConfigPaths,
    IReadOnlySet<string> EmulatorEndpoints)
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>运行计划引用的用户数据键，格式为 user:{scriptId}:{userName}。</summary>
    public IReadOnlySet<string> UserDataKeys { get; init; } = Array.Empty<string>().ToFrozenSet(Comparer);

    /// <summary>日志路径模式资源。无法证明模式互不重叠时按冲突处理。</summary>
    public IReadOnlyList<LogResourceDescriptor> LogResources { get; init; } = Array.Empty<LogResourceDescriptor>();

    /// <summary>用户前置/后置脚本的可执行文件资源。</summary>
    public IReadOnlySet<string> AuxiliaryExecutablePaths { get; init; } = Array.Empty<string>().ToFrozenSet(Comparer);

    /// <summary>用户前置/后置脚本的进程名资源。</summary>
    public IReadOnlySet<string> AuxiliaryProcessNames { get; init; } = Array.Empty<string>().ToFrozenSet(Comparer);

    public static ExecutionResourceSet Empty { get; } = new(
        Array.Empty<string>().ToFrozenSet(Comparer),
        Array.Empty<string>().ToFrozenSet(Comparer),
        Array.Empty<string>().ToFrozenSet(Comparer),
        Array.Empty<string>(),
        Array.Empty<string>().ToFrozenSet(Comparer));

    /// <summary>返回第一个冲突资源的可读标识；null 表示两组资源不冲突。</summary>
    public string? FindConflict(ExecutionResourceSet other)
    {
        string? conflict = FindSetConflict(ScriptIds, other.ScriptIds, "script");
        if (conflict is not null)
        {
            return conflict;
        }

        conflict = FindSetConflict(UserDataKeys, other.UserDataKeys, "user");
        if (conflict is not null)
        {
            return conflict;
        }

        conflict = FindSetConflict(ExecutablePaths, other.ExecutablePaths, "executable");
        if (conflict is not null)
        {
            return conflict;
        }

        conflict = FindSetConflict(ExecutablePaths, other.AuxiliaryExecutablePaths, "executable");
        if (conflict is not null)
        {
            return conflict;
        }

        conflict = FindSetConflict(AuxiliaryExecutablePaths, other.ExecutablePaths, "auxiliary-executable");
        if (conflict is not null)
        {
            return conflict;
        }

        conflict = FindSetConflict(ProcessNames, other.ProcessNames, "process");
        if (conflict is not null)
        {
            return conflict;
        }

        conflict = FindSetConflict(AuxiliaryExecutablePaths, other.AuxiliaryExecutablePaths, "auxiliary-executable");
        if (conflict is not null)
        {
            return conflict;
        }

        conflict = FindSetConflict(ProcessNames, other.AuxiliaryProcessNames, "process");
        if (conflict is not null)
        {
            return conflict;
        }

        conflict = FindSetConflict(AuxiliaryProcessNames, other.ProcessNames, "process");
        if (conflict is not null)
        {
            return conflict;
        }

        conflict = FindSetConflict(AuxiliaryProcessNames, other.AuxiliaryProcessNames, "auxiliary-process");
        if (conflict is not null)
        {
            return conflict;
        }

        foreach (string candidate in ConfigPaths)
        {
            foreach (string existing in other.ConfigPaths)
            {
                if (PathsConflict(candidate, existing))
                {
                    return $"config:{candidate}";
                }
            }
        }

        foreach (LogResourceDescriptor candidate in LogResources)
        {
            foreach (LogResourceDescriptor existing in other.LogResources)
            {
                if (candidate.ConflictsWith(existing))
                {
                    return $"log:{candidate.DisplayPath}";
                }
            }
        }

        return FindSetConflict(EmulatorEndpoints, other.EmulatorEndpoints, "emulator");
    }

    private static string? FindSetConflict(
        IEnumerable<string> left,
        IEnumerable<string> right,
        string prefix)
    {
        HashSet<string> rightSet = right is HashSet<string> hashSet
            ? new HashSet<string>(hashSet, Comparer)
            : new HashSet<string>(right, Comparer);
        foreach (string value in left)
        {
            if (rightSet.Contains(value))
            {
                return $"{prefix}:{value}";
            }
        }
        return null;
    }

    /// <summary>
    /// 路径相等或一方是另一方的祖先目录时视为冲突。
    /// 使用路径边界比较，避免 C:\foo 与 C:\foobar 被误判为父子目录。
    /// </summary>
    internal static bool PathsConflict(string left, string right)
    {
        return IsSameOrAncestor(left, right) || IsSameOrAncestor(right, left);
    }

    private static bool IsSameOrAncestor(string parent, string child)
    {
        if (Comparer.Equals(parent, child))
        {
            return true;
        }

        string root = Path.GetPathRoot(parent) ?? string.Empty;
        if (root.Length > 0 && Comparer.Equals(parent, root))
        {
            return child.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        string prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            || parent.EndsWith(Path.AltDirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 日志路径的运行时资源描述。目录和通配/日期模式只监控目录直接匹配的文件；
/// 同一目录下只要任一方不是精确文件，就按可能重叠处理。
/// </summary>
internal sealed record LogResourceDescriptor(
    string BaseDirectory,
    string Pattern,
    bool IsExactFile,
    string DisplayPath)
{
    public static LogResourceDescriptor FromPath(string path)
    {
        string normalized = ExecutionResourceSetBuilder.NormalizePath(path);
        bool isDirectory = Directory.Exists(path);
        string baseDirectory;
        string pattern;
        if (isDirectory)
        {
            baseDirectory = normalized;
            pattern = "*";
        }
        else
        {
            baseDirectory = ExecutionResourceSetBuilder.NormalizePath(Path.GetDirectoryName(normalized) ?? Directory.GetCurrentDirectory());
            pattern = Path.GetFileName(normalized);
        }

        bool exact = pattern.Length > 0
            && pattern.IndexOf('*') < 0
            && pattern.IndexOf('{') < 0
            && pattern.IndexOf('}') < 0;
        return new LogResourceDescriptor(baseDirectory, pattern, exact, normalized);
    }

    public bool ConflictsWith(LogResourceDescriptor other)
    {
        if (!ExecutionResourceSet.PathsConflict(BaseDirectory, other.BaseDirectory))
        {
            return false;
        }

        if (IsExactFile && other.IsExactFile)
        {
            return string.Equals(
                Path.Combine(BaseDirectory, Pattern),
                Path.Combine(other.BaseDirectory, other.Pattern),
                StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }
}

/// <summary>执行准入时冻结的资格、资源与完成操作描述。</summary>
internal sealed record ExecutionAdmissionProfile(
    string Kind,
    ExecutionConcurrencyClass? QueueClass,
    ExecutionResourceSet Resources,
    string CompletionAction)
{
    public static ExecutionAdmissionProfile ForScript(
        ScriptInstance script,
        string? userName = null,
        IPluginCapabilityResolver? capabilities = null,
        IReadOnlyList<ResolvedScriptUser>? resolvedUsers = null)
    {
        IReadOnlyList<string>? users = string.IsNullOrWhiteSpace(userName)
            ? null
            : new[] { userName };
        return new ExecutionAdmissionProfile(
            "script",
            null,
            ExecutionResourceSetBuilder.Build(
                new[] { new ExecutionResourceInput(script.Id, script, users, resolvedUsers) },
                capabilities),
            "none");
    }

    public static ExecutionAdmissionProfile ForQueue(
        DispatchQueue queue,
        IReadOnlyList<PlannedQueueTask> tasks,
        IPluginCapabilityResolver? capabilities = null)
    {
        bool emulatorOnly = tasks.Count > 0
            && tasks.All(task => task.Script is not null && IsVerifiedEmulator(task.Script, capabilities));

        IEnumerable<ExecutionResourceInput> resources = tasks.Select(task =>
            new ExecutionResourceInput(task.Task.ScriptInstanceId, task.Script, task.EnabledUsers, task.ResolvedUsers));
        return new ExecutionAdmissionProfile(
            "queue",
            emulatorOnly ? ExecutionConcurrencyClass.EmulatorOnly : ExecutionConcurrencyClass.Standard,
            ExecutionResourceSetBuilder.Build(resources, capabilities),
            NormalizeCompletionAction(queue.CompletionAction));
    }

    public static bool IsVerifiedEmulator(ScriptInstance script, IPluginCapabilityResolver? capabilities)
    {
        if (!EmulatorSupport.IsEmulator(script)
            || ExecutionResourceSetBuilder.NormalizeEmulatorEndpoint(script.GameExe) is null)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(script.PluginType)
            || capabilities?.SupportsEmulator(script.PluginType) == true;
    }

    public static string NormalizeCompletionAction(string? action)
    {
        string normalized = string.IsNullOrWhiteSpace(action) ? "none" : action.Trim().ToLowerInvariant();
        return QueueRule.IsValidCompletionAction(normalized) ? normalized : "none";
    }
}

/// <summary>状态存储中登记的运行与准入 profile 关联。</summary>
internal sealed record ExecutionAdmissionEntry(
    string RunId,
    string Kind,
    string TargetId,
    string TargetName,
    ExecutionAdmissionProfile Profile);

/// <summary>队列完成后等待并行运行组空闲时提交的完成意图。</summary>
internal sealed record CompletionIntent(
    string RunId,
    string QueueName,
    string Action);

/// <summary>Windows 资源的规范化构建逻辑，与准入策略保持纯逻辑隔离。</summary>
internal sealed record ExecutionResourceInput(
    string ScriptId,
    ScriptInstance? Script,
    IReadOnlyCollection<string>? UserNames,
    IReadOnlyCollection<ResolvedScriptUser>? ResolvedUsers = null);

internal static class ExecutionResourceSetBuilder
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static ExecutionResourceSet Build(
        IEnumerable<(string ScriptId, ScriptInstance? Script)> items,
        IPluginCapabilityResolver? capabilities = null)
    {
        return Build(
            items.Select(item => new ExecutionResourceInput(item.ScriptId, item.Script, null)),
            capabilities);
    }

    public static ExecutionResourceSet Build(
        IEnumerable<ExecutionResourceInput> items,
        IPluginCapabilityResolver? capabilities = null)
    {
        var scriptIds = new HashSet<string>(Comparer);
        var userDataKeys = new HashSet<string>(Comparer);
        var executablePaths = new HashSet<string>(Comparer);
        var processNames = new HashSet<string>(Comparer);
        var configPaths = new HashSet<string>(Comparer);
        var emulatorEndpoints = new HashSet<string>(Comparer);
        var logResources = new List<LogResourceDescriptor>();
        var auxiliaryExecutablePaths = new HashSet<string>(Comparer);
        var auxiliaryProcessNames = new HashSet<string>(Comparer);

        foreach (ExecutionResourceInput item in items)
        {
            string scriptId = item.ScriptId;
            ScriptInstance? script = item.Script;
            if (!string.IsNullOrWhiteSpace(scriptId))
            {
                string normalizedScriptId = scriptId.Trim();
                scriptIds.Add($"script:{normalizedScriptId}");

                if (script is not null)
                {
                    IEnumerable<ResolvedScriptUser>? resolvedUsers = item.ResolvedUsers;
                    if (resolvedUsers is not null)
                    {
                        foreach (ResolvedScriptUser user in resolvedUsers)
                        {
                            userDataKeys.Add($"user:{normalizedScriptId}:{user.UserKey}");
                            // 用户级接管配置路径并入资源锁：不同用户可绑定不同配置文件/实例目录
                            if (user.Spec is { Succeeded: true } userSpec
                                && !string.IsNullOrWhiteSpace(userSpec.Script.ConfigPath))
                            {
                                string userConfigPath = NormalizePath(userSpec.Script.ConfigPath);
                                if (userConfigPath.Length > 0)
                                {
                                    configPaths.Add(userConfigPath);
                                }
                            }
                        }
                    }
                    else if (item.UserNames is not null)
                    {
                        foreach (string userName in item.UserNames.Where(name => !string.IsNullOrWhiteSpace(name)))
                        {
                            userDataKeys.Add($"user:{normalizedScriptId}:{userName.Trim()}");
                        }
                    }
                }
            }

            if (script is null)
            {
                continue;
            }

            string workingDir = string.IsNullOrWhiteSpace(script.RootPath)
                ? Path.GetDirectoryName(script.MainExe) ?? string.Empty
                : script.RootPath;
            (string launchExe, _) = SystemActions.ResolveLaunchTarget(script.MainExe, workingDir, script.Args);
            AddExecutable(executablePaths, processNames, launchExe);

            if (!string.IsNullOrWhiteSpace(script.ConfigPath))
            {
                string configPath = NormalizePath(script.ConfigPath);
                if (configPath.Length > 0)
                {
                    configPaths.Add(configPath);
                }
            }

            bool emulator = EmulatorSupport.IsEmulator(script);
            if (ExecutionAdmissionProfile.IsVerifiedEmulator(script, capabilities))
            {
                string? endpoint = NormalizeEmulatorEndpoint(script.GameExe);
                if (endpoint is not null)
                {
                    emulatorEndpoints.Add(endpoint);
                }
            }
            else if (!emulator)
            {
                AddExecutable(executablePaths, processNames, script.GameExe);
            }
            else if (!string.IsNullOrWhiteSpace(script.GameExe))
            {
                emulatorEndpoints.Add($"invalid:{NormalizeEndpointText(script.GameExe)}");
            }

            if (!string.IsNullOrWhiteSpace(script.LogPath))
            {
                logResources.Add(LogResourceDescriptor.FromPath(script.LogPath));
            }

            if (item.ResolvedUsers is not null)
            {
                foreach (ResolvedScriptUser user in item.ResolvedUsers)
                {
                    AddExecutable(auxiliaryExecutablePaths, auxiliaryProcessNames, user.Binding.PreRunScript);
                    AddExecutable(auxiliaryExecutablePaths, auxiliaryProcessNames, user.Binding.PostRunScript);
                }
            }
        }

        return new ExecutionResourceSet(
            scriptIds.ToFrozenSet(Comparer),
            executablePaths.ToFrozenSet(Comparer),
            processNames.ToFrozenSet(Comparer),
            configPaths.ToArray(),
            emulatorEndpoints.ToFrozenSet(Comparer))
        {
            UserDataKeys = userDataKeys.ToFrozenSet(Comparer),
            LogResources = logResources,
            AuxiliaryExecutablePaths = auxiliaryExecutablePaths.ToFrozenSet(Comparer),
            AuxiliaryProcessNames = auxiliaryProcessNames.ToFrozenSet(Comparer),
        };
    }

    public static string NormalizePath(string path)
    {
        string trimmed = path.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            string full = Path.GetFullPath(trimmed);
            full = TryGetPhysicalPath(full) ?? full;
            string root = Path.GetPathRoot(full) ?? string.Empty;
            return full.Length > root.Length
                ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : full;
        }
        catch
        {
            return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    public static string? NormalizeEmulatorEndpoint(string? endpoint)
    {
        if (!EmulatorSupport.IsValidAdbAddress(endpoint) || string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        string value = endpoint.Trim();
        int colon = value.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(value[(colon + 1)..], out int port))
        {
            return null;
        }
        string host = value[..colon].Trim().ToLowerInvariant();
        host = host.Trim('[', ']');
        if (host is "localhost" or "127.0.0.1" or "::1")
        {
            return $"loopback:{port}";
        }
        return $"{host}:{port}";
    }

    private static string NormalizeEndpointText(string endpoint)
    {
        return endpoint.Trim().ToLowerInvariant().Replace(' ', '_');
    }

    private static void AddExecutable(HashSet<string> paths, HashSet<string> processNames, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized = NormalizePath(path);
        if (normalized.Length > 0)
        {
            paths.Add(normalized);
        }

        string processName = Path.GetFileNameWithoutExtension(path.Trim());
        if (processName.Length > 0)
        {
            processNames.Add(processName);
        }
    }

    private static string? TryGetPhysicalPath(string path)
    {
        string existingPath = path;
        var suffix = new Stack<string>();
        while (!File.Exists(existingPath) && !Directory.Exists(existingPath))
        {
            string leaf = Path.GetFileName(existingPath);
            string? parent = Path.GetDirectoryName(existingPath);
            if (string.IsNullOrWhiteSpace(leaf)
                || string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, existingPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            suffix.Push(leaf);
            existingPath = parent;
        }

        string? physical = TryGetFinalPath(existingPath);
        if (physical is null)
        {
            return null;
        }
        while (suffix.Count > 0)
        {
            physical = Path.Combine(physical, suffix.Pop());
        }
        return physical;
    }

    private static string? TryGetFinalPath(string path)
    {
        IntPtr handle = CreateFile(
            path,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle == InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                return null;
            }
            string finalPath = buffer.ToString();
            if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + finalPath[8..];
            }
            return finalPath.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? finalPath[4..]
                : finalPath;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        IntPtr file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

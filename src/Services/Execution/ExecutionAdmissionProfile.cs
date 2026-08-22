using System.Collections.Frozen;
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

        conflict = FindSetConflict(ExecutablePaths, other.ExecutablePaths, "executable");
        if (conflict is not null)
        {
            return conflict;
        }

        conflict = FindSetConflict(ProcessNames, other.ProcessNames, "process");
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

/// <summary>执行准入时冻结的资格、资源与完成操作描述。</summary>
internal sealed record ExecutionAdmissionProfile(
    string Kind,
    ExecutionConcurrencyClass? QueueClass,
    ExecutionResourceSet Resources,
    string CompletionAction)
{
    public static ExecutionAdmissionProfile ForScript(ScriptInstance script)
    {
        return new ExecutionAdmissionProfile(
            "script",
            null,
            ExecutionResourceSetBuilder.Build(new[] { (script.Id, (ScriptInstance?)script) }),
            "none");
    }

    public static ExecutionAdmissionProfile ForQueue(
        DispatchQueue queue,
        IReadOnlyList<PlannedQueueTask> tasks)
    {
        bool emulatorOnly = tasks.Count > 0
            && tasks.All(task => task.Script is not null && EmulatorSupport.IsEmulator(task.Script));

        IEnumerable<(string ScriptId, ScriptInstance? Script)> resources = tasks.Select(task =>
            (task.Task.ScriptInstanceId, task.Script));
        return new ExecutionAdmissionProfile(
            "queue",
            emulatorOnly ? ExecutionConcurrencyClass.EmulatorOnly : ExecutionConcurrencyClass.Standard,
            ExecutionResourceSetBuilder.Build(resources),
            NormalizeCompletionAction(queue.CompletionAction));
    }

    /// <summary>供旧 TryRegister 兼容入口使用；新执行入口必须传入真实计划 profile。</summary>
    public static ExecutionAdmissionProfile Legacy(RunningExecution exec)
    {
        return new ExecutionAdmissionProfile(
            exec.Kind,
            exec.Kind == "queue" ? ExecutionConcurrencyClass.Standard : null,
            ExecutionResourceSet.Empty,
            "none");
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
internal static class ExecutionResourceSetBuilder
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static ExecutionResourceSet Build(IEnumerable<(string ScriptId, ScriptInstance? Script)> items)
    {
        var scriptIds = new HashSet<string>(Comparer);
        var executablePaths = new HashSet<string>(Comparer);
        var processNames = new HashSet<string>(Comparer);
        var configPaths = new HashSet<string>(Comparer);
        var emulatorEndpoints = new HashSet<string>(Comparer);

        foreach ((string scriptId, ScriptInstance? script) in items)
        {
            if (!string.IsNullOrWhiteSpace(scriptId))
            {
                scriptIds.Add($"script:{scriptId.Trim()}");
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
                configPaths.Add(NormalizePath(script.ConfigPath));
            }

            if (EmulatorSupport.IsEmulator(script))
            {
                string? endpoint = NormalizeEmulatorEndpoint(script.GameExe);
                if (endpoint is not null)
                {
                    emulatorEndpoints.Add(endpoint);
                }
            }
            else
            {
                AddExecutable(executablePaths, processNames, script.GameExe);
            }
        }

        return new ExecutionResourceSet(
            scriptIds.ToFrozenSet(Comparer),
            executablePaths.ToFrozenSet(Comparer),
            processNames.ToFrozenSet(Comparer),
            configPaths.ToArray(),
            emulatorEndpoints.ToFrozenSet(Comparer));
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
        return $"{host}:{port}";
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
}

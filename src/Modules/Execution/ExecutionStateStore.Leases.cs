using NexusPipeline.Modules.Configuration.Contracts;
using NexusPipeline.Modules.Configuration.Recovery;

namespace NexusPipeline.Modules.Execution;

internal sealed partial class ExecutionStateStore
{
    /// <summary>查询活动执行对脚本或用户数据的引用，供 Web/CLI 破坏性 CRUD 使用同一租约来源。</summary>
    public IReadOnlyList<ExecutionLeaseReference> FindLeases(string scriptId, string? userName = null)
    {
        lock (_sync)
        {
            return FindLeasesLocked(scriptId, userName);
        }
    }

    /// <summary>在准入协调锁内检查租约并执行同步数据变更，消除“检查后到删除前”的竞态窗口。</summary>
    /// <summary>
    /// 在准入协调锁内检查宿主维护状态、执行租约并完成同步数据变更。
    /// 维护租约优先于资源租约返回，确保重启/更新等待期间所有配置写入都被挡住。
    /// </summary>
    public bool TryExecuteLeaseMutation(
        string scriptId,
        string? userName,
        Action mutation,
        out IReadOnlyList<ExecutionLeaseReference> leases,
        out string? failureCode)
    {
        lock (_coordinationSync)
        {
            lock (_sync)
            {
                if (_maintenanceActive || _groupState == ExecutionGroupState.Maintenance)
                {
                    leases = Array.Empty<ExecutionLeaseReference>();
                    failureCode = "host_maintenance";
                    return false;
                }
                leases = FindLeasesLocked(scriptId, userName);
            }
            if (leases.Count > 0)
            {
                failureCode = "execution_resource_in_use";
                return false;
            }
            failureCode = null;
            mutation();
            return true;
        }
    }

    /// <summary>在准入协调锁内执行宿主级配置变更；维护租约期间拒绝写入设置和插件偏好。</summary>
    public bool TryExecuteHostConfigurationMutation(Action mutation, out string? failureCode)
    {
        lock (_coordinationSync)
        {
            lock (_sync)
            {
                if (_maintenanceActive || _groupState == ExecutionGroupState.Maintenance)
                {
                    failureCode = "host_maintenance";
                    return false;
                }
                if (UnownedRecoveryIsolationLocked().Count > 0)
                {
                    failureCode = "recovery_isolated";
                    return false;
                }
                failureCode = null;
                mutation();
                return true;
            }
        }
    }

    /// <summary>把队列 CRUD 也纳入准入协调域，避免检查完成后执行计划才登记的 TOCTOU 窗口。</summary>
    public bool TryExecuteQueueLeaseMutation(
        string queueId,
        Action mutation,
        out IReadOnlyList<ExecutionLeaseReference> leases,
        out string? failureCode)
    {
        lock (_coordinationSync)
        {
            lock (_sync)
            {
                if (_maintenanceActive || _groupState == ExecutionGroupState.Maintenance)
                {
                    leases = Array.Empty<ExecutionLeaseReference>();
                    failureCode = "host_maintenance";
                    return false;
                }
                leases = _active
                    .Where(exec => exec.Kind == "queue"
                        && string.Equals(exec.TargetId, queueId, StringComparison.Ordinal))
                    .Select(exec => new ExecutionLeaseReference(
                        exec.Id,
                        exec.Kind,
                        exec.TargetId,
                        exec.TargetName))
                    .ToList();
                if (leases.Count > 0)
                {
                    failureCode = "execution_resource_in_use";
                    return false;
                }
                failureCode = null;
                mutation();
                return true;
            }
        }
    }

    public bool TryExecuteAnyQueueLeaseMutation(
        Action mutation,
        out IReadOnlyList<ExecutionLeaseReference> leases,
        out string? failureCode)
    {
        lock (_coordinationSync)
        {
            lock (_sync)
            {
                if (_maintenanceActive || _groupState == ExecutionGroupState.Maintenance)
                {
                    leases = Array.Empty<ExecutionLeaseReference>();
                    failureCode = "host_maintenance";
                    return false;
                }
                leases = _active
                    .Where(exec => exec.Kind == "queue")
                    .Select(exec => new ExecutionLeaseReference(
                        exec.Id,
                        exec.Kind,
                        exec.TargetId,
                        exec.TargetName))
                    .ToList();
                if (leases.Count > 0)
                {
                    failureCode = "execution_resource_in_use";
                    return false;
                }
                failureCode = null;
                mutation();
                return true;
            }
        }
    }

    /// <summary>以脚本、用户和配置文件资源建立编辑会话租约。</summary>
    public bool TryBeginEditSession(
        string scriptId,
        string userName,
        string configPath,
        out string? conflict)
    {
        string normalizedScriptId = scriptId.Trim();
        string normalizedUser = userName.Trim();
        string leaseKey = $"{normalizedScriptId}:{normalizedUser}";
        ExecutionResourceSet resources = ExecutionResourceSetBuilder.Build(
                new[] { new ExecutionResourceInput(normalizedScriptId, null, new[] { normalizedUser }) })
            with
            {
                ConfigPaths = string.IsNullOrWhiteSpace(configPath)
                    ? Array.Empty<string>()
                    : new[] { Path.GetFullPath(configPath) },
            };
        return TryBeginConfigurationSession(leaseKey, resources, out conflict);
    }

    internal bool TryBeginProviderConfiguration(string operationId, string scriptId, string packageRoot,
        IReadOnlyList<NexusPipeline.Plugin.Abstractions.PluginProviderResource> requested, out string? conflict, string? previousRoot = null)
    {
        string root = Path.GetFullPath(packageRoot).TrimEnd('\\', '/');
        if (requested.Count is < 1 or > 64 || requested.Any(resource => resource.Kind is not ("writable_root" or "desktop_input" or "adb_endpoint")))
            throw new InvalidDataException("configuration.resources.invalid");
        var roots = requested.Where(resource => resource.Kind == "writable_root").Select(resource => Path.GetFullPath(resource.Identity).TrimEnd('\\', '/')).ToArray();
        if (roots.Length == 0 || roots.Any(path => !path.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("configuration.resources.root_scope");
        if (!string.IsNullOrEmpty(previousRoot)) roots = roots.Append(Path.GetFullPath(previousRoot).TrimEnd('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string path in roots)
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("configuration.resources.link");
        if (requested.Any(resource => resource.Kind == "desktop_input" && resource.Identity != "current_session"
            || resource.Kind == "adb_endpoint" && (string.IsNullOrWhiteSpace(resource.Identity) || resource.Identity.Length > 512)))
            throw new InvalidDataException("configuration.resources.identity");
        var resources = ExecutionResourceSet.Empty with
        {
            ScriptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "script:" + scriptId },
            WritableRoots = roots,
            DesktopDomains = new HashSet<string>(requested.Where(resource => resource.Kind == "desktop_input")
                .Select(_ => "session:" + System.Diagnostics.Process.GetCurrentProcess().SessionId), StringComparer.OrdinalIgnoreCase),
            EmulatorEndpoints = new HashSet<string>(requested.Where(resource => resource.Kind == "adb_endpoint")
                .Select(resource => ExecutionResourceSetBuilder.NormalizeEmulatorEndpoint(resource.Identity) ?? resource.Identity.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase),
        };
        return TryBeginConfigurationSession(scriptId + ":provider-operation:" + operationId, resources, out conflict);
    }

    private bool TryBeginConfigurationSession(string leaseKey, ExecutionResourceSet resources, out string? conflict)
    {

        lock (_coordinationSync)
        {
            lock (_sync)
            {
                if (_maintenanceActive || _groupState == ExecutionGroupState.Maintenance)
                {
                    conflict = "宿主正在进行维护操作，暂不能开始配置编辑";
                    return false;
                }
                string? isolationConflict = FindRecoveryIsolationConflictLocked(resources, unknownOnly: false);
                if (isolationConflict is not null)
                {
                    conflict = $"配置恢复隔离占用资源 {isolationConflict}";
                    return false;
                }
                foreach (ExecutionAdmissionEntry active in _active.Select(exec => new ExecutionAdmissionEntry(
                    exec.Id,
                    exec.Kind,
                    exec.TargetId,
                    exec.TargetName,
                    _admissions[exec.Id])))
                {
                    string? resource = resources.FindConflict(active.Profile.Resources);
                    if (resource is not null)
                    {
                        conflict = $"运行「{active.TargetName}」已占用资源 {resource}";
                        return false;
                    }
                }

                foreach ((string existingKey, ExecutionResourceSet existing) in _editSessionLeases)
                {
                    string? resource = resources.FindConflict(existing);
                    if (resource is not null)
                    {
                        conflict = $"配置编辑会话「{existingKey}」已占用资源 {resource}";
                        return false;
                    }
                }

                _editSessionLeases[leaseKey] = resources;
                conflict = null;
                return true;
            }
        }
    }

    public void EndEditSession(string scriptId, string userName)
    {
        string leaseKey = $"{scriptId.Trim()}:{userName.Trim()}";
        lock (_coordinationSync)
        {
            lock (_sync)
            {
                _editSessionLeases.Remove(leaseKey);
            }
        }
    }

    private IReadOnlyList<ExecutionLeaseReference> FindLeasesLocked(string scriptId, string? userName)
    {
        string scriptKey = $"script:{scriptId.Trim()}";
        string? userKey = string.IsNullOrWhiteSpace(userName)
            ? null
            : $"user:{scriptId.Trim()}:{userName.Trim()}";
        IReadOnlyList<ExecutionLeaseReference> active = _active
            .Select(exec => new
            {
                Exec = exec,
                Profile = _admissions[exec.Id],
            })
            .Where(item => item.Profile.Resources.ScriptIds.Contains(scriptKey)
                && (userKey is null || item.Profile.Resources.UserDataKeys.Contains(userKey)))
            .Select(item => new ExecutionLeaseReference(
                item.Exec.Id,
                item.Exec.Kind,
                item.Exec.TargetId,
                item.Exec.TargetName))
            .ToList();
        IReadOnlyList<ExecutionLeaseReference> isolated = UnownedRecoveryIsolationLocked()
            .Where(item => item.ScriptId.Length == 0
                || string.Equals(item.ScriptId, scriptId.Trim(), StringComparison.OrdinalIgnoreCase)
                && (userName is null || string.Equals(item.UserId, userName.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Select(item => new ExecutionLeaseReference(
                item.RecoveryId.Length == 0 ? "recovery-unknown" : item.RecoveryId,
                "recovery", item.ScriptId, "配置恢复隔离"))
            .ToArray();
        return active.Concat(isolated).ToArray();
    }
}

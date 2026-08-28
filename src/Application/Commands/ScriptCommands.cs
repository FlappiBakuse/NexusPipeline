using NexusPipeline.App.Contracts;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;

namespace NexusPipeline.App.Commands;

/// <summary>
/// 脚本实例的应用命令。HTTP、CLI 和交互菜单都通过常驻服务进入这里，Web 层只负责协议解析与展示投影。
/// 图标提取与旧客户端 users 投影仍属于 Web 展示适配，不放入本命令。
/// </summary>
internal static class ScriptCommands
{
    public static OperationResult<ScriptInstance> Create(ScriptInstance candidate, string source = Audit.Web)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                return Validation<ScriptInstance>("脚本名称不能为空");
            }

            NormalizePaths(candidate);
            string? pluginError = string.IsNullOrWhiteSpace(candidate.PluginType)
                ? null
                : ApplyProfile(candidate);
            if (pluginError is not null)
            {
                return Validation<ScriptInstance>(pluginError);
            }
            if (candidate.JudgeScriptEnabled && string.IsNullOrWhiteSpace(candidate.JudgeScript))
            {
                return Validation<ScriptInstance>("开启「使用判断脚本」但判断脚本代码为空");
            }
            string? pathError = Limits.CheckScriptPaths(
                candidate,
                RuntimeContext.Instance.Resolve<IPluginCapabilityResolver>());
            if (pathError is not null)
            {
                return Validation<ScriptInstance>(pathError);
            }

            RuntimeContext ctx = RuntimeContext.Instance;
            string? limitError = null;
            lock (ctx.DataLock)
            {
                limitError = Limits.CheckScriptCount(ctx.Scripts.Count)
                    ?? Limits.CheckNameBytes(candidate.Name, Limits.Current.MaxScriptNameBytes, "脚本名称")
                    ?? Limits.CheckAttempts(candidate.MaxAttempts)
                    ?? Limits.CheckScriptTimeouts(candidate.LogStallTimeoutMinutes, candidate.TotalTimeoutMinutes);
                if (limitError is null)
                {
                    candidate.Id = Guid.NewGuid().ToString("N");
                    candidate.Index = ctx.Scripts.Count == 0 ? 0 : ctx.Scripts.Max(item => item.Index) + 1;
                    ctx.Scripts.Add(candidate);
                    try
                    {
                        DataStore.SaveScripts(ctx.Scripts);
                    }
                    catch
                    {
                        ctx.Scripts.Remove(candidate);
                        throw;
                    }
                }
            }
            if (limitError is not null)
            {
                return Validation<ScriptInstance>(limitError);
            }

            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "添加脚本实例", $"{candidate.Name}（id={candidate.Id}）");
            return OperationResult<ScriptInstance>.Ok(candidate);
        }
        catch (Exception ex)
        {
            return Internal<ScriptInstance>(ex);
        }
    }

    public static OperationResult<ScriptInstance> Update(
        string scriptId,
        ScriptInstance candidate,
        string source = Audit.Web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        ScriptInstance? existing = ctx.FindScript(scriptId);
        if (existing is null)
        {
            return NotFound<ScriptInstance>($"未找到脚本实例：{scriptId}");
        }

        string? existingPluginError = PluginAvailability.GetUnavailableReason(
            existing,
            ctx.Resolve<IPluginAvailability>());
        if (existingPluginError is not null)
        {
            return Validation<ScriptInstance>(existingPluginError);
        }

        SemaphoreSlim gate = ScriptConfigGate.Get(existing.Id);
        if (!gate.Wait(0))
        {
            return Conflict<ScriptInstance>(
                "resource_busy",
                "脚本正在运行或编辑配置中，无法修改");
        }
        try
        {
            string? limitError = Limits.CheckNameBytes(candidate.Name, Limits.Current.MaxScriptNameBytes, "脚本名称")
                ?? Limits.CheckAttempts(candidate.MaxAttempts)
                ?? Limits.CheckScriptTimeouts(candidate.LogStallTimeoutMinutes, candidate.TotalTimeoutMinutes);
            if (limitError is not null)
            {
                return Validation<ScriptInstance>(limitError);
            }

            candidate.Id = existing.Id;
            candidate.Index = existing.Index;
            candidate.Users = existing.Users;
            NormalizePaths(candidate);
            string? pluginError = string.IsNullOrWhiteSpace(candidate.PluginType)
                ? null
                : ApplyProfile(candidate);
            if (pluginError is not null)
            {
                return Validation<ScriptInstance>(pluginError);
            }
            if (candidate.JudgeScriptEnabled && string.IsNullOrWhiteSpace(candidate.JudgeScript))
            {
                return Validation<ScriptInstance>("开启「使用判断脚本」但判断脚本代码为空");
            }
            string? pathError = Limits.CheckScriptPaths(
                candidate,
                ctx.Resolve<IPluginCapabilityResolver>());
            if (pathError is not null)
            {
                return Validation<ScriptInstance>(pathError);
            }

            IReadOnlyList<ExecutionLeaseReference> leases;
            bool changed = ctx.Center.TryExecuteLeaseMutation(
                existing.Id,
                null,
                () =>
                {
                    lock (ctx.DataLock)
                    {
                        int index = ctx.Scripts.IndexOf(existing);
                        if (index < 0)
                        {
                            return;
                        }
                        ctx.Scripts[index] = candidate;
                        DataStore.SaveScripts(ctx.Scripts);
                    }
                },
                out leases,
                out string? failureCode);
            if (!changed)
            {
                return LeaseConflict<ScriptInstance>(leases, $"script:{existing.Id}", failureCode);
            }

            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "修改脚本实例", $"{candidate.Name}（id={candidate.Id}）");
            return OperationResult<ScriptInstance>.Ok(candidate);
        }
        catch (Exception ex)
        {
            return Internal<ScriptInstance>(ex);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>删除不存在的 ID 仍返回成功，保持既有 Web API 的幂等语义。</summary>
    public static OperationResult<ScriptInstance?> Delete(string scriptId, string source = Audit.Web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        ScriptInstance? removed = ctx.FindScript(scriptId);
        SemaphoreSlim? gate = null;
        if (removed is not null)
        {
            gate = ScriptConfigGate.Get(removed.Id);
            if (!gate.Wait(0))
            {
                return Conflict<ScriptInstance?>(
                    "resource_busy",
                    "脚本正在运行或编辑配置中，无法删除");
            }
        }

        try
        {
            bool changed = ctx.Center.TryExecuteLeaseMutation(
                scriptId,
                null,
                () =>
                {
                    lock (ctx.DataLock)
                    {
                        int index = ctx.Scripts.FindIndex(script => script.Id == scriptId);
                        if (index >= 0)
                        {
                            ScriptInstance removedEntry = ctx.Scripts[index];
                            ctx.Scripts.RemoveAt(index);
                            try
                            {
                                DataStore.SaveScripts(ctx.Scripts);
                            }
                            catch
                            {
                                ctx.Scripts.Insert(index, removedEntry);
                                throw;
                            }
                        }
                    }
                    if (removed is not null)
                    {
                        UserConfigManager.RemoveScriptData(scriptId);
                        ctx.Plugins.DeleteScriptData(scriptId);
                    }
                    ConfigSwapPrimitives.RemoveMutex(scriptId);
                },
                out IReadOnlyList<ExecutionLeaseReference> leases,
                out string? failureCode);
            if (!changed)
            {
                return LeaseConflict<ScriptInstance?>(leases, $"script:{scriptId}", failureCode);
            }

            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "删除脚本实例", removed is null ? $"id={scriptId}（不存在）" : $"{removed.Name}（id={scriptId}）");
            return OperationResult<ScriptInstance?>.Ok(removed);
        }
        catch (Exception ex)
        {
            return Internal<ScriptInstance?>(ex);
        }
        finally
        {
            gate?.Release();
        }
    }

    public static OperationResult<bool> Reorder(IReadOnlyList<string>? ids, string source = Audit.Web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        try
        {
            string? error = null;
            lock (ctx.DataLock)
            {
                if (ids is null || ids.Count != ctx.Scripts.Count
                    || ids.Any(string.IsNullOrWhiteSpace)
                    || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
                {
                    error = "脚本顺序名单缺失或与当前脚本列表不一致";
                }
                else
                {
                    HashSet<string> existing = new(ctx.Scripts.Select(script => script.Id), StringComparer.Ordinal);
                    if (ids.Any(id => !existing.Contains(id)))
                    {
                        error = "脚本顺序名单与当前脚本列表不一致";
                    }
                    else
                    {
                        Dictionary<string, ScriptInstance> byId = ctx.Scripts.ToDictionary(script => script.Id, StringComparer.Ordinal);
                        for (int i = 0; i < ids.Count; i++)
                        {
                            byId[ids[i]].Index = i;
                        }
                        DataStore.SaveScripts(ctx.Scripts);
                    }
                }
            }
            if (error is not null)
            {
                return Validation<bool>(error);
            }
            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "调整脚本顺序", $"{ids!.Count} 个脚本实例");
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

    public static OperationResult<ScriptProfile> Probe(string pluginType, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(pluginType))
        {
            return Validation<ScriptProfile>("缺少专用插件标识");
        }
        RuntimeContext ctx = RuntimeContext.Instance;
        string? availabilityError = PluginAvailability.GetUnavailableReason(
            pluginType,
            ctx.Resolve<IPluginAvailability>());
        if (availabilityError is not null)
        {
            return Validation<ScriptProfile>(availabilityError);
        }
        ScriptProfile? profile = ctx.Resolve<IPluginCapabilityResolver>()
            .ResolveProfile(pluginType, StripPathQuotes(rootPath));
        return profile is null
            ? Validation<ScriptProfile>("无法从脚本根目录推导专用插件配置（请检查根目录，并确认专用插件已启用）")
            : OperationResult<ScriptProfile>.Ok(profile);
    }

    private static string? ApplyProfile(ScriptInstance script)
    {
        string? availabilityError = PluginAvailability.GetUnavailableReason(
            script,
            RuntimeContext.Instance.Resolve<IPluginAvailability>());
        if (availabilityError is not null)
        {
            return availabilityError;
        }
        ScriptProfile? profile = RuntimeContext.Instance.Plugins.ResolveProfile(script.PluginType, script.RootPath);
        if (profile is null)
        {
            return "专用插件无法从脚本根目录推导配置（请检查脚本根目录，并确认专用插件已启用）";
        }
        script.MainExe = profile.MainExe;
        script.Args = profile.Args;
        script.ConfigPath = profile.ConfigPath;
        script.LogPath = profile.LogPath;
        script.SuccessKeywords = "";
        script.FailureKeywords = "";
        script.AutoUpdateConfig = true;
        script.JudgeScriptEnabled = !string.IsNullOrWhiteSpace(profile.JudgeScript);
        script.JudgeScriptLanguage = string.IsNullOrWhiteSpace(profile.JudgeScriptLanguage)
            ? "javascript"
            : profile.JudgeScriptLanguage;
        script.JudgeScript = profile.JudgeScript ?? "";
        return null;
    }

    private static void NormalizePaths(ScriptInstance script)
    {
        script.RootPath = StripPathQuotes(script.RootPath);
        script.MainExe = StripPathQuotes(script.MainExe);
        script.ConfigPath = StripPathQuotes(script.ConfigPath);
        script.LogPath = StripPathQuotes(script.LogPath);
        script.GameExe = StripPathQuotes(script.GameExe);
    }

    private static string StripPathQuotes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        string trimmed = value.Trim();
        if (trimmed.Length >= 2)
        {
            char first = trimmed[0];
            char last = trimmed[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                return trimmed[1..^1].Trim();
            }
        }
        return trimmed;
    }

    private static OperationResult<T> Validation<T>(string message) =>
        OperationResult<T>.Failure("validation_error", message, OperationErrorKind.Validation);

    private static OperationResult<T> NotFound<T>(string message) =>
        OperationResult<T>.Failure("not_found", message, OperationErrorKind.NotFound);

    private static OperationResult<T> Conflict<T>(string code, string message) =>
        OperationResult<T>.Failure(code, message, OperationErrorKind.Conflict);

    private static OperationResult<T> LeaseConflict<T>(
        IReadOnlyList<ExecutionLeaseReference> leases,
        string resource,
        string? failureCode = null)
    {
        if (failureCode == "host_maintenance")
        {
            return OperationResult<T>.Failure(
                "host_maintenance",
                "宿主正在进行维护操作，暂不能修改运行配置",
                OperationErrorKind.Conflict);
        }
        return OperationResult<T>.Failure(
            "execution_resource_in_use",
            $"执行计划正在引用资源「{resource}」，当前无法修改；请等待相关运行结束",
            OperationErrorKind.Conflict,
            leases.Select(lease => lease.RunId).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static OperationResult<T> Internal<T>(Exception exception) =>
        OperationResult<T>.Failure("internal_error", exception.Message, OperationErrorKind.Internal);
}

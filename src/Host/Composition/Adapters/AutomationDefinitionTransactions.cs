using NexusPipeline.Host.State;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Persistence;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Bindings;
using NexusPipeline.Modules.Users.Persistence;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>Host-owned cross-entity script deletion transaction.</summary>
internal sealed class AutomationDefinitionTransactions : IScriptDeletionTransaction
{
    private readonly AutomationDefinitionState _state;
    private readonly IScriptMutationAdmission _admission;
    private readonly PluginManager _plugins;
    private readonly IScriptPlansChanged _plansChanged;

    public AutomationDefinitionTransactions(
        AutomationDefinitionState state,
        IScriptMutationAdmission admission,
        PluginManager plugins,
        IScriptPlansChanged plansChanged)
    {
        _state = state;
        _admission = admission;
        _plugins = plugins;
        _plansChanged = plansChanged;
    }

    public ScriptDeletionResult Execute(string scriptId, string source, out string? failureCode)
    {
        ScriptInstance? removed = _state.FindScript(scriptId);
        FileSnapshot? scriptsFile = null;
        FileSnapshot? usersFile = null;
        List<RemovedScriptBinding> removedBindings = new();

        ScriptMutationAdmissionResult admission = _admission.TryExecute(
            scriptId,
            null,
            () =>
            {
                _state.Mutate(state =>
                {
                    int index = state.Scripts.FindIndex(script => script.Id == scriptId);
                    bool hasBindings = state.Users.Any(user => user.Bindings.Any(binding =>
                        string.Equals(binding.ScriptInstanceId, scriptId, StringComparison.Ordinal)));
                    ScriptInstance? removedEntry = null;
                    if (index >= 0 || hasBindings)
                    {
                        scriptsFile = CaptureFileSnapshot(AppPaths.ScriptsPath);
                        usersFile = CaptureFileSnapshot(AppPaths.UsersPath);
                    }
                    if (index >= 0)
                    {
                        removedEntry = state.Scripts[index].Clone();
                        state.Scripts.RemoveAt(index);
                    }
                    removedBindings = ScriptBindingCleanup.RemoveForScript(state.Users, scriptId);
                    try
                    {
                        if (removedBindings.Count > 0)
                        {
                            UserDefinitionStore.SaveUsers(state.Users);
                        }
                        if (index >= 0)
                        {
                            ScriptDefinitionStore.SaveScripts(state.Scripts);
                        }
                    }
                    catch
                    {
                        if (removedEntry is not null && index >= 0)
                        {
                            state.Scripts.Insert(index, removedEntry);
                        }
                        ScriptBindingCleanup.Restore(removedBindings);
                        if (scriptsFile is not null)
                        {
                            RestoreFileSnapshot(scriptsFile);
                        }
                        if (usersFile is not null)
                        {
                            RestoreFileSnapshot(usersFile);
                        }
                        throw;
                    }
                });
                if (removed is not null)
                {
                    ConfigWorkAreaService.RemoveScriptData(scriptId);
                    _plugins.DeleteScriptData(scriptId);
                }
                ConfigSwapPrimitives.RemoveMutex(scriptId);
            });

        failureCode = admission.FailureCode;
        if (!admission.Allowed)
        {
            return new ScriptDeletionResult(false, removed, admission.RunIds, admission.FailureCode);
        }

        _plansChanged.RevalidatePendingPlans();
        Audit.Log(
            source,
            "删除脚本实例",
            removed is null ? $"id={scriptId}（不存在）" : $"{removed.Name}（id={scriptId}）");
        return new ScriptDeletionResult(true, removed, Array.Empty<string>(), null);
    }

    private static FileSnapshot CaptureFileSnapshot(string path)
    {
        bool exists = File.Exists(path);
        return new FileSnapshot(path, exists, exists ? File.ReadAllText(path) : "");
    }

    private static void RestoreFileSnapshot(FileSnapshot snapshot)
    {
        try
        {
            if (snapshot.Existed)
            {
                JsonUtil.WriteAtomic(snapshot.Path, snapshot.Content);
            }
            else if (File.Exists(snapshot.Path))
            {
                File.Delete(snapshot.Path);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[错误] 脚本删除回滚文件失败（{snapshot.Path}）：{ex.Message}");
        }
    }

    private sealed record FileSnapshot(string Path, bool Existed, string Content);
}

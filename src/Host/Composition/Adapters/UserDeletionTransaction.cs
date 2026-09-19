using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users.Persistence;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Host.Composition.Adapters;

/// <summary>单一实体状态锁内完成用户、绑定与关联配置/插件数据的删除。</summary>
internal sealed class UserDeletionTransaction : IUserDeletionTransaction
{
    private readonly IUserMutationState _state;
    private readonly IUserMutationAdmission _admission;
    private readonly IUserMutationPolicy _policy;
    private readonly IUserConfigDataMaintenance _configData;
    private readonly UserAssetService _assets;
    private readonly IUserPlansChanged _plansChanged;

    public UserDeletionTransaction(
        IUserMutationState state,
        IUserMutationAdmission admission,
        IUserMutationPolicy policy,
        IUserConfigDataMaintenance configData,
        UserAssetService assets,
        IUserPlansChanged plansChanged)
    {
        _state = state;
        _admission = admission;
        _policy = policy;
        _configData = configData;
        _assets = assets;
        _plansChanged = plansChanged;
    }

    public UserDeletionResult Execute(string userId)
    {
        NexusUser? target = _state.Find(userId);
        if (target is null)
        {
            return new UserDeletionResult(true, null, Array.Empty<string>(), null, null);
        }

        NexusUser snapshot = target.Clone();
        FileSnapshot usersFile = Capture(AppPaths.UsersPath);
        int removedIndex = -1;
        try
        {
            UserMutationBlock? block = null;
            _admission.WithCoordination(() =>
            {
                block = _policy.CheckUser(snapshot);
                if (block is not null)
                {
                    return;
                }
                _state.Mutate(state =>
                {
                    NexusUser? current = state.Users.FirstOrDefault(item =>
                        string.Equals(item.Id, snapshot.Id, StringComparison.OrdinalIgnoreCase));
                    if (current is null)
                    {
                        return;
                    }
                    removedIndex = state.Users.IndexOf(current);
                    state.Users.RemoveAt(removedIndex);
                    try
                    {
                        UserDefinitionStore.SaveUsers(state.Users);
                    }
                    catch
                    {
                        state.Users.Insert(removedIndex, current);
                        throw;
                    }
                });
                foreach (UserScriptBinding binding in snapshot.Bindings)
                {
                    _configData.RemoveUserScriptData(snapshot.Id, binding.ScriptInstanceId);
                }
                _assets.RemoveAvatar(snapshot.Id);
                _configData.RemoveUserData(snapshot.Id);
            });

            if (block is not null)
            {
                return new UserDeletionResult(
                    false,
                    snapshot,
                    Array.Empty<string>(),
                    block.Code,
                    block.Message);
            }

            _plansChanged.RevalidatePendingPlans();
            return new UserDeletionResult(true, snapshot, Array.Empty<string>(), null, null);
        }
        catch
        {
            if (removedIndex >= 0)
            {
                try
                {
                    _state.Mutate(state =>
                    {
                        if (!state.Users.Any(item => string.Equals(item.Id, snapshot.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            state.Users.Insert(Math.Min(removedIndex, state.Users.Count), snapshot.Clone());
                            UserDefinitionStore.SaveUsers(state.Users);
                        }
                    });
                }
                catch (Exception restoreException)
                {
                    Logger.Error($"[错误] 用户删除回滚失败（{snapshot.Id}）：{restoreException}");
                }
                Restore(usersFile);
            }
            throw;
        }
    }

    private static FileSnapshot Capture(string path)
    {
        bool exists = File.Exists(path);
        return new FileSnapshot(path, exists, exists ? File.ReadAllText(path) : "");
    }

    private static void Restore(FileSnapshot snapshot)
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
            Logger.Error($"[错误] 用户删除文件回滚失败（{snapshot.Path}）：{ex}");
        }
    }

    private sealed record FileSnapshot(string Path, bool Existed, string Content);
}

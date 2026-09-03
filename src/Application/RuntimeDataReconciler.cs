using NexusPipeline.Persistence;
using NexusPipeline.Models;
using NexusPipeline.App.State;
using NexusPipeline.Utilities;
using NexusPipeline.Services;

namespace NexusPipeline;

/// <summary>
/// 宿主取得运行时所有权后的实体数据修复协调器。
/// 读取由 RuntimeContext.ReloadData 完成；本类负责历史兼容清理、名称消歧和必要落盘。
/// </summary>
internal static class RuntimeDataReconciler
{
    public static void Reconcile(RuntimeContext ctx)
    {
        ctx.EntityState.Mutate(state =>
        {
            if (ctx.EntityState.LastScriptsLoadWasAuthoritative)
            {
                HashSet<string> scriptIds = state.Scripts
                    .Select(script => script.Id)
                    .ToHashSet(StringComparer.Ordinal);
                int removedBindings = ScriptBindingCleanup.RemoveMissingScriptBindings(state.Users, scriptIds);
                if (removedBindings > 0)
                {
                    try
                    {
                        DataStore.SaveUsers(state.Users);
                        Logger.Info($"启动时清理已删除脚本的用户绑定：{removedBindings} 条");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[用户绑定] 启动清理落盘失败（保留当前内存清理结果）：{ex.Message}");
                    }
                }
            }

            NormalizeEntityNames(state);
        });
    }

    private static void NormalizeEntityNames(RuntimeEntityMutationContext state)
    {
        List<ScriptInstance> normalizedScripts = state.Scripts.Select(script => script.Clone()).ToList();
        IReadOnlyList<NameNormalizationChange> scriptChanges = EntityNameRules.NormalizeDuplicates(
            normalizedScripts,
            script => script.Name,
            (script, name) => script.Name = name,
            script => script.Id,
            script => script.Index,
            AppFixedLimits.MaxEntityNameBytes);
        if (scriptChanges.Count > 0)
        {
            try
            {
                DataStore.SaveScripts(normalizedScripts);
                state.Scripts.Clear();
                state.Scripts.AddRange(normalizedScripts);
                Logger.Info($"启动时规范化脚本名称：{scriptChanges.Count} 个");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[脚本名称] 启动消歧落盘失败（保留原名称，下一次启动重试）：{ex.Message}");
            }
        }

        List<DispatchQueue> normalizedQueues = state.Queues.Select(queue => queue.Clone()).ToList();
        IReadOnlyList<NameNormalizationChange> queueChanges = EntityNameRules.NormalizeDuplicates(
            normalizedQueues,
            queue => queue.Name,
            (queue, name) => queue.Name = name,
            queue => queue.Id,
            queue => queue.Index,
            AppFixedLimits.MaxEntityNameBytes);
        if (queueChanges.Count > 0)
        {
            try
            {
                DataStore.SaveQueues(normalizedQueues);
                state.Queues.Clear();
                state.Queues.AddRange(normalizedQueues);
                Logger.Info($"启动时规范化调度队列名称：{queueChanges.Count} 个");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[调度队列名称] 启动消歧落盘失败（保留原名称，下一次启动重试）：{ex.Message}");
            }
        }

        List<NexusUser> normalizedUsers = state.Users.Select(user => user.Clone()).ToList();
        IReadOnlyList<NameNormalizationChange> userChanges = EntityNameRules.NormalizeDuplicates(
            normalizedUsers,
            user => user.Name,
            (user, name) => user.Name = name,
            user => user.Id,
            user => user.Index,
            AppFixedLimits.MaxEntityNameBytes);
        if (userChanges.Count > 0)
        {
            try
            {
                DataStore.SaveUsers(normalizedUsers);
                state.Users.Clear();
                state.Users.AddRange(normalizedUsers);
                Logger.Info($"启动时规范化用户昵称：{userChanges.Count} 个");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[用户昵称] 启动消歧落盘失败（保留原昵称，下一次启动重试）：{ex.Message}");
            }
        }
    }
}

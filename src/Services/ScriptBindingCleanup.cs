using NexusPipeline.Models;

namespace NexusPipeline.Services;

/// <summary>维护用户绑定与脚本实例集合之间的引用完整性。</summary>
internal static class ScriptBindingCleanup
{
    public static List<RemovedScriptBinding> RemoveForScript(
        IEnumerable<NexusUser> users,
        string scriptId)
    {
        var removed = new List<RemovedScriptBinding>();
        if (string.IsNullOrWhiteSpace(scriptId))
        {
            return removed;
        }

        foreach (NexusUser user in users)
        {
            for (int index = user.Bindings.Count - 1; index >= 0; index--)
            {
                UserScriptBinding binding = user.Bindings[index];
                if (!string.Equals(binding.ScriptInstanceId, scriptId, StringComparison.Ordinal))
                {
                    continue;
                }

                removed.Add(new RemovedScriptBinding(user, index, binding));
                user.Bindings.RemoveAt(index);
            }
        }
        return removed;
    }

    public static void Restore(IEnumerable<RemovedScriptBinding> removed)
    {
        foreach (IGrouping<NexusUser, RemovedScriptBinding> group in removed.GroupBy(item => item.User))
        {
            foreach (RemovedScriptBinding item in group.OrderBy(item => item.Index))
            {
                int index = Math.Min(item.Index, group.Key.Bindings.Count);
                group.Key.Bindings.Insert(index, item.Binding);
            }
        }
    }

    public static int RemoveMissingScriptBindings(
        IEnumerable<NexusUser> users,
        IReadOnlySet<string> scriptIds)
    {
        int removed = 0;
        foreach (NexusUser user in users)
        {
            removed += user.Bindings.RemoveAll(binding => !scriptIds.Contains(binding.ScriptInstanceId));
        }
        return removed;
    }
}

internal sealed record RemovedScriptBinding(
    NexusUser User,
    int Index,
    UserScriptBinding Binding);

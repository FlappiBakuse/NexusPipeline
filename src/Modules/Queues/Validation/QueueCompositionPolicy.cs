using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Queues.Contracts;
using NexusPipeline.Modules.Scripts;
namespace NexusPipeline.Modules.Queues.Validation;


/// <summary>约束体系：首次启动缺失时生成默认配置；绝对安全区间（内置默认值）静默生效；超安全值但入警告区间 → 启动警告；超警告区间或区间矛盾 → FATAL 拒绝启动。</summary>
internal static class QueueCompositionPolicy
{
    internal static string? CheckDependencies(DispatchQueue queue)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (QueueTask task in queue.Tasks.OrderBy(item => item.Index))
        {
            if (string.IsNullOrWhiteSpace(task.Id) || !seen.Add(task.Id))
                return "队列任务 ID 缺失或重复，无法确认后继依赖";
            IReadOnlyList<string> dependencies = task.DependsOnTaskIds ?? [];
            if (dependencies.Count > 64 || dependencies.Any(id => !seen.Contains(id) || id == task.Id)
                || dependencies.Distinct(StringComparer.Ordinal).Count() != dependencies.Count)
                return "队列任务依赖必须引用不重复的前序任务 ID";
        }
        return null;
    }

    /// <summary>
    /// 队列长时/普通混排校验：队列链式串行执行，长时脚本（日志无更新上限为 -1）可能持续运行并阻塞后续任务——
    /// 长时脚本实例不能与普通脚本实例编排进同一队列。任务不足两项或全部同类时通过。
    /// </summary>
    public static string? CheckQueueMix(IEnumerable<ScriptInstance> scripts, DispatchQueue queue)
    {
        List<ScriptInstance> tasks = queue.Tasks
            .Select(task => scripts.FirstOrDefault(script => script.Id == task.ScriptInstanceId))
            .Where(script => script is not null)
            .Cast<ScriptInstance>()
            .ToList();
        if (tasks.Count < 2)
        {
            return null;
        }
        bool hasLong = tasks.Any(script => script.IsLongRunning);
        bool hasNormal = tasks.Any(script => !script.IsLongRunning);
        if (hasLong && hasNormal)
        {
            return "队列不能混合编排长时脚本（日志无更新上限为 -1）与普通脚本实例，请分开建立队列";
        }
        return null;
    }

    /// <summary>队列任务的启用绑定总数：各任务引用脚本的启用绑定数之和，每个任务至少计 1。</summary>
    public static int QueueTotalUsers(
        IReadOnlyList<ScriptInstance> scripts,
        IQueueUserParticipationReader users,
        DispatchQueue queue)
    {
        return queue.Tasks.Sum(task =>
        {
            ScriptInstance? script = scripts.FirstOrDefault(item => item.Id == task.ScriptInstanceId);
            if (script is null)
            {
                return 1;
            }
            int enabled = users.CountParticipatingBindings(script.Id);
            return enabled < 1 ? 1 : enabled;
        });
    }
}

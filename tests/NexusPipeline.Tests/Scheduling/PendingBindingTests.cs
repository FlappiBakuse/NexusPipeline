using Xunit;
using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scheduling;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users;
namespace NexusPipeline.Tests.Scheduling;


public sealed class PendingBindingTests
{

    [Fact]
    public void PendingFrozenPlan_BlocksOnlyMatchingUserAndScriptBinding()
    {
        HostCompositionRoot context = HostCompositionRoot.Instance;
        string scriptId = "regression-pending-" + Guid.NewGuid().ToString("N");
        string unrelatedScriptId = "regression-unrelated-" + Guid.NewGuid().ToString("N");
        string userId = Guid.NewGuid().ToString("N");
        string queueId = "regression-queue-" + Guid.NewGuid().ToString("N");
        var script = new ScriptInstance { Id = scriptId, Name = "pending script" };
        var task = new PlannedQueueTask(
            new QueueTask { ScriptInstanceId = scriptId },
            script,
            new[] { userId },
            new[]
            {
                new ResolvedScriptUser(
                    userId,
                    "pending-user",
                    new UserScriptBinding { ScriptInstanceId = scriptId }),
            });
        var queue = new DispatchQueue { Id = queueId, Name = "pending queue" };
        var plan = new QueueExecutionPlan(
            queue,
            new[] { task },
            ExecutionAdmissionProfile.ForQueue(queue, new[] { task }),
            1);

        string key = context.Scheduler.AddPendingForTest(plan, queueId, "regression-occurrence");
        try
        {
            Assert.True(context.Scheduler.HasPendingBinding(userId, scriptId));
            Assert.False(context.Scheduler.HasPendingBinding(userId, unrelatedScriptId));
            Assert.False(context.Scheduler.HasPendingBinding(Guid.NewGuid().ToString("N"), scriptId));

        }
        finally
        {
            context.Scheduler.RemoveOccurrenceForTest(key);
        }
    }

    private static void RestoreFile(string path, bool existed, byte[]? bytes)
    {
        if (existed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes!);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteExactDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

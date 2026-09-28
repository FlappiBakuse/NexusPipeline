using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Queues.Validation;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class QueueDependencyPolicyTests
{
    [Fact]
    public void OnlyEarlierQueueItemsMayBeDeclaredAsPrerequisites()
    {
        var queue = new DispatchQueue
        {
            Tasks =
            [
                new QueueTask { Id = "a", Index = 0 },
                new QueueTask { Id = "b", Index = 1, DependsOnTaskIds = ["a"] },
                new QueueTask { Id = "c", Index = 2 },
            ],
        };
        Assert.Null(QueueCompositionPolicy.CheckDependencies(queue));
        Assert.Equal("a", Assert.Single(queue.Clone().Tasks[1].DependsOnTaskIds));

        queue.Tasks[0].DependsOnTaskIds = ["b"];
        Assert.NotNull(QueueCompositionPolicy.CheckDependencies(queue));
        queue.Tasks[0].DependsOnTaskIds = [];
        queue.Tasks[1].DependsOnTaskIds = ["a", "a"];
        Assert.NotNull(QueueCompositionPolicy.CheckDependencies(queue));
        queue.Tasks[1].DependsOnTaskIds = ["missing"];
        Assert.NotNull(QueueCompositionPolicy.CheckDependencies(queue));
    }
}

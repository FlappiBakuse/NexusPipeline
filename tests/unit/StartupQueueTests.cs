using NexusPipeline.Core.Scheduling;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class StartupQueueTests
{
    [Fact(DisplayName = "A Startup Generation Owns One Run Identity Despite Later Definition Revisions")]
    public void KeepsGenerationIdentity()
    {
        var instance = Guid.NewGuid(); var generation = Guid.NewGuid(); var queue = Guid.NewGuid();
        var original = StartupQueuePolicy.CreateOccurrence(instance, generation, queue, 1, 1, 0);
        var edited = StartupQueuePolicy.CreateOccurrence(instance, generation, queue, 2, 4, 3);
        Assert.Equal(original.OccurrenceId, edited.OccurrenceId); Assert.Equal(original.RunId, edited.RunId);
        Assert.Equal(original.StartOperationId, edited.StartOperationId); Assert.NotEqual(original.QueueRevision, edited.QueueRevision);
        var restarted = StartupQueuePolicy.CreateOccurrence(instance, Guid.NewGuid(), queue, 2, 4, 3);
        Assert.NotEqual(original.OccurrenceId, restarted.OccurrenceId); Assert.NotEqual(original.RunId, restarted.RunId);
        Assert.Equal(3, new[] { original.OccurrenceId, original.RunId, original.StartOperationId }.Distinct().Count());
    }

    [Theory(DisplayName = "Invalid Startup Sources Cannot Create Occurrence Identities")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RejectsInvalidSources(int invalid)
    {
        Assert.Throws<ArgumentException>(() => StartupQueuePolicy.CreateOccurrence(invalid == 0 ? Guid.Empty : Guid.NewGuid(),
            invalid == 1 ? Guid.Empty : Guid.NewGuid(), invalid == 2 ? Guid.Empty : Guid.NewGuid(),
            invalid == 3 ? 0 : 1, invalid == 4 ? 0 : 1, invalid == 5 ? -1 : 0));
    }
}

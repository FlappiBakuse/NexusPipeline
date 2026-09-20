using Xunit;
using NexusPipeline.Modules.Execution.Monitoring;
namespace NexusPipeline.Tests.Execution;


public sealed class LogCandidateStartPolicyTests
{

    [Fact]
    public void LogCandidateStartPolicy_ResumesOldCandidateAndStartsNewOrReplacedCandidate()
    {
        LogCandidateSnapshot before = new(100, "fileid:old");

        (bool oldFromStart, long oldPosition) = LogMonitor.DecideStart(before, new LogCandidateSnapshot(150, "fileid:old"));
        (bool newFromStart, long newPosition) = LogMonitor.DecideStart(before, new LogCandidateSnapshot(20, "fileid:new"));
        (bool missingFromStart, long missingPosition) = LogMonitor.DecideStart(null, new LogCandidateSnapshot(20, "fileid:new"));

        Assert.False(oldFromStart);
        Assert.Equal(100, oldPosition);
        Assert.True(newFromStart);
        Assert.Equal(0, newPosition);
        Assert.True(missingFromStart);
        Assert.Equal(0, missingPosition);
    }
}

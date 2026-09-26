using System.Text.Json;
using NexusPipeline.Modules.History;
using Xunit;

namespace NexusPipeline.Tests.History;

public sealed class RunOutcomeDimensionsTests
{
    [Fact]
    public void LegacyHistoryDoesNotAcquireVerifiedOutcomeDuringReadOrClone()
    {
        const string legacy = """{"Id":"old","Status":"success","ResultCode":"tasks.all_satisfied"}""";
        RunRecord record = JsonSerializer.Deserialize<RunRecord>(legacy)!;
        Assert.Null(record.Outcomes);
        Assert.Null(record.Clone().Outcomes);
        Assert.DoesNotContain("Outcomes", JsonSerializer.Serialize(record), StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentFactsRoundTripWithoutChangingLegacyStatus()
    {
        var record = new RunRecord
        {
            Status = "partial",
            Outcomes = new("succeeded", "unverified", "completed", "quarantined"),
        };
        RunRecord copy = JsonSerializer.Deserialize<RunRecord>(JsonSerializer.Serialize(record))!;
        Assert.True(copy.Outcomes!.IsValid);
        Assert.Equal("partial", copy.Status);
        Assert.Equal("quarantined", copy.Outcomes.RecoveryOutcome);
    }
}

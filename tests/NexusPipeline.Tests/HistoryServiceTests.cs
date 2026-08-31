using NexusPipeline.Models;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class HistoryServiceTests
{
    [Fact]
    public void CountSuccessfulRunsByUserCountsOnlySameDayFinalSuccess()
    {
        DateTime date = new(2026, 8, 31, 12, 0, 0);
        var records = new[]
        {
            Record("u1", "success", "success", date.AddHours(-2)),
            Record("u1", "success", "partial", date.AddHours(-1)),
            Record("u1", "success", "success", date.AddDays(-1)),
            Record("u2", "success", "", date.AddHours(-3)),
            Record("u3", "failed", "failed", date.AddHours(-4)),
            Record("u4", "success", "success", date.AddHours(-4), "other-script"),
            Record("", "success", "success", date.AddHours(-5)),
        };

        IReadOnlyDictionary<string, int> result = HistoryService.CountSuccessfulRunsByUser(
            records,
            date,
            "script-1");

        Assert.Equal(1, result["u1"]);
        Assert.Equal(1, result["u2"]);
        Assert.DoesNotContain("u3", result.Keys);
        Assert.DoesNotContain("u4", result.Keys);
        Assert.Equal(2, result.Count);
    }

    private static RunRecord Record(
        string userId,
        string status,
        string finalStatus,
        DateTime startTime,
        string scriptId = "script-1") => new()
    {
        ScriptInstanceId = scriptId,
        UserId = userId,
        StartTime = startTime,
        Status = status,
        FinalStatus = finalStatus,
    };
}

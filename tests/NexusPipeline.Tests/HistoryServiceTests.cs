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
            Record("u1", "Alice", "success", "success", date.AddHours(-2)),
            Record("u1", "Alice", "success", "partial", date.AddHours(-1)),
            Record("u1", "Alice", "success", "success", date.AddDays(-1)),
            Record("u2", "Bob", "success", "", date.AddHours(-3)),
            Record("u3", "Cara", "failed", "failed", date.AddHours(-4)),
            Record("u4", "Drew", "success", "success", date.AddHours(-4), "other-script"),
            Record("", "", "success", "success", date.AddHours(-5)),
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

    [Fact]
    public void SummarizeUsersGroupsByStableIdAndLegacyName()
    {
        DateTime date = new(2026, 8, 31, 12, 0, 0);
        var records = new[]
        {
            Record("u1", "Alice", "success", "success", date.AddHours(-1)),
            Record("u1", "Alice", "failed", "failed", date.AddHours(-2)),
            Record("u2", "Bob", "partial", "partial", date.AddHours(-3)),
            Record("", "旧用户", "cancelled", "cancelled", date.AddHours(-4)),
            Record("", "旧用户", "skipped", "skipped", date.AddHours(-5)),
            Record("", "", "failed", "failed", date.AddHours(-6)),
            Record("u1", "Alice", "success", "success", date.AddDays(-1)),
            Record("other", "Other", "success", "success", date.AddHours(-2), "other-script"),
        };

        List<HistoryUserSummary> result = HistoryService.SummarizeUsers(records, date, "script-1");

        Assert.Equal(4, result.Count);
        HistoryUserSummary alice = Assert.Single(result, item => item.UserKey == "id:u1");
        Assert.Equal("Alice", alice.UserName);
        Assert.Equal(2, alice.Count);
        Assert.Equal(1, alice.SuccessCount);
        Assert.Equal(1, alice.FailedCount);
        HistoryUserSummary legacy = Assert.Single(result, item => item.UserKey == "legacy:旧用户");
        Assert.Equal(2, legacy.Count);
        Assert.Equal(1, legacy.CancelledCount);
        Assert.Equal(1, legacy.SkippedCount);
        HistoryUserSummary unknown = Assert.Single(result, item => item.UserKey == "legacy:");
        Assert.Equal("未指定用户", unknown.UserName);
    }

    private static RunRecord Record(
        string userId,
        string userName,
        string status,
        string finalStatus,
        DateTime startTime,
        string scriptId = "script-1") => new()
    {
        ScriptInstanceId = scriptId,
        UserId = userId,
        UserName = userName,
        StartTime = startTime,
        Status = status,
        FinalStatus = finalStatus,
    };
}

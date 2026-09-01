using System.Text;
using System.Text.Json;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
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

    [Fact]
    public void SaveStoresNestedRunDirectoryLogsAndEightScreenshotsPerAttempt()
    {
        string root = CreateTempDirectory();
        try
        {
            string historyRoot = Path.Combine(root, "history");
            var service = new HistoryService(
                historyRoot,
                Path.Combine(root, "output"),
                Path.Combine(root, "logs"));
            DateTime start = new(2026, 8, 31, 14, 58, 21);
            var record = Record("u1", "测试用户", "failed", "failed", start);
            record.Id = "run-nested-1";
            record.Attempts = 2;
            record.MaxAttempts = 2;
            record.AttemptDetails = new List<RunAttempt>
            {
                new() { Number = 1, StartTime = start, Status = "failed", Reason = "第一次失败" },
                new() { Number = 2, StartTime = start.AddMinutes(1), Status = "failed", Reason = "第二次失败" },
            };
            var screenshots = Enumerable.Range(1, 9)
                .Select(index => Screenshot($"shot-{index}", index, 1, (byte)index))
                .Concat(Enumerable.Range(1, 2).Select(index => Screenshot($"retry-shot-{index}", 20 + index, 2, (byte)(20 + index))))
                .ToList();

            HistorySaveResult saved = service.Save(record, new List<string> { "attempt one", "attempt two" }, screenshots);
            Assert.Null(saved.PersistenceWarning);
            Assert.Equal(Path.Combine("测试用户", "14-58-21"), saved.Record.HistoryDirectory);

            string runDirectory = Path.Combine(historyRoot, "2026-08-31", saved.Record.HistoryDirectory);
            Assert.True(File.Exists(Path.Combine(runDirectory, "14-58-21.json")));
            Assert.Equal("attempt one", File.ReadAllText(Path.Combine(runDirectory, "14-58-21-1.log"), Encoding.UTF8).Trim().TrimStart('\uFEFF'));
            Assert.Equal("attempt two", File.ReadAllText(Path.Combine(runDirectory, "14-58-21-2.log"), Encoding.UTF8).Trim().TrimStart('\uFEFF'));

            RunAttempt firstAttempt = Assert.Single(saved.Record.AttemptDetails, attempt => attempt.Number == 1);
            Assert.Equal(8, firstAttempt.Screenshots.Count);
            Assert.Equal("shot-2", firstAttempt.Screenshots[0].Id);
            Assert.Equal("shot-9", firstAttempt.Screenshots[^1].Id);
            Assert.Equal("14-58-21-1-s1.jpg", firstAttempt.Screenshots[0].FileName);
            Assert.Equal("14-58-21-1-s8.jpg", firstAttempt.Screenshots[^1].FileName);
            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(runDirectory, "14-58-21-1-s8.jpg")));

            RunAttempt secondAttempt = Assert.Single(saved.Record.AttemptDetails, attempt => attempt.Number == 2);
            Assert.Equal(new[] { "retry-shot-1", "retry-shot-2" }, secondAttempt.Screenshots.Select(item => item.Id));
            Assert.Equal("14-58-21-2-s1.jpg", secondAttempt.Screenshots[0].FileName);

            List<RunRecord> queried = service.Query(start.Date, start.Date.AddDays(1).AddTicks(-1));
            RunRecord persisted = Assert.Single(queried);
            Assert.Equal("run-nested-1", persisted.Id);
            Assert.Equal(new byte[] { 9 }, service.ReadScreenshot(persisted, 1, "shot-9"));
            Assert.Equal("attempt two", service.ReadScriptLog(persisted, 2)?.LogText.Trim().TrimStart('\uFEFF'));

            HistorySaveResult collision = service.Save(record, new List<string> { "attempt one", "attempt two" }, screenshots);
            Assert.Equal(Path.Combine("测试用户", "14-58-21-2"), collision.Record.HistoryDirectory);
            Assert.Equal(2, service.Query(start.Date, start.Date.AddDays(1).AddTicks(-1)).Count);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void MigrateLegacyHistoryIntoUserRunDirectoryAndIsIdempotent()
    {
        string root = CreateTempDirectory();
        try
        {
            string historyRoot = Path.Combine(root, "history");
            string dayDirectory = Path.Combine(historyRoot, "2026-08-31");
            Directory.CreateDirectory(dayDirectory);
            string jsonPath = Path.Combine(dayDirectory, "14-58-21.json");
            string logPath = Path.Combine(dayDirectory, "14-58-21-1.log");
            var record = Record("u1", "迁移用户", "success", "success", new DateTime(2026, 8, 31, 14, 58, 21));
            record.Id = "legacy-run-1";
            record.LogFile = "14-58-21.json";
            record.Attempts = 1;
            record.AttemptDetails = new List<RunAttempt>
            {
                new()
                {
                    Number = 1,
                    StartTime = record.StartTime,
                    Status = "success",
                    LogFile = "14-58-21-1.log",
                },
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(record));
            File.WriteAllText(logPath, "legacy log", Encoding.UTF8);

            var service = new HistoryService(
                historyRoot,
                Path.Combine(root, "output"),
                Path.Combine(root, "logs"));
            service.MigrateLegacy();

            string runDirectory = Path.Combine(historyRoot, "2026-08-31", "迁移用户", "14-58-21");
            Assert.False(File.Exists(jsonPath));
            Assert.False(File.Exists(logPath));
            Assert.True(File.Exists(Path.Combine(runDirectory, "14-58-21.json")));
            Assert.Equal("legacy log", File.ReadAllText(Path.Combine(runDirectory, "14-58-21-1.log"), Encoding.UTF8).Trim());
            Assert.Equal("legacy-run-1", Assert.Single(service.Query(record.StartTime.Date, record.StartTime.Date.AddDays(1).AddTicks(-1))).Id);

            service.MigrateLegacy();
            Assert.Single(service.Query(record.StartTime.Date, record.StartTime.Date.AddDays(1).AddTicks(-1)));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static RunScreenshot Screenshot(string id, long ordinal, int attempt, byte value) =>
        new(
            id,
            ordinal,
            new DateTimeOffset(2026, 8, 31, 14, 58, 21, TimeSpan.FromHours(8)).AddSeconds(ordinal),
            attempt,
            640,
            480,
            "pc",
            "judge-manual",
            new[] { value });

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "NexusPipeline.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
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

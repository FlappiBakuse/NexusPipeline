using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal static class HistoryReadBenchmark
{
    internal static async Task MeasureAsync(HistoryReadServer server, string range)
    {
        var cursor = "&afterEndedAt=" + Uri.EscapeDataString(HistoryReadSeed.From.AddTicks(98999).UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
            + "&afterRunId=" + HistoryReadSeed.Id("10000000", 99000);
        var queries = new (string Name, string Path, Action<JsonElement> Check)[]
        {
            ("first-page", "control/v1/records" + range + "&limit=200", value => Page(value, 1)),
            ("deep-page", "control/v1/records" + range + "&limit=200" + cursor, value => Page(value, 99001)),
            ("empty-groups", "control/v1/records" + range + "&limit=200&scope=groups",
                value => Assert.Empty(value.GetProperty("items").EnumerateArray())),
            ("account-page", "control/v1/records" + range + "&limit=200&userId=" + HistoryReadSeed.Id("20000000", 1), value => Page(value, 1)),
            ("days", "control/v1/records/summary/days" + range + "&offsetMinutes=540",
                value => Assert.Equal(HistoryReadSeed.Count, value.GetProperty("totals").GetProperty("runs").GetInt32())),
            ("users-first", "control/v1/records/summary/users" + range + "&offsetMinutes=540",
                value => Assert.Equal(200, value.GetProperty("users").GetArrayLength())),
            ("users-deep", "control/v1/records/summary/users" + range + "&offsetMinutes=540&afterUserId=" + HistoryReadSeed.Id("20000000", 9800),
                value => Assert.Equal(200, value.GetProperty("users").GetArrayLength()))
        };
        var results = new List<object>();
        foreach (var (name, path, check) in queries)
        {
            var samples = new List<double>();
            var sizes = new List<int>();
            for (var index = 0; index < 21; index++)
            {
                var start = Stopwatch.GetTimestamp();
                using var response = await server.Client.GetAsync(path);
                var bytes = await response.Content.ReadAsByteArrayAsync();
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                sizes.Add(bytes.Length);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(bytes);
                check(document.RootElement);
            }
            var warm = samples.Skip(1).Order().ToArray();
            results.Add(new { name, firstMilliseconds = samples[0], medianMilliseconds = (warm[9] + warm[10]) / 2,
                p95Milliseconds = warm[18], maximumMilliseconds = warm[^1], samplesMilliseconds = samples,
                maximumResponseBytes = sizes.Max() });
        }
        var report = new { records = HistoryReadSeed.Count, users = 10001, pageLimit = 200,
            databaseBytes = new FileInfo(server.Database.DatabasePath).Length,
            sqlite = server.Database.ReadSettings().NativeVersion,
            method = "Actual loopback HTTP authentication, production routes and SQLite queries; first request plus 20 repeated requests per query; OS caches retained",
            scope = "Synthetic persisted read-model rows; execution journals and full Host startup are measured separately", results };
        await File.WriteAllTextAsync(Path.Combine(server.Root, "history-read-benchmark.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report, JsonSerializerOptions.Web));
    }

    private static void Page(JsonElement value, int first)
    {
        var items = value.GetProperty("items");
        Assert.Equal(200, items.GetArrayLength());
        Assert.Equal(HistoryReadSeed.Id("10000000", first), items[0].GetProperty("finalAttempt").GetProperty("runId").GetGuid());
        Assert.Equal(HistoryReadSeed.Id("10000000", first + 199), items[199].GetProperty("finalAttempt").GetProperty("runId").GetGuid());
    }
}

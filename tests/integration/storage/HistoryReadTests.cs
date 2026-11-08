using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class HistoryReadTests
{
    [Fact(DisplayName = "Actual Record Http Routes Query Every One Of One Hundred Thousand Sqlite Rows And Remote User Identities")]
    public async Task ServesLargeHistory()
    {
        await using var server = await HistoryReadServer.StartAsync();
        var query = "?endedFromInclusive=" + Uri.EscapeDataString(HistoryReadSeed.From.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
            + "&endedUntilExclusive=" + Uri.EscapeDataString(HistoryReadSeed.From.AddDays(1).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        await HistoryReadBenchmark.MeasureAsync(server, query);
        Assert.Equal(HistoryReadSeed.Count, await ReadRecords(server.Client, query));
        Assert.Equal(10001, await ReadUsers(server.Client, query));
        using var daysResponse = await server.Client.GetAsync("control/v1/records/summary/days" + query + "&offsetMinutes=540");
        Assert.Equal(HttpStatusCode.OK, daysResponse.StatusCode);
        using var days = JsonDocument.Parse(await daysResponse.Content.ReadAsStringAsync());
        Assert.Equal(HistoryReadSeed.Count, days.RootElement.GetProperty("totals").GetProperty("runs").GetInt32());
        Assert.Equal(HistoryReadSeed.Count, days.RootElement.GetProperty("days").EnumerateArray()
            .Sum(day => day.GetProperty("totals").GetProperty("runs").GetInt32()));
        Assert.False(days.RootElement.TryGetProperty("users", out _));
        foreach (var path in new[] { "summary", "summary/days", "summary/users" })
        {
            using var invalid = await server.Client.GetAsync("control/v1/records/" + path + query + "&offsetMinutes=841");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var unknown = await server.Client.GetAsync("control/v1/records/" + path + query + "&unknown=true");
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }
        using var oldest = await server.Client.GetAsync("control/v1/records/" + HistoryReadSeed.Id("10000000", 1));
        Assert.Equal(HttpStatusCode.OK, oldest.StatusCode);
        using var record = JsonDocument.Parse(await oldest.Content.ReadAsStringAsync());
        Assert.Equal(HistoryReadSeed.Id("10000000", 1), record.RootElement.GetProperty("finalAttempt").GetProperty("runId").GetGuid());
        using var account = await server.Client.GetAsync("control/v1/records/" + HistoryReadSeed.Id("10000000", 1)
            + "?userId=" + HistoryReadSeed.Id("20000000", 1));
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        using var accountRecord = JsonDocument.Parse(await account.Content.ReadAsStringAsync());
        Assert.Equal(HistoryReadSeed.Id("20000000", 1), accountRecord.RootElement.GetProperty("userId").GetGuid());
        using var foreign = await server.Client.GetAsync("control/v1/records/" + HistoryReadSeed.Id("10000000", 1)
            + "?userId=" + HistoryReadSeed.Id("20000000", 2));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var denied = await server.Client.PostAsync("control/v1/records", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, denied.StatusCode);
        await server.CoordinateAsync();
    }

    private static async Task<int> ReadRecords(HttpClient client, string query)
    {
        string? beforeTime = null;
        string? beforeRun = null;
        var count = 0;
        do
        {
            var cursor = beforeTime is null ? "" : "&afterEndedAt=" + Uri.EscapeDataString(beforeTime) + "&afterRunId=" + beforeRun;
            using var response = await client.GetAsync("control/v1/records" + query + "&limit=200" + cursor);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = page.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.InRange(items.Length, 1, 200);
            foreach (var item in items)
            {
                Assert.Equal(HistoryReadSeed.Id("10000000", count + 1),
                    item.GetProperty("finalAttempt").GetProperty("runId").GetGuid());
                count++;
            }
            if (!page.RootElement.TryGetProperty("next", out var next) || next.ValueKind is JsonValueKind.Null) break;
            beforeTime = next.GetProperty("endedAt").GetString();
            beforeRun = next.GetProperty("runId").GetString();
        } while (true);
        return count;
    }

    private static async Task<int> ReadUsers(HttpClient client, string query)
    {
        string? after = null;
        var users = 0;
        do
        {
            using var response = await client.GetAsync("control/v1/records/summary/users" + query + "&offsetMinutes=540"
                + (after is null ? "" : "&afterUserId=" + after));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var summary = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(summary.RootElement.TryGetProperty("totals", out _));
            Assert.False(summary.RootElement.TryGetProperty("days", out _));
            var items = summary.RootElement.GetProperty("users").EnumerateArray().ToArray();
            Assert.InRange(items.Length, 1, 200);
            if (users == 0) Assert.Equal(90000, items[0].GetProperty("totals").GetProperty("runs").GetInt32());
            users += items.Length;
            after = summary.RootElement.TryGetProperty("nextUserId", out var next) && next.ValueKind is not JsonValueKind.Null
                ? next.GetString() : null;
        } while (after is not null);
        return users;
    }

}

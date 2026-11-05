using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class DateFilterTests
{
    [Theory(DisplayName = "Utc Half Open Dates Compose With Indexed Filters And Stable Timestamp Tie Cursors")]
    [InlineData("all")]
    [InlineData("automation")]
    [InlineData("user")]
    [InlineData("business")]
    [InlineData("combined")]
    public void PagesUtcFilters(string filter)
    {
        var (_, store) = RecordStoreFixture.NewStore();
        var automation = Guid.NewGuid(); var other = Guid.NewGuid(); var user = Guid.NewGuid();
        var records = Enumerable.Range(0, 140).Select(index =>
        {
            var value = RecordStoreFixture.Record(index, index % 2 == 0 ? automation : other);
            return value with { UserId = index % 3 == 0 ? user : null,
                FinalAttempt = value.FinalAttempt with { Business = index % 2 == 0 ? BusinessOutcome.Succeeded : BusinessOutcome.Failed } };
        }).ToArray();
        store.AppendCompleted(records);
        var from = records.Min(record => record.EndedAt).AddSeconds(5); var until = from.AddSeconds(5);
        var query = new RecordQuery(3, EndedFromInclusive: from, EndedUntilExclusive: until);
        query = filter switch
        {
            "automation" => query with { AutomationId = automation },
            "user" => query with { UserId = user },
            "business" => query with { Business = BusinessOutcome.Succeeded },
            "combined" => query with { AutomationId = automation, UserId = user, Business = BusinessOutcome.Succeeded },
            _ => query
        };
        var expected = records.Where(record => record.EndedAt >= from && record.EndedAt < until &&
            (query.AutomationId is null || record.AutomationId == query.AutomationId) &&
            (query.UserId is null || record.UserId == query.UserId) &&
            (query.Business is null || record.FinalAttempt.Business == query.Business))
            .OrderByDescending(record => record.EndedAt).ThenByDescending(record => record.FinalAttempt.RunId.ToString("D"), StringComparer.Ordinal).ToArray();
        var actual = new List<CompletedRunRecord>(); RecordCursor? cursor = null;
        do
        {
            var request = query with { After = cursor };
            var plan = store.ExplainPage(request);
            Assert.Contains(plan, line => line.Contains("SEARCH", StringComparison.Ordinal) && line.Contains("run_records_", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
            var page = store.ReadPage(request); Assert.InRange(page.Items.Count, 1, 3);
            actual.AddRange(page.Items); cursor = page.Next;
        } while (cursor is not null);
        Assert.NotEmpty(expected); Assert.Equal(expected, actual);
        Assert.Contains(actual, record => record.EndedAt == from);
        Assert.DoesNotContain(actual, record => record.EndedAt == until);
        Assert.Equal(actual.Count, actual.Select(record => record.FinalAttempt.RunId).Distinct().Count());
    }

    [Theory(DisplayName = "Invalid Utc Bounds Are Rejected By Both The Core Query And The Actual Read Only Page Without Changing Stored Facts")]
    [InlineData("fromNonUtc")]
    [InlineData("untilNonUtc")]
    [InlineData("fromDefault")]
    [InlineData("untilDefault")]
    [InlineData("equal")]
    [InlineData("reverse")]
    public void RejectsInvalidDates(string fault)
    {
        var (_, store) = RecordStoreFixture.NewStore(); var original = RecordStoreFixture.Record(1, Guid.NewGuid());
        store.AppendCompleted([original]); var point = original.EndedAt;
        var query = fault switch
        {
            "fromNonUtc" => new RecordQuery(EndedFromInclusive: point.ToOffset(TimeSpan.FromHours(8))),
            "untilNonUtc" => new RecordQuery(EndedUntilExclusive: point.ToOffset(TimeSpan.FromHours(-3))),
            "fromDefault" => new RecordQuery(EndedFromInclusive: default(DateTimeOffset)),
            "untilDefault" => new RecordQuery(EndedUntilExclusive: default(DateTimeOffset)),
            "equal" => new RecordQuery(EndedFromInclusive: point, EndedUntilExclusive: point),
            "reverse" => new RecordQuery(EndedFromInclusive: point.AddTicks(1), EndedUntilExclusive: point),
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<ArgumentException>(() => query.Validate());
        Assert.Throws<ArgumentException>(() => store.ReadPage(query));
        Assert.Throws<ArgumentException>(() => store.ExplainPage(query));
        Assert.Equal(original, Assert.Single(store.ReadPage(new()).Items));
    }
}

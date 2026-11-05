using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class RecordStoreTests
{
    [Fact(DisplayName = "Ascending Keyset Pages Keep Timestamp Ties And Include Later Insertions Once")]
    public void KeepsStablePages()
    {
        var (_, store) = RecordStoreFixture.NewStore();
        var automation = Guid.NewGuid();
        var records = Enumerable.Range(0, 233).Select(index => RecordStoreFixture.Record(index, automation)).ToArray();
        store.AppendCompleted(records);
        var expected = records.OrderBy(item => item.EndedAt).ThenBy(item => item.FinalAttempt.RunId.ToString("D"), StringComparer.Ordinal).ToArray();
        var first = store.ReadPage(new(50));
        Assert.Equal(expected.Take(50), first.Items);
        var concurrent = RecordStoreFixture.Record(1000, automation) with { EndedAt = records.Max(item => item.EndedAt).AddDays(1) };
        store.AppendCompleted([concurrent]);
        var collected = new List<CompletedRunRecord>(first.Items);
        var cursor = first.Next;
        while (cursor is not null)
        {
            var page = store.ReadPage(new(50, After: cursor));
            Assert.InRange(page.Items.Count, 1, 50);
            collected.AddRange(page.Items);
            cursor = page.Next;
        }
        Assert.Equal(expected.Append(concurrent), collected);
        Assert.Equal(collected.Count, collected.Select(item => item.FinalAttempt.RunId).Distinct().Count());
    }

    [Theory(DisplayName = "Filtered History Uses Ordered Indexes And Returns Only Its Frozen Facts")]
    [InlineData("all", "run_records_ended")]
    [InlineData("automation", "run_records_automation_ended")]
    [InlineData("user", "run_records_user_ended")]
    [InlineData("business", "run_records_business_ended")]
    public void UsesFilteredIndexes(string filter, string index)
    {
        var (_, store) = RecordStoreFixture.NewStore();
        var automation = Guid.NewGuid();
        var user = Guid.NewGuid();
        var selected = Enumerable.Range(0, 120).Select(number => RecordStoreFixture.Record(number, automation) with { UserId = user }).ToArray();
        store.AppendCompleted(selected);
        store.AppendCompleted([RecordStoreFixture.Record(500, Guid.NewGuid()) with { FinalAttempt = RecordStoreFixture.Record(500, Guid.NewGuid()).FinalAttempt with { Business = BusinessOutcome.Failed } }]);
        var query = filter switch
        {
            "all" => new RecordQuery(), "automation" => new(AutomationId: automation),
            "user" => new(UserId: user), "business" => new(Business: BusinessOutcome.Succeeded),
            _ => throw new InvalidOperationException()
        };
        var plan = store.ExplainPage(query);
        Assert.Contains(plan, item => item.Contains(index, StringComparison.Ordinal));
        Assert.DoesNotContain(plan, item => item.Contains("TEMP B-TREE", StringComparison.Ordinal));
        var page = store.ReadPage(query);
        Assert.Equal(50, page.Items.Count);
        if (filter != "all") Assert.All(page.Items, item => Assert.Equal(automation, item.AutomationId));
        Assert.NotNull(page.Next);
        Assert.Contains(store.ExplainPage(query with { After = page.Next }), item => item.Contains(index, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Completed Records Are Append Only And A Rejected Batch Cannot Publish Partial History")]
    public void PreservesAppendHistory()
    {
        var (_, store) = RecordStoreFixture.NewStore();
        var original = RecordStoreFixture.Record(1, Guid.NewGuid());
        store.AppendCompleted([original]);
        var independent = RecordStoreFixture.Record(2, Guid.NewGuid());
        Assert.Throws<SqliteException>(() => store.AppendCompleted([independent, original with { DurationMilliseconds = 9000 }]));
        Assert.Equal(original, Assert.Single(store.ReadPage(new()).Items));
        Assert.Throws<SqliteException>(() => store.AppendCompleted([independent with { FinalAttempt = independent.FinalAttempt with { AttemptId = original.FinalAttempt.AttemptId } }]));
        Assert.Equal(original, Assert.Single(store.ReadPage(new()).Items));
        var cancelled = independent with { FinalAttempt = independent.FinalAttempt with { Lifecycle = LifecycleOutcome.Cancelled } };
        store.AppendCompleted([cancelled]);
        Assert.Equal(BusinessOutcome.Succeeded, Assert.Single(store.ReadPage(new(Lifecycle: LifecycleOutcome.Cancelled)).Items).FinalAttempt.Business);
        Assert.Equal(cancelled, Assert.Single(store.ReadPage(new(Business: BusinessOutcome.Succeeded, Lifecycle: LifecycleOutcome.Cancelled)).Items));
        Assert.Empty(store.ReadPage(new(Business: BusinessOutcome.Failed, Lifecycle: LifecycleOutcome.Cancelled)).Items);
        Assert.Equal(original, Assert.Single(store.ReadPage(new(Lifecycle: LifecycleOutcome.Completed)).Items));
        Assert.Throws<ArgumentException>(() => store.ReadPage(new(Lifecycle: LifecycleOutcome.Running)));
    }

    [Theory(DisplayName = "Invalid Or Unbounded Record Requests Do Not Change Visible History")]
    [InlineData("active")]
    [InlineData("pending-recovery")]
    [InlineData("invalid-identity")]
    [InlineData("free-text")]
    [InlineData("empty-batch")]
    [InlineData("large-batch")]
    [InlineData("invalid-query")]
    public void RejectsInvalidQueries(string fault)
    {
        var (_, store) = RecordStoreFixture.NewStore();
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        Action invalid = fault switch
        {
            "active" => () => store.AppendCompleted([record with { FinalAttempt = record.FinalAttempt with { Lifecycle = LifecycleOutcome.Running } }]),
            "pending-recovery" => () => store.AppendCompleted([record with { FinalAttempt = record.FinalAttempt with { Recovery = RecoveryOutcome.Pending } }]),
            "invalid-identity" => () => store.AppendCompleted([record with { AutomationId = Guid.Empty }]),
            "free-text" => () => store.AppendCompleted([record with { FinalAttempt = record.FinalAttempt with { ReasonCode = "account=fixture-secret" } }]),
            "empty-batch" => () => store.AppendCompleted([]),
            "large-batch" => () => store.AppendCompleted(Enumerable.Repeat(record, 257).ToArray()),
            "invalid-query" => () => store.ReadPage(new(201)),
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<ArgumentException>(invalid);
        Assert.Empty(store.ReadPage(new()).Items);
    }

}

using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetirementHistoryTests
{
    [Fact(DisplayName = "Repeated Log Retirement Keeps Each Completed Run In History")]
    public async Task KeepsRetiredRuns()
    {
        using var fixture = new ExecutionHooksFixture(false, accept: false);
        var source = fixture.Source;
        var records = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, new()));
        var retirement = new ContentRetirementService(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index);
        var logs = new LogCatalogReader(source.Instance, source.Database, new());
        var registry = new RecoveryJournalRegistry(source.Database);
        var cleanup = new OwnedCleanupExecutor(source.Instance, new(), source.Database, fixture.Base.Index);
        var runs = new List<(LogCatalogQuery Query, LogCatalogItem[] Retired)>();
        for (var index = 0; index < 2; index++)
        {
            _ = fixture.Base.Service.Start(Guid.NewGuid(), ExecutionHooksFixture.Subject,
                source.Request(new BindingTarget(source.BindingA)), DateTimeOffset.UtcNow);
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            records.OnCommitted(receipt);
            var query = new LogCatalogQuery(receipt.Facts.RunId, receipt.Facts.AttemptId);
            var original = logs.ReadPage(query).Items;
            Assert.NotEmpty(original);
            var page = Assert.IsType<ContentRetirementPage>(
                retirement.RetireLogsPage(Guid.NewGuid(), "retirement-history", query.RunId));
            var journal = registry.ReadJournal(page.CleanupJournalId);
            using var lease = fixture.Base.Resources.TryAcquire(page.OperationId,
                journal.ResourceKeys.Select(key => new ResourceClaim(key, ResourceClaimMode.Mutation)).ToArray()).Lease;
            Assert.NotNull(lease);
            Assert.Equal(FileCleanupResult.Cleaned, cleanup.Execute(page.CleanupJournalId, source.Lease, lease));
            runs.Add((query, original.Select(item => item with
                { ContentAvailability = RecordContentAvailability.Retired }).ToArray()));
        }

        foreach (var (query, retired) in runs)
            Assert.Equal(retired, logs.ReadPage(query).Items.ToArray());
    }
}

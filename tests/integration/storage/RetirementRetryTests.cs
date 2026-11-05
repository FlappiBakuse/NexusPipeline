using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RetirementRetryTests
{
    [Fact(DisplayName = "Both Real Retry Attempts Remain Individually Bound When Their Final Run Retires Only Display Content")]
    public async Task RetiresIndividualAttempts()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: true, retryApproved: true, maximumAttempts: 2);
        var source = fixture.Source; var first = await fixture.RunInterruptedAsync();
        var retries = new ExecutionRetryService(source.Instance, source.Database, new(), source.Lease, fixture.Service);
        using var prepared = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        _ = retries.Start(prepared, DateTimeOffset.UtcNow);
        var second = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var projection = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, new()));
        projection.OnCommitted(second);
        new LogCatalogProjection(source.Instance, source.Database, new(), source.Lease).OnCommitted(first);
        var service = new ContentRetirementService(source.Instance, source.Database, new(), source.Lease, fixture.Base.Resources, fixture.Base.Index);
        Assert.NotNull(service.RetireLogsPage(Guid.NewGuid(), "retirement-fixture", second.Facts.RunId));
        var catalog = new LogCatalogReader(source.Instance, source.Database, new());
        Assert.All(catalog.ReadPage(new(first.Facts.RunId, first.Facts.AttemptId)).Items,
            item => Assert.NotEqual(RecordContentAvailability.Available, item.ContentAvailability));
        Assert.All(catalog.ReadPage(new(second.Facts.RunId, second.Facts.AttemptId)).Items,
            item => Assert.NotEqual(RecordContentAvailability.Available, item.ContentAvailability));
        Assert.Equal(first, new AttemptReceiptStore(source.Database).Read(first.Facts.AttemptId));
        Assert.Equal(second, new AttemptReceiptStore(source.Database).Read(second.Facts.AttemptId));
        Assert.Equal(1, fixture.Base.Count("run_records"));
    }
}

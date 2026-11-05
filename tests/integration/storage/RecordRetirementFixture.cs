using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class RecordRetirementFixture : IDisposable
{
    internal ExecutionHooksFixture Hooks { get; } = new(false);
    internal PlanBuilderFixture Source => Hooks.Source;
    internal AttemptReceipt Receipt { get; private set; } = null!;
    internal ContentRetirementService Retirement => new(Source.Instance, Source.Database, new(), Source.Lease, Hooks.Base.Resources, Hooks.Base.Index);
    internal ExecutionLogReader Logs => new(Source.Instance, Source.Database, new());
    internal LogCatalogReader Catalog => new(Source.Instance, Source.Database, new());
    internal IReadOnlyList<LogCatalogItem> Items => Catalog.ReadPage(new(Receipt.Facts.RunId, Receipt.Facts.AttemptId)).Items;

    internal static async Task<RecordRetirementFixture> CreateAsync(bool publishRecord = true)
    {
        var result = new RecordRetirementFixture();
        try
        {
            result.Receipt = Assert.IsType<AttemptReceipt>(await result.Hooks.Consumer.RunNextAsync());
            if (publishRecord)
                new ExecutionRecordProjection(result.Source.Instance, result.Source.Database, new(), result.Source.Lease,
                    new(result.Source.Instance, result.Source.Database, new())).OnCommitted(result.Receipt);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal FrozenFileCleanup Plan(ContentRetirementPage page)
    {
        var registered = new RecoveryJournalRegistry(Source.Database).ReadSnapshot().Journals.Single(item => item.JournalId == page.CleanupJournalId);
        return new CleanupPlanCodec(Source.Instance, new(), new(Source.Instance, new()))
            .Read(new(registered.PlanPath, new FileInfo(Path.Combine(Source.Instance.RuntimeRoot, registered.PlanPath)).Length, registered.PlanDigest));
    }

    internal FileCleanupResult Cleanup(ContentRetirementPage page) => WithCleanupRoot(page, root =>
        new OwnedCleanupExecutor(Source.Instance, new(), Source.Database, Hooks.Base.Index)
            .Execute(page.CleanupJournalId, Source.Lease, root));

    internal T WithCleanupRoot<T>(ContentRetirementPage page, Func<ResourceOperationLease, T> action)
    {
        var plan = Plan(page);
        var claims = plan.Items.Select(item => new ResourceClaim(CleanupCandidateFiles.ResourceKey(item.Blob), ResourceClaimMode.Mutation)).ToArray();
        using var root = Hooks.Base.Resources.TryAcquire(page.OperationId, claims).Lease;
        Assert.NotNull(root);
        using var borrow = root!.Borrow(Hooks.Base.Resources, page.OperationId, claims);
        return borrow.Use(() => action(root));
    }

    internal string PathOf(BlobReference reference) => Path.Combine(Source.Instance.RuntimeRoot, reference.RelativePath);

    internal void RequireOriginalMetadata(byte[] journal)
    {
        Assert.Equal(journal, new JournalStore(Source.Instance, new()).Read(Receipt.JournalId).Export());
        Assert.Equal(Receipt, new AttemptReceiptStore(Source.Database).Read(Receipt.Facts.AttemptId));
        Assert.Equal(1, Hooks.Base.Count("run_records"));
    }

    public void Dispose() => Hooks.Dispose();
}

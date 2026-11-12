using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ArchiveCleanupTests
{
    [Theory(DisplayName = "Archive Sweeps Retire A Superseded ZIP Only After Its Last Actual Reference Ends")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReclaimsOldPackage(bool shared)
    {
        await using var fixture = await ExtensionUpgradeFixture.CreateAsync(enabled: false);
        var owner = fixture.Original.Package.Catalog;
        var roots = fixture.Original.Roots;
        var before = fixture.Original.Binding().Read(fixture.Original.Accepted.JournalId);
        var old = before.Source.Package!.Archive;
        var blobs = new ImmutableBlobStore(owner.Instance, owner.Contracts);
        var references = new BlobReferenceIndex(owner.Instance, owner.Contracts, owner.Database, blobs, roots);
        fixture.Prepare(PayloadAction.Upgrade);
        fixture.Accept();
        var input = File.ReadAllBytes(fixture.Next.Archive);
        var digest = Convert.ToHexStringLower(SHA256.HashData(input));
        var next = new BlobReference($"blobs/content/{digest}.bin", input.LongLength, digest);
        Assert.True(references.IsReferenced(next));
        Assert.True(references.IsReferenced(old));
        _ = await fixture.Session().ApplyAsync(fixture.Prepared!, default);
        fixture.Prepared!.Dispose(); fixture.Verified!.Dispose(); fixture.Root!.Dispose();
        ExtensionProjectionPublication.PublishIfDirty(owner.Instance, owner.Database, owner.Lease, owner.Contracts, roots, owner.Resources, owner.Index);
        var orphan = blobs.PersistContent(new byte[] { 21, 19, 17, 2 }, default);
        if (shared)
        {
            var record = RecordStoreFixture.Record(1, Guid.NewGuid());
            new RunRecordStore(owner.Database).AppendCompleted([record]);
            new RecordAssetStore(owner.Database, blobs).AttachVerified(new(record.FinalAttempt.RunId, old.Sha256,
                old.SizeBytes, RecordAssetKind.Screenshot));
        }
        Assert.Equal(shared, references.IsReferenced(old));
        Assert.True(references.IsReferenced(next));
        var cleanup = new ExtensionArchiveCleanup(owner.Instance, owner.Database, owner.Lease, owner.Contracts, roots, owner.Resources, owner.Index);
        cleanup.Sweep(default);
        Assert.Equal(shared, File.Exists(Path.Combine(owner.Instance.RuntimeRoot, old.RelativePath)));
        blobs.Verify(next);
        blobs.Verify(orphan);
        Assert.All(before.Plan.Candidate.Contents, content => Assert.False(File.Exists(Path.Combine(owner.Instance.RuntimeRoot, content.RelativePath))));
        var query = new ExtensionQueryService(owner.Instance, owner.Lease, owner.Database, owner.Contracts, roots);
        Assert.Equal(ExtensionApplyOutcome.Installed, query.Read(before.Plan.OperationId, "local")!.Outcome);
        var replay = new PayloadAcceptanceLookup(owner.Instance, owner.Database, owner.Contracts, roots)
            .ReadAccepted(before.Plan.OperationId, "local", ExtensionApplyRequest.FromSource(before.Source));
        Assert.Equal(before.Plan.JournalId, replay!.JournalId);
        Assert.False(replay.Acceptance.IsNew);
        if (!shared)
        {
            var receipt = cleanup.RunNextPage(before.Plan.JournalId, old, default)!;
            Assert.Equal("completed", receipt.Acceptance.Status);
            Assert.False(receipt.Acceptance.IsNew);
            Assert.Equal(FileEffectState.Completed, Assert.Single(new FileEffectOutbox(owner.Database).Read(receipt.JournalId)).State);
            Assert.Equal(0, new RuntimeDoctor(owner.Instance, owner.Database, owner.Index).ReadCounts().PendingOperations);
        }
        Assert.Equal(before.Latest.Export(), fixture.Original.Binding().Read(before.Plan.JournalId).Latest.Export());
    }
}

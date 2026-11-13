using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExpandedContentTests
{
    [Theory(DisplayName = "Legacy Expanded Members Retire Through Durable Pages While Shared Content And Current Zip Remain Usable")]
    [InlineData(false)] [InlineData(true)]
    public async Task RetiresExpandedMembers(bool shared)
    {
        await using var fixture = await ExtensionUpgradeFixture.CreateAsync(enabled: false, originalPayloadFiles: 70);
        var owner = fixture.Original.Package.Catalog; var roots = fixture.Original.Roots;
        var before = fixture.Original.Binding().Read(fixture.Original.Accepted.JournalId);
        var blobs = new ImmutableBlobStore(owner.Instance, owner.Contracts);
        var members = before.Plan.Candidate.Contents;
        Assert.All(members, content => Assert.False(File.Exists(Path.Combine(owner.Instance.RuntimeRoot, content.RelativePath))));
        foreach (var file in before.Plan.Candidate.Snapshot.Entries.Where(item => item.Kind == FileObjectKind.File))
            blobs.PersistContent(Path.Combine(before.Plan.TargetPath, file.RelativePath), file, default);
        var manifest = members.Single(content => content.Sha256 == before.Source.Package!.Entry.ManifestSha256);
        var retained = members.First(content => content != manifest);
        if (shared)
        {
            var record = RecordStoreFixture.Record(1, Guid.NewGuid());
            new RunRecordStore(owner.Database).AppendCompleted([record]);
            new RecordAssetStore(owner.Database, blobs).AttachVerified(new(record.FinalAttempt.RunId, retained.Sha256,
                retained.SizeBytes, RecordAssetKind.Screenshot));
        }
        var cleanup = new ExtensionArchiveCleanup(owner.Instance, owner.Database, owner.Lease, owner.Contracts,
            roots, owner.Resources, owner.Index);
        CleanupAcceptanceResult? receipt = null;
        using var executor = ProcessIdentityLease.HoldCurrent();
        foreach (var content in members)
        {
            var result = cleanup.RunNextPage(before.Plan.JournalId, content, default, executor: executor);
            if (content == manifest || shared && content == retained) { Assert.Null(result); blobs.Verify(content); continue; }
            receipt ??= result;
            Assert.Equal("completed", result!.Acceptance.Status);
            Assert.False(File.Exists(Path.Combine(owner.Instance.RuntimeRoot, content.RelativePath)));
        }
        if (shared) blobs.Verify(retained);
        blobs.Verify(before.Source.Package!.Archive);
        var replay = cleanup.RunNextPage(before.Plan.JournalId, members.First(content => content != manifest && (!shared || content != retained)), default)!;
        Assert.False(replay.Acceptance.IsNew); Assert.Equal(receipt!.JournalId, replay.JournalId);
        Assert.NotNull(new ExtensionDetailsReader(owner.Instance, owner.Database, owner.Lease, owner.Contracts, roots).Read("fixture-plugin"));
        fixture.Prepare(PayloadAction.Upgrade); fixture.Accept();
        _ = await fixture.Session().ApplyAsync(fixture.Prepared!, default);
        fixture.Prepared!.Dispose(); fixture.Verified!.Dispose(); fixture.Root!.Dispose();
        var current = new ExtensionInstalledEvidence(owner.Instance, owner.Database, owner.Contracts, roots).ReadCurrent("fixture-plugin")!;
        Assert.Equal("0.6.0", current.Metadata.Package.PackageVersion);
        Assert.Equal("completed", cleanup.RunNextPage(before.Plan.JournalId, manifest, default, executor: executor)!.Acceptance.Status);
        Assert.False(File.Exists(Path.Combine(owner.Instance.RuntimeRoot, manifest.RelativePath)));
        Assert.All(current.Footprint.Payload.Contents, content =>
        {
            if (shared && content == retained) return;
            Assert.False(File.Exists(Path.Combine(owner.Instance.RuntimeRoot, content.RelativePath)));
        });
        Assert.Equal(before.Latest.Export(), fixture.Original.Binding().Read(before.Plan.JournalId).Latest.Export());
    }

    [Fact(DisplayName = "Pending Extension Preparation Still Protects Legacy Before Members Until Its No Effect Decision Commits")]
    public async Task ProtectsPendingMembers()
    {
        await using var fixture = await ExtensionUpgradeFixture.CreateAsync(enabled: false);
        var owner = fixture.Original.Package.Catalog;
        var before = fixture.Original.Binding().Read(fixture.Original.Accepted.JournalId);
        var file = before.Plan.Candidate.Snapshot.Entries.First(item => item.Kind == FileObjectKind.File);
        var blobs = new ImmutableBlobStore(owner.Instance, owner.Contracts);
        var content = blobs.PersistContent(Path.Combine(before.Plan.TargetPath, file.RelativePath), file, default);
        fixture.Prepare(PayloadAction.Upgrade); fixture.Accept();
        Assert.True(new BlobReferenceIndex(owner.Instance, owner.Contracts, owner.Database, blobs, fixture.Original.Roots).IsReferenced(content));
        Assert.Null(new ExtensionArchiveCleanup(owner.Instance, owner.Database, owner.Lease, owner.Contracts,
            fixture.Original.Roots, owner.Resources, owner.Index).RunNextPage(before.Plan.JournalId, content, default));
        blobs.Verify(content);
    }
}

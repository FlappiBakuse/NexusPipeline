using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ConfigurationBehaviorTests
{
    [Fact(DisplayName = "Configuration Sources Reuse Owned Attempt History While Independent Reads Still Recheck Bytes")]
    public void ReusesOwnedRetrySources()
    {
        using var fixture = new ConfigurationRunFixture();
        using var root = fixture.Acquire(fixture.Request);
        using var prepared = fixture.Preparation.Prepare(root, fixture.Owner, fixture.Execution.Plan, fixture.StepId, fixture.Request);
        var parent = fixture.Parent(prepared); fixture.Accept(parent);
        var source = fixture.Source; var journalId = Guid.Parse(parent.JournalId);
        var store = new ConfigurationRunStore(source.Instance, source.Database, new());
        var identity = ConfigurationRunStore.BeginIdentity(Guid.NewGuid(), ConfigurationRunFixture.Subject, journalId, fixture.StepId, prepared);
        var group = store.Begin(identity, journalId, fixture.StepId, prepared, DateTimeOffset.UtcNow);
        using var owner = fixture.Lease.BindAttemptReads(source.Database).Start(source.Instance, source.Database, new(), journalId)!;
        var expected = ConfigurationRunBinding.Read(source.Instance, source.Database, new(), group);
        var path = Path.Combine(source.Instance.RuntimeRoot, "journals", journalId.ToString("D"), "0000000001.json");
        using var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        blocked.Lock(0, blocked.Length);
        try
        {
            Assert.Throws<IOException>(() => new JournalStore(source.Instance, new()).Read(journalId));
            var actual = ConfigurationRunBinding.Read(source.Instance, source.Database, new(), group);
            Assert.Equal((expected.ParentJournal.JournalId, expected.ParentJournal.OperationId, expected.ParentJournal.Kind, expected.ParentJournal.Phase,
                    expected.ParentJournal.Revision, expected.ParentJournal.PlanPath, expected.ParentJournal.PlanDigest),
                (actual.ParentJournal.JournalId, actual.ParentJournal.OperationId, actual.ParentJournal.Kind, actual.ParentJournal.Phase,
                    actual.ParentJournal.Revision, actual.ParentJournal.PlanPath, actual.ParentJournal.PlanDigest));
            Assert.Equal(expected.ParentJournal.ResourceKeys.ToArray(), actual.ParentJournal.ResourceKeys.ToArray());
            Assert.Equal(expected.Evidence.AttemptId, actual.Evidence.AttemptId);
            Assert.Equal(expected.Resources.Select(item => item.State.ConfigurationOperationId), actual.Resources.Select(item => item.State.ConfigurationOperationId));
            Assert.Equal(expected.Resources.Select(item => (item.State.Source.Address, item.State.Source.Revision, item.State.InitialPublicationRevision)),
                actual.Resources.Select(item => (item.State.Source.Address, item.State.Source.Revision, item.State.InitialPublicationRevision)));
            Assert.Equal(expected.Resources.Select(item => item.State.SnapshotReference), actual.Resources.Select(item => item.State.SnapshotReference));
        }
        finally { blocked.Unlock(0, blocked.Length); }
    }

    [Fact(DisplayName = "An Invalid First Start Leaves No Saved Configuration Or Receipt And Can Be Retried With Valid Bytes")]
    public void EstablishesInitialSnapshot()
    {
        using var fixture = new ExecutionAdmissionFixture(saved: false);
        var source = fixture.Source;
        const string subject = "initial-configuration";
        var operation = Guid.NewGuid();
        var request = source.Request(new BindingTarget(source.BindingA));
        var overview = new ConfigurationOverviewReader(source.Instance, source.Database, source.Lease, new());
        var resource = Assert.Single(overview.Read(source.BindingA).Resources);
        var address = new ConfigurationAddress(source.BindingA, resource.ResourceId);
        var revisions = new ConfigurationRevisionStore(source.Instance, source.Database, new());

        var invalid = "{invalid"u8.ToArray();
        File.WriteAllBytes(source.Configuration, invalid);
        Assert.Throws<ConfigurationException>(() => fixture.Service.Start(operation, subject, request, DateTimeOffset.UtcNow));
        Assert.Null(revisions.Read(address));
        var acceptance = new RunAttemptAcceptance(source.Instance, new(), source.Database);
        Assert.Null(acceptance.LookupStart(ExecutionStartIdentity.Create(operation, subject, request)));
        Assert.Equal(0, fixture.Inbox.PendingCount);
        Assert.Equal(invalid, File.ReadAllBytes(source.Configuration));

        var original = "{\n  \"fixture\": true\n}\n"u8.ToArray();
        File.WriteAllBytes(source.Configuration, original);
        fixture.Service.Start(operation, subject, request, DateTimeOffset.UtcNow);
        using var accepted = fixture.Take(operation);
        var saved = Assert.IsType<ConfigurationRevision>(revisions.Read(address));
        Assert.Equal(1, saved.Revision);
        var entry = Assert.Single(saved.Tree.Entries);
        using var stream = new ImmutableBlobStore(source.Instance, new())
            .OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        Assert.Equal(original, bytes.ToArray());
        Assert.Equal(original, File.ReadAllBytes(source.Configuration));
    }

    [Theory(DisplayName = "Sealed Ordinary Retry Input Retains Progress Independently Of Long Term Synchronization")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetainsSealedProgress(bool autoSync)
    {
        await using var fixture = new RunWriterFixture(autoSync: autoSync);
        await fixture.StartAsync();
        var stopped = await fixture.StopAsync(); fixture.Seal(stopped);
        foreach (var resource in fixture.Session.Resources)
        {
            var plan = fixture.Prepared.Use(items => items.Single(item => item.Address == resource.Address).Plan);
            var source = new RunStateCodec(fixture.Base.Source.Instance, new(), fixture.Base.Source.Blobs).Read(plan.RunState!);
            var input = RetryConfigurationInput.Read(fixture.Base.Source.Instance, fixture.Base.Source.Database, new(), resource, source);
            Assert.Contains("\"count\":41", fixture.Base.Read(input.Tree));
            var selected = new ConfigurationTaskTransformation(fixture.Base.Source.Blobs).Select(resource.OperationId, source.Source, source.Tasks, inputTree: input.Tree);
            Assert.Contains("\"count\":41", fixture.Base.Read(selected));
            Assert.Contains("\"one\":true", fixture.Base.Read(selected));
            Assert.Contains("\"count\":17", fixture.Base.Read(new ConfigurationRevisionStore(fixture.Base.Source.Instance,
                fixture.Base.Source.Database, new()).Read(resource.Address)!.Tree));
        }
        fixture.Restore(); fixture.Base.VerifyOriginal();
    }
}

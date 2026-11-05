using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class RunPreparationTests
{
    [Theory(DisplayName = "Actual Run Preparation Freezes Selected Copies And Keeps Generic Auto Sync Choice Without Writing The Live Target")]
    [InlineData(false)]
    [InlineData(true)]
    public void FreezesSelectedCopies(bool autoSync)
    {
        using var fixture = new ConfigurationRunFixture();
        var request = fixture.Request with { AutoSyncEnabled = autoSync, SyncInitially = autoSync, SyncAtFinish = autoSync };
        using var root = fixture.Acquire(request);
        using var prepared = fixture.Preparation.Prepare(root, fixture.Owner, fixture.Execution.Plan, fixture.StepId, request);
        prepared.Use(resources =>
        {
            var resource = Assert.Single(resources);
            Assert.Equal(fixture.Request.Resources[0].Target.Address, resource.Address);
            Assert.NotEqual(fixture.Owner, resource.Plan.OperationId);
            Assert.Equal(request.Resources[0].OperationId, resource.Plan.OperationId);
            Assert.Equal(request.Resources[0].JournalId, resource.Plan.JournalId);
            var state = new RunStateCodec(fixture.Source.Instance, new(), fixture.Source.Blobs).Read(resource.Plan.RunState!);
            Assert.Equal(fixture.Owner, state.OwnerOperationId);
            Assert.Equal(autoSync, state.AutoSyncEnabled);
            Assert.False(state.Tasks[0].Initial.Values["one"]);
            Assert.Equal(fixture.Saved.Tree.Entries, state.Source.Tree.Entries);
            Assert.Contains("\"one\":true", fixture.Read(resource.Plan.Candidate.Snapshot));
            Assert.Contains("\"one\":false", fixture.Read(state.Source.Tree));
            foreach (var path in new[] { resource.Reference.RelativePath, resource.Plan.RunState!.RelativePath,
                Content(fixture.Saved.Tree), Content(resource.Plan.Candidate.Snapshot) })
                Assert.Throws<IOException>(() => File.Delete(Path.Combine(fixture.Source.Instance.RuntimeRoot, path)));
            return 0;
        });
        fixture.VerifyOriginal();
        Assert.Empty(new NexusPipeline.Platform.Storage.Sqlite.RecoveryJournalRegistry(fixture.Source.Database).ReadSnapshot().Journals);
    }

    [Fact(DisplayName = "A Prepared Run Retains The Borrowed Root Claims And Host Write After Its Caller Disposes The Root")]
    public void RetainsBorrowedClaims()
    {
        using var fixture = new ConfigurationRunFixture();
        using var root = fixture.Acquire(fixture.Request);
        var prepared = fixture.Preparation.Prepare(root, fixture.Owner, fixture.Execution.Plan, fixture.StepId, fixture.Request);
        try
        {
            root.Dispose();
            Assert.False(root.IsActive);
            Assert.Equal(ResourceLeaseDenial.Conflict, fixture.Resources.TryAcquire(Guid.NewGuid(), fixture.Claims(fixture.Request)).Denial);
            var session = Guid.NewGuid();
            Assert.Equal("accepted_writes_pending", fixture.Lifecycle.RequestClose(session, Guid.NewGuid()).ReasonCode);
            Assert.Single(prepared.Use(resources => resources.ToArray()));
            prepared.Dispose(); prepared.Dispose();
            Assert.Throws<ObjectDisposedException>(() => prepared.Use(resources => resources.Count));
            Assert.NotNull(fixture.Lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
        }
        finally { prepared.Dispose(); }
        fixture.VerifyOriginal();
    }

    [Theory(DisplayName = "Invalid Run Preparation Cannot Adopt The Live Scene Or Publish Any Frozen Candidate")]
    [InlineData("missing-configuration")]
    [InlineData("revision")]
    [InlineData("contract")]
    [InlineData("binding-revision")]
    [InlineData("user-definition")]
    [InlineData("account")]
    [InlineData("foreign-manager")]
    [InlineData("missing-claim")]
    [InlineData("weak-claim")]
    public void RejectsInvalidPreparation(string fault)
    {
        using var fixture = new ConfigurationRunFixture(save: fault != "missing-configuration");
        var resource = fixture.Request.Resources[0]; var request = fixture.Request;
        if (fault == "revision") request = request with { Resources = [resource with { Target = resource.Target with { ExpectedRevision = 2 } }] };
        if (fault == "contract") request = request with { Resources = [resource with { Target = resource.Target with
            { Contract = resource.Target.Contract with { DefinitionSha256 = new('d', 64) } } }] };
        if (fault == "account") request = request with { Resources = [resource with { Target = resource.Target with
            { Address = new(fixture.OtherBinding, "main") } }] };
        if (fault is "binding-revision" or "user-definition") fixture.ChangeDefinition(fault);
        var required = fixture.Claims(fixture.Request);
        if (fault == "missing-claim") required = required.Where(claim => claim.ResourceKey != ConfigurationRevisionService.ResourceKey(resource.Target.Address)).ToArray();
        if (fault == "weak-claim") required = required.Select(claim => claim.ResourceKey == ConfigurationRevisionService.ResourceKey(resource.Target.Address)
            ? claim with { Mode = ResourceClaimMode.Reference } : claim).ToArray();
        var manager = fixture.Resources;
        if (fault == "foreign-manager")
        {
            var index = new RecoveryIndex(); index.PublishVerified(0, 1, []);
            manager = new(new(index, TimeProvider.System));
        }
        using var root = Assert.IsType<ResourceOperationLease>(manager.TryAcquire(fixture.Owner, required).Lease);
        var before = PersistentFingerprints(fixture.Source.Root);
        Assert.ThrowsAny<Exception>(() => fixture.Preparation.Prepare(root, fixture.Owner, fixture.Execution.Plan, fixture.StepId, request));
        Assert.Equal(before, PersistentFingerprints(fixture.Source.Root));
        fixture.VerifyOriginal();
        root.Dispose();
        using var available = fixture.Acquire(fixture.Request);
    }

    [Fact(DisplayName = "Declared Overlapping Run Targets Are Rejected Before Archiving Or Retaining Any Parent Path")]
    public void RejectsOverlappingTargets()
    {
        using var fixture = new ConfigurationRunFixture(overlap: true);
        using var root = Assert.IsType<ResourceOperationLease>(fixture.Resources.TryAcquire(fixture.Owner, [new("fixture:root", ResourceClaimMode.Reference)]).Lease);
        var before = PersistentFingerprints(fixture.Source.Root);
        Assert.Equal("configuration_edit_target_overlap", Assert.Throws<ConfigurationException>(() =>
            fixture.Preparation.Prepare(root, fixture.Owner, fixture.Execution.Plan, fixture.StepId, fixture.Request)).Message);
        Assert.Equal(before, PersistentFingerprints(fixture.Source.Root));
        fixture.VerifyOriginal();
    }

    [Fact(DisplayName = "A Request Cannot Replace Its Frozen Declared Target With Another External Path")]
    public void RejectsChangedTargets()
    {
        using var fixture = new ConfigurationRunFixture();
        var path = Path.Combine(fixture.Source.Root, "unrelated-external.json"); File.WriteAllText(path, "preserve unrelated");
        var resource = fixture.Request.Resources[0];
        var request = fixture.Request with { Resources = [resource with { Target = resource.Target with { TargetPath = path } }] };
        using var root = fixture.Acquire(fixture.Request);
        var before = PersistentFingerprints(fixture.Source.Root);
        Assert.Equal("configuration_run_step_unbound", Assert.Throws<IOException>(() =>
            fixture.Preparation.Prepare(root, fixture.Owner, fixture.Execution.Plan, fixture.StepId, request)).Message);
        Assert.Equal(before, PersistentFingerprints(fixture.Source.Root));
        fixture.VerifyOriginal();
    }

    private static string Content(FileTreeSnapshot tree) => $"blobs/content/{tree.Entries.Single(entry => entry.Kind == FileObjectKind.File).Sha256}.bin";

    private static string[] PersistentFingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "instance.lock" && !path.EndsWith("-shm", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

}

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class RunPreparationTests
{
    [Theory(DisplayName = "Run Preparation Applies Task Selection To Its Copy With Either Sync Choice And Releases Its Files")]
    [InlineData(false)]
    [InlineData(true)]
    public void FreezesSelectedCopies(bool autoSync)
    {
        using var fixture = new ConfigurationRunFixture();
        var request = fixture.Request with { AutoSyncEnabled = autoSync, SyncInitially = autoSync, SyncAtFinish = autoSync };
        var original = fixture.Read(fixture.Saved.Tree);
        using var root = fixture.Acquire(request);
        using var prepared = fixture.Preparation.Prepare(root, fixture.Owner, fixture.Execution.Plan, fixture.StepId, request);
        var candidate = prepared.Use(resources => Assert.Single(resources).Plan.Candidate.Snapshot);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"tasks":{"one":true},"count":17}"""), JsonNode.Parse(fixture.Read(candidate))));
        var paths = new[] { Content(fixture.Saved.Tree), Content(candidate) };
        foreach (var path in paths)
            Assert.Throws<IOException>(() => File.Delete(Path.Combine(fixture.Source.Instance.RuntimeRoot, path)));
        prepared.Dispose();
        foreach (var path in paths)
        {
            using var writer = File.Open(Path.Combine(fixture.Source.Instance.RuntimeRoot, path), FileMode.Open, FileAccess.Write, FileShare.None);
        }
        var revisions = new ConfigurationRevisionStore(fixture.Source.Instance, fixture.Source.Database, new());
        var saved = Assert.IsType<ConfigurationRevision>(revisions.Read(fixture.Saved.Address));
        Assert.Equal(fixture.Saved.Revision, saved.Revision);
        Assert.Equal(original, fixture.Read(saved.Tree));
        fixture.VerifyOriginal();
        Assert.Empty(new RecoveryJournalRegistry(fixture.Source.Database).ReadSnapshot().Journals);
    }

    [Fact(DisplayName = "A Prepared Run Retains The Borrowed Root Claims And Host Write After Its Caller Disposes The Root")]
    public void RetainsBorrowedClaims()
    {
        using var fixture = new ConfigurationRunFixture();
        using var root = fixture.Acquire(fixture.Request);
        using var prepared = fixture.Preparation.Prepare(root, fixture.Owner, fixture.Execution.Plan, fixture.StepId, fixture.Request);
        root.Dispose();
        Assert.Equal(ResourceLeaseDenial.Conflict, fixture.Resources.TryAcquire(Guid.NewGuid(), fixture.Claims(fixture.Request)).Denial);
        var session = Guid.NewGuid();
        Assert.Equal("accepted_writes_pending", fixture.Lifecycle.RequestClose(session, Guid.NewGuid()).ReasonCode);
        prepared.Dispose();
        Assert.NotNull(fixture.Lifecycle.RequestClose(session, Guid.NewGuid()).Permit);
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

using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ResourceClaimsTests
{
    [Fact(DisplayName = "Different Scripts With Sibling Targets Share Physical Ancestors And The Same Account In One Manager")]
    public void SharesSiblingAncestors()
    {
        using var fixture = new PlanBuilderFixture();
        var sibling = Path.Combine(Path.GetDirectoryName(fixture.Configuration)!, "sibling.json");
        File.WriteAllText(sibling, "{\"sibling\":true}"); SetOtherTarget(fixture, sibling);
        using var first = fixture.Prepare(new NexusPipeline.Core.Execution.BindingTarget(fixture.BindingB));
        using var second = fixture.Prepare(new NexusPipeline.Core.Execution.BindingTarget(fixture.OtherBinding));
        var firstClaims = ExecutionPlanResources.Claims(first.ReadPlan()!.ReadWire());
        var secondClaims = ExecutionPlanResources.Claims(second.ReadPlan()!.ReadWire());
        var shared = firstClaims.Where(claim => secondClaims.Any(other => other.ResourceKey == claim.ResourceKey)).ToArray();
        Assert.Contains(shared, claim => claim.ResourceKey.StartsWith("filesystem:", StringComparison.Ordinal));
        Assert.Contains(shared, claim => claim.ResourceKey == "user:" + fixture.UserB.ToString("D"));
        Assert.All(shared, claim => Assert.Equal(ResourceClaimMode.Reference, claim.Mode));
        var (manager, _) = Ready(fixture);
        using var firstLease = Assert.IsType<ResourceOperationLease>(manager.TryAcquire(Guid.NewGuid(), firstClaims).Lease);
        using var secondLease = Assert.IsType<ResourceOperationLease>(manager.TryAcquire(Guid.NewGuid(), secondClaims).Lease);
        Assert.NotEqual(firstLease.OperationId, secondLease.OperationId);
        foreach (var claim in shared)
        {
            var denied = manager.TryAcquire(Guid.NewGuid(), [claim with { Mode = ResourceClaimMode.Mutation }]);
            Assert.Null(denied.Lease); Assert.Equal(ResourceLeaseDenial.Conflict, denied.Denial);
        }
    }

    [Fact(DisplayName = "Different Scripts Cannot Mutate The Same Real Configuration Object In One Manager")]
    public void RejectsSharedTargets()
    {
        using var fixture = new PlanBuilderFixture(); SetOtherTarget(fixture, fixture.Configuration);
        using var first = fixture.Prepare(new NexusPipeline.Core.Execution.BindingTarget(fixture.BindingB));
        using var second = fixture.Prepare(new NexusPipeline.Core.Execution.BindingTarget(fixture.OtherBinding));
        var firstClaims = ExecutionPlanResources.Claims(first.ReadPlan()!.ReadWire());
        var secondClaims = ExecutionPlanResources.Claims(second.ReadPlan()!.ReadWire());
        Assert.Contains(firstClaims, claim => claim.Mode == ResourceClaimMode.Mutation &&
            claim.ResourceKey.StartsWith("filesystem:", StringComparison.Ordinal) && secondClaims.Contains(claim));
        var (manager, _) = Ready(fixture);
        using var firstLease = Assert.IsType<ResourceOperationLease>(manager.TryAcquire(Guid.NewGuid(), firstClaims).Lease);
        var denied = manager.TryAcquire(Guid.NewGuid(), secondClaims);
        Assert.Null(denied.Lease); Assert.Equal(ResourceLeaseDenial.Conflict, denied.Denial);
        firstLease.Dispose();
        using var accepted = Assert.IsType<ResourceOperationLease>(manager.TryAcquire(Guid.NewGuid(), secondClaims).Lease);
    }

    [Fact(DisplayName = "A Real Manual Editor Entry Cannot Bypass A Run Claim Using Another Scripts Binding")]
    public async Task GuardsEditorAdmission()
    {
        using var fixture = new PlanBuilderFixture();
        using var draft = fixture.Prepare(new NexusPipeline.Core.Execution.BindingTarget(fixture.BindingB));
        var (manager, index) = Ready(fixture);
        using var running = Assert.IsType<ResourceOperationLease>(manager.TryAcquire(Guid.NewGuid(),
            ExecutionPlanResources.Claims(draft.ReadPlan()!.ReadWire())).Lease);
        using var controller = new ConfigurationEditController(fixture.Instance, fixture.Database, new(), fixture.Lease, manager, index);
        var request = new ConfigurationEditRequest(1, [new(new(fixture.OtherBinding, "main"), 0,
            new(1, new('a', 64), null, false), fixture.Configuration)
            { WritePolicy = new([""], [FileObjectKind.File]) }])
        {
            Editor = new(fixture.OtherImage, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.OtherImage))),
                [], Path.GetDirectoryName(fixture.OtherImage)!)
        };
        var before = fixture.Snapshot();
        Assert.Equal("configuration_resource_conflict", (await Assert.ThrowsAsync<ConfigurationException>(() =>
            controller.BeginAsync(Guid.NewGuid(), "claims-fixture", request, DateTimeOffset.UtcNow))).Message);
        var after = fixture.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }

    [Fact(DisplayName = "A Real Mode Insert Failure Rolls Back The Whole Parent Acceptance Before Its Initial Journal Exists")]
    public void RevertsClaimFailure()
    {
        var fixture = AttemptTestFixture.Create();
        var selected = ExecutionPlanResources.Claims(fixture.Source.Plan).Last(claim => claim.Mode == ResourceClaimMode.Mutation);
        Execute(fixture.Database, "CREATE TRIGGER fixture_fail_claim BEFORE INSERT ON journal_resource_claims WHEN NEW.resource_key=" +
            Literal(selected.ResourceKey) + " BEGIN SELECT RAISE(ABORT,'fixture_mode_insert_failure'); END;");
        var commits = fixture.Database.CommitSequence; var target = TreeBytes(fixture.Configuration.TargetPath);
        try
        {
            Assert.Throws<SqliteException>(() => fixture.Accept());
            Assert.Equal(commits, fixture.Database.CommitSequence);
            Assert.Null(new RecoveryJournalRegistry(fixture.Database).FindJournal(fixture.JournalId));
            Assert.False(Directory.Exists(Path.Combine(fixture.Instance.RuntimeRoot, "journals", fixture.JournalId.ToString("D"))));
            Assert.Equal(target, TreeBytes(fixture.Configuration.TargetPath));
        }
        finally { Execute(fixture.Database, "DROP TRIGGER fixture_fail_claim;"); }
        Assert.True(fixture.Accept().Acceptance.IsNew);
        Assert.Equal(ExecutionPlanResources.Claims(fixture.Source.Plan).Select(ClaimText), ReadClaims(fixture.Database, fixture.JournalId));
    }

    [Theory(DisplayName = "Damaged Parent Claim Rows Are Rejected On An Independent Diagnostic Copy Without Repairing The Source")]
    [InlineData("missing")]
    [InlineData("all-missing")]
    [InlineData("extra")]
    [InlineData("reference-to-mutation")]
    [InlineData("mutation-to-reference")]
    public void RejectsDamagedClaims(string damage)
    {
        var fixture = AttemptTestFixture.Create(); fixture.Accept();
        var claims = ExecutionPlanResources.Claims(fixture.Source.Plan);
        var chosen = claims.First(claim => claim.Mode == (damage == "mutation-to-reference" ? ResourceClaimMode.Mutation : ResourceClaimMode.Reference));
        var journal = Literal(fixture.JournalId.ToString("D")); var key = Literal(chosen.ResourceKey);
        var sql = damage switch
        {
            "missing" => $"DELETE FROM journal_resource_claims WHERE journal_id={journal} AND resource_key={key};",
            "all-missing" => $"DELETE FROM journal_resource_claims WHERE journal_id={journal};",
            "extra" => $"INSERT INTO journal_resources VALUES({journal},'fixture:extra'); " +
                $"INSERT INTO resource_isolations VALUES('fixture:extra',{journal},'verified','recovery_pending'); " +
                $"INSERT INTO journal_resource_claims VALUES({journal},'fixture:extra',0);",
            _ => $"UPDATE journal_resource_claims SET mode={(damage == "reference-to-mutation" ? 1 : 0)} " +
                $"WHERE journal_id={journal} AND resource_key={key};"
        };
        Execute(fixture.Database, sql);
        var before = DatabaseBytes(fixture.Database); var site = TreeBytes(fixture.Configuration.TargetPath);
        Assert.Equal("journal_resource_claims_unbound", Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Instance,
            Path.Combine(fixture.Root, "claims-diagnostic"), new JournalStartupAdmission(fixture.Instance, new()))).Message);
        AssertDatabaseBytes(fixture.Database, before); Assert.Equal(site, TreeBytes(fixture.Configuration.TargetPath));
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, "claims-diagnostic")));
    }

    [Fact(DisplayName = "Recovery Isolation Publishes The Accepted Reference And Mutation Modes To The Existing Lifecycle")]
    public void PreservesClaimModes()
    {
        var fixture = AttemptTestFixture.Create(); fixture.Accept();
        var expected = ExecutionPlanResources.Claims(fixture.Source.Plan);
        var snapshot = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot();
        Assert.Equal(expected.Select(ClaimText), snapshot.Isolations.OrderBy(item => item.ResourceKey, StringComparer.Ordinal)
            .Select(item => ClaimText(new(item.ResourceKey, item.Mode))));
        var index = new RecoveryIndex(); index.PublishVerified(0, 1, snapshot.Isolations);
        var manager = new ResourceLeaseManager(new(index, TimeProvider.System));
        var reference = expected.First(claim => claim.Mode == ResourceClaimMode.Reference);
        var mutation = expected.First(claim => claim.Mode == ResourceClaimMode.Mutation);
        using var allowed = Assert.IsType<ResourceOperationLease>(manager.TryAcquire(Guid.NewGuid(), [reference]).Lease);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryAcquire(Guid.NewGuid(), [reference with { Mode = ResourceClaimMode.Mutation }]).Denial);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryAcquire(Guid.NewGuid(), [mutation with { Mode = ResourceClaimMode.Reference }]).Denial);
    }

    [Fact(DisplayName = "An Installed Configuration Keeps Its Original Frozen Parent Claims Readable And Exactly Replayable")]
    public void KeepsFrozenClaims()
    {
        using var fixture = new ConfigurationRunFixture();
        using var root = fixture.Acquire(fixture.Request);
        using var prepared = fixture.Preparation.Prepare(root, fixture.Owner, fixture.Execution.Plan, fixture.StepId, fixture.Request);
        var parent = fixture.Parent(prepared); var accepted = fixture.Accept(parent);
        var store = new ConfigurationRunStore(fixture.Source.Instance, fixture.Source.Database, new());
        var identity = ConfigurationRunStore.BeginIdentity(Guid.NewGuid(), ConfigurationRunFixture.Subject,
            Guid.Parse(parent.JournalId), fixture.StepId, prepared);
        var group = store.Begin(identity, Guid.Parse(parent.JournalId), fixture.StepId, prepared, DateTimeOffset.UtcNow);
        var expected = ExecutionPlanResources.Claims(fixture.Source.Plan);
        var modes = ReadClaims(fixture.Source.Database, Guid.Parse(parent.JournalId));
        var totalBytes = fixture.Execution.Plan.Export();
        var original = PhysicalObjectIdentity.Read(fixture.Request.Resources[0].Target.TargetPath);
        try
        {
            foreach (var resource in group.Resources) fixture.Install(resource.JournalId);
            Assert.NotEqual(original, PhysicalObjectIdentity.Read(fixture.Request.Resources[0].Target.TargetPath));
            Assert.False(ExecutionPlanResources.Capture(fixture.Source.Plan).SequenceEqual(fixture.Source.Plan.PhysicalClaims));
            Assert.Equal(expected.ToArray(), ExecutionPlanResources.Claims(fixture.Source.Plan).ToArray());
            Assert.Equal(fixture.Source.Plan.ResourceKeys, ExecutionPlanResources.Derive(fixture.Source.Plan));
            Assert.Equal(totalBytes, fixture.Source.Codec.Read(fixture.Execution.Reference).Export());
            var codec = new AttemptPlanCodec(fixture.Source.Instance, new(), fixture.Source.Blobs);
            Assert.Equal(parent.OperationId, codec.Read(accepted.Plan).ReadWire().OperationId);
            JournalResourceClaims.VerifyInventory(fixture.Source.Instance, fixture.Source.Database, new());
            var replay = fixture.Accept(parent);
            Assert.False(replay.Acceptance.IsNew); Assert.Equal(accepted.Plan, replay.Plan);
            Assert.Equal(modes, ReadClaims(fixture.Source.Database, Guid.Parse(parent.JournalId)));
        }
        finally { fixture.Restore(); }
    }

    [Theory(DisplayName = "An Exact Accepted Replay Returns The Old Reference Without Recapturing The Changed Physical Site")]
    [InlineData("missing-image")]
    [InlineData("moved-target")]
    public void ReplaysExactReferences(string change)
    {
        var fixture = AttemptTestFixture.Create(); var accepted = fixture.Accept();
        if (change == "missing-image") File.Move(fixture.Source.Image, Path.Combine(fixture.Root, "retained-image"));
        else Directory.Move(fixture.Configuration.TargetPath, Path.Combine(fixture.Root, "retained-configuration"));
        var before = RuntimeBytes(fixture.Instance.RuntimeRoot);
        Assert.Equal(accepted with { Acceptance = accepted.Acceptance with { IsNew = false } }, fixture.Accept());
        Assert.Equal(before, RuntimeBytes(fixture.Instance.RuntimeRoot));
    }

    [Theory(DisplayName = "Accepted Replay Rejects Changed Candidate Or Operation Identity Before Writing Anything")]
    [InlineData("candidate")]
    [InlineData("subject")]
    [InlineData("kind")]
    [InlineData("digest")]
    public void RejectsChangedReplay(string change)
    {
        var fixture = AttemptTestFixture.Create(); var accepted = fixture.Accept();
        var operation = new OperationIdentity(Guid.Parse(fixture.Plan.OperationId), "fixture-attempt", new('b', 64), "fixture.attempt");
        operation = change switch
        {
            "subject" => new(operation.OperationId, "different-subject", operation.RequestDigest, operation.Kind),
            "kind" => new(operation.OperationId, operation.SubjectId, operation.RequestDigest, "different.kind"),
            "digest" => new(operation.OperationId, operation.SubjectId, new('c', 64), operation.Kind), _ => operation
        };
        var candidate = change == "candidate" ? fixture.Plan with { AttemptOrdinal = 2 } : fixture.Plan;
        var before = RuntimeBytes(fixture.Instance.RuntimeRoot); var commits = fixture.Database.CommitSequence;
        Assert.Throws<OperationConflictException>(() => new RunAttemptAcceptance(fixture.Instance, new(), fixture.Database)
            .Accept(operation, candidate, DateTimeOffset.UtcNow));
        Assert.Equal(before, RuntimeBytes(fixture.Instance.RuntimeRoot)); Assert.Equal(commits, fixture.Database.CommitSequence);
        Assert.Equal(accepted with { Acceptance = accepted.Acceptance with { IsNew = false } }, fixture.Accept());
    }

    [Fact(DisplayName = "Native Deletion Guard Still Protects Parent Definition Content After Its Image And Target Have Moved")]
    public void ProtectsMovedInputs()
    {
        var fixture = AttemptTestFixture.Create(); fixture.Accept();
        File.Move(fixture.Source.Image, Path.Combine(fixture.Root, "retained-image"));
        Directory.Move(fixture.Configuration.TargetPath, Path.Combine(fixture.Root, "retained-configuration"));
        var definition = fixture.Source.Plan.DefinitionBundle;
        var content = new BlobReference(definition.RelativePath, definition.SizeBytes, definition.Sha256);
        var candidate = Assert.Single(new CleanupPlanCodec(fixture.Instance, new(), fixture.Source.Blobs)
            .Capture(Guid.NewGuid(), Guid.NewGuid(), [content]).Items);
        var cleanup = new BlobCleanup(fixture.Instance, fixture.Source.Blobs,
            new(fixture.Instance, new(), fixture.Database, fixture.Source.Blobs));
        Assert.Equal(CleanupItemState.Referenced, cleanup.Inspect(candidate));
        Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(candidate));
        fixture.Source.Blobs.Verify(content);
    }

    private static void SetOtherTarget(PlanBuilderFixture fixture, string path)
    {
        var catalog = fixture.Definitions.Read(); var external = catalog.Roots.Single(root => root.Kind == AutomationRootKind.External);
        var script = catalog.Scripts.Single(script => script.Id == fixture.OtherScriptId) with
            { ConfigurationPaths = [new(external.Id, Path.GetRelativePath(external.Path, path).Replace('\\', '/'))] };
        var change = new AutomationChanges(catalog.Revision, [], [script], [], [], [], [], []);
        fixture.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "claims-fixture", change), change, DateTimeOffset.UtcNow);
        var current = fixture.Definitions.Read();
        var binding = current.Bindings.Single(item => item.Id == fixture.OtherBinding);
        var portable = current.Scripts.Single(item => item.Id == fixture.OtherScriptId).ConfigurationPaths.Single();
        var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(fixture.Instance, new())).Capture(path, default).Snapshot;
        var saved = new ConfigurationRevisionChange(new(binding.Id, GenericConfigurationPolicy.ResourceId(portable)), 0,
            binding.Revision, GenericConfigurationPolicy.Contract(portable), tree, ConfigurationUpdateKind.Manual);
        new ConfigurationRevisionStore(fixture.Instance, fixture.Database, new()).Commit(
            ConfigurationRevisionStore.Identity(Guid.NewGuid(), "claims-fixture", saved), saved, DateTimeOffset.UtcNow);
    }

    private static (ResourceLeaseManager Manager, RecoveryIndex Index) Ready(PlanBuilderFixture fixture)
    {
        var index = new StartupJournalRecovery(fixture.Instance, new(), fixture.Database,
            new(Path.Combine(fixture.Root, "host"), Path.Combine(fixture.Instance.RuntimeRoot, "extensions"))).Recover(fixture.Lease);
        return (new(new(index, TimeProvider.System)), index);
    }

    private static string ClaimText(ResourceClaim claim) => claim.ResourceKey + ":" + (int)claim.Mode;
    private static string[] ReadClaims(StateDatabase database, Guid journal) => JournalResourceClaims.Read(database, journal)
        .OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => ClaimText(new(item.Key, item.Value))).ToArray();

    private static void Execute(StateDatabase database, string sql)
    {
        using var connection = Connect(database); using var command = connection.CreateCommand();
        command.CommandText = sql; command.ExecuteNonQuery();
    }

    private static SqliteConnection Connect(StateDatabase database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath, Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string[] TreeBytes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + PhysicalObjectIdentity.Read(path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private static string[] RuntimeBytes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "instance.lock" && !path.EndsWith("nexus.db", StringComparison.Ordinal) &&
            !path.EndsWith("nexus.db-wal", StringComparison.Ordinal) && !path.EndsWith("nexus.db-shm", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private static Dictionary<string, byte[]> DatabaseBytes(StateDatabase database) => new[] { "", "-wal", "-shm", "-journal" }
        .Where(suffix => File.Exists(database.DatabasePath + suffix)).ToDictionary(suffix => suffix, suffix => File.ReadAllBytes(database.DatabasePath + suffix));
    private static void AssertDatabaseBytes(StateDatabase database, Dictionary<string, byte[]> expected)
    {
        var actual = DatabaseBytes(database); Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var pair in expected) Assert.Equal(pair.Value, actual[pair.Key]);
    }
}

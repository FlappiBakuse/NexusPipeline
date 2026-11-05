using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class OwnedPayloadTests
{
    [Fact(DisplayName = "Actual Opaque First Install Materializes And Moves The Verified Tree Before Any Installed Or Enabled Claim")]
    public async Task InstallsVerifiedTree()
    {
        using var fixture = new PayloadAcceptanceFixture(savedEnabled: false);
        _ = fixture.Prepare(); var accepted = fixture.Accept();
        var owner = Inventory(fixture);
        using var scope = await owner.ReserveForApplyAsync(fixture.Package.OperationId, "fixture-plugin", default);
        var physical = Journal(fixture).Apply(fixture.Prepared!, scope, default);
        Assert.Equal("candidate-installed", physical.Stage);
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Package.Deployed, "manifest.json")),
            File.ReadAllBytes(Path.Combine(fixture.Target, "manifest.json")));
        Assert.Equal(physical.CandidateObjects.ToArray(), PhysicalTreeIdentity.Capture(fixture.Target,
            new FileTreeScanner(new(4096, 268435456)).Capture(fixture.Target, default)).ToArray());
        Assert.Equal(JournalPhase.Applied, new RecoveryJournalRegistry(fixture.Package.Catalog.Database)
            .ReadJournal(accepted.JournalId).Phase);
        Assert.All(new FileEffectOutbox(fixture.Package.Catalog.Database).Read(accepted.JournalId),
            item => Assert.Equal(FileEffectState.Completed, item.State));
        using var connection = DatabaseConnections.Open(fixture.Package.Catalog.Database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Null(ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, "fixture-plugin"));
        Assert.False(new HostSettingsStore(fixture.Package.Catalog.Database).Read().Values.Extensions.Single().Enabled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(ExtensionWorkspacePath.For(fixture.Target, fixture.Package.OperationId)));
    }

    [Fact(DisplayName = "Unknown Workspace Bytes Are Preserved And Do Not Become A Completed Installation")]
    public async Task PreservesUnknownWorkspace()
    {
        using var fixture = new PayloadAcceptanceFixture(savedEnabled: false);
        _ = fixture.Prepare(); var accepted = fixture.Accept(); var work = ExtensionWorkspacePath.For(fixture.Target, fixture.Package.OperationId);
        var extra = Path.Combine(work, "unknown.bin");
        var observer = new Observer((point, revision, _) =>
        {
            if (point == PayloadPersistencePoint.SqlProjectionCommitted && revision == 3)
                File.WriteAllBytes(extra, [19, 73, 14]);
        });
        using var scope = await Inventory(fixture).ReserveForApplyAsync(fixture.Package.OperationId, "fixture-plugin", default);
        Assert.Throws<IOException>(() => Journal(fixture, observer).Apply(fixture.Prepared!, scope, default));
        Assert.Equal(new byte[] { 19, 73, 14 }, File.ReadAllBytes(extra)); Assert.False(Directory.Exists(fixture.Target));
        var journal = new RecoveryJournalRegistry(fixture.Package.Catalog.Database).ReadJournal(accepted.JournalId);
        Assert.Equal(JournalPhase.Applying, journal.Phase);
        Assert.Equal(FileEffectState.Started, Assert.Single(new FileEffectOutbox(fixture.Package.Catalog.Database).Read(accepted.JournalId)).State);
    }

    [Fact(DisplayName = "An Applied Physical Prefix Is Reconciled After Final Projection Sql Failure Without Moving Another Tree")]
    public async Task ReconcilesAppliedPrefix()
    {
        using var fixture = new PayloadAcceptanceFixture(savedEnabled: false);
        _ = fixture.Prepare(); var accepted = fixture.Accept();
        using var scope = await Inventory(fixture).ReserveForApplyAsync(fixture.Package.OperationId, "fixture-plugin", default);
        fixture.Package.Catalog.Sql("CREATE TRIGGER fixture_fail_extension_applied BEFORE UPDATE OF phase ON operation_journals " +
            "WHEN NEW.operation_kind='extensionApply' AND NEW.phase='applied' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try { Assert.Throws<SqliteException>(() => Journal(fixture).Apply(fixture.Prepared!, scope, default)); }
        finally { fixture.Package.Catalog.Sql("DROP TRIGGER fixture_fail_extension_applied;"); }
        var identity = PhysicalObjectIdentity.Read(fixture.Target);
        var current = new JournalStore(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts).Read(accepted.JournalId);
        Assert.Equal(JournalPhase.Applied, current.Phase);
        Assert.Equal(JournalPhase.Applying, new RecoveryJournalRegistry(fixture.Package.Catalog.Database).ReadJournal(accepted.JournalId).Phase);
        var result = Journal(fixture).Apply(fixture.Prepared!, scope, default);
        Assert.Equal(identity, PhysicalObjectIdentity.Read(fixture.Target));
        Assert.Equal(current.Export(), new JournalStore(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts).Read(accepted.JournalId).Export());
        Assert.Equal(identity, Assert.Single(result.CandidateObjects, item => item.RelativePath.Length == 0).Identity);
        Assert.Equal(JournalPhase.Applied, new RecoveryJournalRegistry(fixture.Package.Catalog.Database).ReadJournal(accepted.JournalId).Phase);
    }

    [Theory(DisplayName = "Changed Immutable Source Or Physical Fact Cannot Authorize Another Effect")]
    [InlineData("source")]
    [InlineData("physical")]
    public async Task RejectsChangedFacts(string changed)
    {
        using var fixture = new PayloadAcceptanceFixture(savedEnabled: false);
        _ = fixture.Prepare(); var accepted = fixture.Accept();
        using var scope = await Inventory(fixture).ReserveForApplyAsync(fixture.Package.OperationId, "fixture-plugin", default);
        _ = Journal(fixture).Apply(fixture.Prepared!, scope, default);
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Target, "manifest.json"));
        var current = new JournalStore(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts).Read(accepted.JournalId);
        var plan = new PayloadPlanCodec(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts,
            fixture.Package.Blobs, fixture.Roots).Read(accepted.Plan);
        var reference = changed == "source" ? plan.ExtensionSource! :
            ExtensionPhysicalCodec.ReadReferences(current.Root, plan)[^1];
        var path = Path.Combine(fixture.Package.Catalog.Instance.RuntimeRoot, reference.RelativePath);
        var original = File.ReadAllBytes(path); File.WriteAllText(path, "{\"format\":\"unknown-preserve\"}");
        try
        {
            Assert.Throws<IOException>(() => Journal(fixture).Apply(fixture.Prepared!, scope, default));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Target, "manifest.json")));
            Assert.Equal("{\"format\":\"unknown-preserve\"}", File.ReadAllText(path));
        }
        finally { File.WriteAllBytes(path, original); }
    }

    [Fact(DisplayName = "Physical Metadata Capacity Is Measured Without Publishing Facts Or Creating A Workspace")]
    public void MeasuresBeforeEffects()
    {
        using var fixture = new PayloadSourceFixture();
        ExtensionPhysicalCodec.RequireCapacity(fixture.Plan);
        var oversized = fixture.Plan with
        {
            Candidate = fixture.Plan.Candidate with
            {
                Snapshot = new(FileObjectKind.Directory, Enumerable.Range(0, 4096).Select(index =>
                    new FileTreeEntry(index.ToString("D4", global::System.Globalization.CultureInfo.InvariantCulture) +
                        new string('a', 400), FileObjectKind.Directory, 0, null)).ToArray())
            }
        };
        var failure = Assert.Throws<IOException>(() => ExtensionPhysicalCodec.RequireCapacity(oversized));
        Assert.Equal("extension_payload_metadata_capacity", failure.Message);
        Assert.False(Directory.Exists(fixture.Plan.TargetPath));
        Assert.False(Directory.Exists(ExtensionWorkspacePath.For(fixture.Plan)));
    }

    private static ExtensionActivator Inventory(PayloadAcceptanceFixture fixture) => new(
        fixture.Package.Catalog.Contracts, fixture.Package.Catalog.Lease, fixture.Package.Catalog.Resources);
    private static ExtensionPhysicalJournal Journal(PayloadAcceptanceFixture fixture, IPayloadPersistenceObserver? observer = null) =>
        new(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Database, fixture.Package.Catalog.Contracts, fixture.Roots, observer);
    private sealed class Observer(Action<PayloadPersistencePoint, long, JournalPhase> action) : IPayloadPersistenceObserver
    {
        public void Reached(PayloadPersistencePoint point, Guid journalId, long revision, JournalPhase phase) => action(point, revision, phase);
    }
}

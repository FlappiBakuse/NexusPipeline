using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class RunStoreTests
{
    private static readonly string[] Tables = ["operations", "operation_journals", "file_effects", "journal_writers",
        "journal_resource_claims", "configuration_run_sessions", "configuration_run_resources"];

    [Fact(DisplayName = "Accepting A Run Group Atomically Registers Every Prepared Child And Reopens Its Exact Durable Identity")]
    public void RegistersCompleteGroups()
    {
        using var fixture = new ConfigurationRunFixture(additional: true);
        using var root = fixture.Acquire(fixture.Request);
        using var prepared = Prepare(fixture, root);
        var parent = fixture.Parent(prepared); fixture.Accept(parent);
        var store = Store(fixture); var sessionId = Guid.NewGuid();
        var operation = Identity(sessionId, parent, prepared);
        var accepted = store.Begin(operation, Guid.Parse(parent.JournalId), fixture.StepId, prepared, DateTimeOffset.UtcNow);
        Assert.True(accepted.IsNew); Assert.Equal(ConfigurationRunPhase.Prepared, accepted.Phase); Assert.Equal(1, accepted.Revision);
        Assert.Equal(fixture.Request.Resources.Select(item => item.Target.Address).OrderBy(item => item.ResourceId),
            accepted.Resources.Select(item => item.Address).OrderBy(item => item.ResourceId));
        Assert.Equal(sessionId, accepted.SessionId); Assert.Equal(Guid.Parse(parent.JournalId), accepted.ParentJournalId);
        Assert.Equal(fixture.StepId, accepted.StepId); Assert.Equal(prepared.OwnerOperationId, accepted.OwnerOperationId);
        var registered = new RecoveryJournalRegistry(fixture.Source.Database).ReadSnapshot();
        foreach (var resource in accepted.Resources)
        {
            var journal = Assert.Single(registered.Journals, item => item.JournalId == resource.JournalId);
            Assert.Equal(resource.OperationId, journal.OperationId); Assert.Equal(JournalPhase.Prepared, journal.Phase);
            var expected = prepared.Use(items => items.Single(item => item.Address == resource.Address));
            Assert.Equal(expected.Plan.OperationId, resource.OperationId); Assert.Equal(expected.Plan.JournalId, resource.JournalId);
            Assert.Equal(expected.Reference.RelativePath, journal.PlanPath); Assert.Equal(expected.Reference.Sha256, journal.PlanDigest);
            var effects = registered.FileEffects.Where(item => item.JournalId == resource.JournalId).ToArray();
            foreach (var action in new[] { FileEffectAction.PreserveOriginal, FileEffectAction.InstallCandidate,
                FileEffectAction.RetireCandidate, FileEffectAction.RestoreOriginal })
                Assert.Contains(effects, effect => effect.Action == action && effect.State == FileEffectState.Pending &&
                    effect.IntentDigest == expected.Reference.Sha256 && effect.ReceiptDigest is null);
            var writer = new JournalWriterStore(fixture.Source.Database).Observe(resource.JournalId);
            Assert.True(writer.IsComplete); Assert.Equal(WriterState.Running, writer.State);
            Assert.Equal(1, resource.CurrentRevision);
        }
        var recreated = Store(fixture);
        var reopened = recreated.Read(sessionId, ConfigurationRunFixture.Subject)!;
        Assert.Equal(accepted.Resources.ToArray(), reopened.Resources.ToArray());
        Assert.Equal(accepted.OwnerOperationId, reopened.OwnerOperationId);
        var after = Counts(fixture);
        Assert.False(recreated.Begin(operation, Guid.Parse(parent.JournalId), fixture.StepId, prepared, DateTimeOffset.UtcNow).IsNew);
        Assert.Equal(after, Counts(fixture)); fixture.VerifyOriginal();
    }

    [Theory(DisplayName = "A Failure After The First Run Resource Rolls Back The Whole Group And Every Child Operation Journal And Outbox")]
    [InlineData("journal")]
    [InlineData("resource")]
    [InlineData("group")]
    public void RevertsGroupFailure(string boundary)
    {
        using var fixture = new ConfigurationRunFixture(additional: true);
        using var root = fixture.Acquire(fixture.Request); using var prepared = Prepare(fixture, root);
        var parent = fixture.Parent(prepared); fixture.Accept(parent);
        var second = prepared.Use(resources => resources[1]);
        var condition = boundary == "journal" ? $"NEW.journal_id='{second.Plan.JournalId:D}'"
            : boundary == "resource" ? $"NEW.resource_id='{second.Address.ResourceId}'" : "1";
        var table = boundary == "journal" ? "operation_journals" : boundary == "resource" ? "configuration_run_resources" : "configuration_run_sessions";
        fixture.Execute($"CREATE TRIGGER fixture_fail_run BEFORE INSERT ON {table} WHEN {condition} BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        var before = Counts(fixture); var sessionId = Guid.NewGuid(); var operation = Identity(sessionId, parent, prepared); var store = Store(fixture);
        Assert.Throws<SqliteException>(() => store.Begin(operation, Guid.Parse(parent.JournalId), fixture.StepId, prepared, DateTimeOffset.UtcNow));
        Assert.Null(store.Read(sessionId, ConfigurationRunFixture.Subject));
        Assert.Equal(before, Counts(fixture)); fixture.VerifyOriginal();
        fixture.Execute("DROP TRIGGER fixture_fail_run;");
        Assert.True(store.Begin(operation, Guid.Parse(parent.JournalId), fixture.StepId, prepared, DateTimeOffset.UtcNow).IsNew);
        Assert.Equal(2, store.Read(sessionId, ConfigurationRunFixture.Subject)!.Resources.Length);
    }

    [Theory(DisplayName = "Accepted Run Group Replay Is Bound To Its Subject And Every Frozen Parent Step And Child Reference")]
    [InlineData("subject")]
    [InlineData("step")]
    [InlineData("parent")]
    [InlineData("plans")]
    public void BindsParentReplay(string fault)
    {
        using var fixture = new ConfigurationRunFixture();
        using var root = fixture.Acquire(fixture.Request); using var prepared = Prepare(fixture, root);
        var parent = fixture.Parent(prepared); fixture.Accept(parent);
        var sessionId = Guid.NewGuid(); var store = Store(fixture);
        store.Begin(Identity(sessionId, parent, prepared), Guid.Parse(parent.JournalId), fixture.StepId, prepared, DateTimeOffset.UtcNow);
        using var alternate = fault == "plans" ? fixture.Preparation.Prepare(root, fixture.Owner, fixture.Execution.Plan, fixture.StepId,
            fixture.Request with { AutoSyncEnabled = false, SyncInitially = false, SyncAtFinish = false }) : null;
        var input = alternate ?? prepared;
        var parentId = fault == "parent" ? Guid.NewGuid() : Guid.Parse(parent.JournalId);
        var step = fault == "step" ? "other-step" : fixture.StepId;
        var subject = fault == "subject" ? "another-subject" : ConfigurationRunFixture.Subject;
        var operation = ConfigurationRunStore.BeginIdentity(sessionId, subject, parentId, step, input);
        var before = PersistentFingerprints(fixture.Source.Root);
        Assert.Throws<OperationConflictException>(() => store.Begin(operation, parentId, step, input, DateTimeOffset.UtcNow));
        Assert.Throws<OperationConflictException>(() => store.Read(sessionId, "another-subject"));
        Assert.Equal(before, PersistentFingerprints(fixture.Source.Root)); fixture.VerifyOriginal();
    }

    [Theory(DisplayName = "Run Groups Cannot Borrow An Unaccepted Foreign Or Differently Covered Parent Attempt")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("changed-child")]
    [InlineData("partial-children")]
    public void RejectsForeignParents(string fault)
    {
        using var fixture = new ConfigurationRunFixture(additional: true);
        using var root = fixture.Acquire(fixture.Request); using var prepared = Prepare(fixture, root);
        var parent = fixture.Parent(prepared); var parentId = Guid.Parse(parent.JournalId);
        using var foreign = fault == "foreign" ? new ConfigurationRunFixture() : null;
        using var foreignRoot = foreign?.Acquire(foreign.Request);
        using var foreignPrepared = foreign is null ? null : Prepare(foreign, foreignRoot!);
        if (fault == "foreign")
        {
            var otherParent = foreign!.Parent(foreignPrepared!); foreign.Accept(otherParent); parentId = Guid.Parse(otherParent.JournalId);
        }
        else if (fault != "missing") fixture.Accept(parent);
        using var alternate = fault == "changed-child" ? fixture.Preparation.Prepare(root, fixture.Owner, fixture.Execution.Plan, fixture.StepId,
            fixture.Request with { AutoSyncEnabled = false, SyncInitially = false, SyncAtFinish = false }) : null;
        if (fault == "partial-children")
        {
            var invalid = fixture.Source.Blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(parent with { ConfigurationPlans = parent.ConfigurationPlans.Take(1).ToArray() }));
            fixture.Execute($"UPDATE operation_journals SET plan_path='{invalid.RelativePath}',plan_digest='{invalid.Sha256}' WHERE journal_id='{parentId:D}';");
        }
        var input = alternate ?? prepared; var sessionId = Guid.NewGuid();
        var operation = ConfigurationRunStore.BeginIdentity(sessionId, ConfigurationRunFixture.Subject, parentId, fixture.StepId, input);
        var before = PersistentFingerprints(fixture.Source.Root); var counts = Counts(fixture);
        Assert.ThrowsAny<Exception>(() => Store(fixture).Begin(operation, parentId, fixture.StepId, input, DateTimeOffset.UtcNow));
        Assert.Equal(before, PersistentFingerprints(fixture.Source.Root)); Assert.Equal(counts, Counts(fixture));
        fixture.VerifyOriginal();
    }

    [Theory(DisplayName = "Run Group Acceptance Rechecks Current Binding And Configuration Cas After Preparation")]
    [InlineData("binding")]
    [InlineData("configuration")]
    public void RechecksConfigurationCas(string fault)
    {
        using var fixture = new ConfigurationRunFixture();
        using var root = fixture.Acquire(fixture.Request); using var prepared = Prepare(fixture, root);
        var parent = fixture.Parent(prepared); fixture.Accept(parent);
        if (fault == "binding") fixture.ChangeDefinition("binding-revision"); else fixture.SaveAgain();
        var before = Counts(fixture); var operation = Identity(Guid.NewGuid(), parent, prepared);
        Assert.Equal(fault == "binding" ? "configuration_binding_revision_conflict" : "configuration_revision_conflict",
            Assert.Throws<ConfigurationException>(() => Store(fixture).Begin(operation, Guid.Parse(parent.JournalId), fixture.StepId, prepared, DateTimeOffset.UtcNow)).Message);
        Assert.Equal(before, Counts(fixture)); fixture.VerifyOriginal();
    }

    [Fact(DisplayName = "A Run Group Enters Running Only After Every Actual Configuration Was Installed And Keeps Its Frozen Baseline")]
    public void RequiresInstalledResources()
    {
        using var fixture = new ConfigurationRunFixture(additional: true);
        using var root = fixture.Acquire(fixture.Request); using var prepared = Prepare(fixture, root);
        var parent = fixture.Parent(prepared); fixture.Accept(parent); var sessionId = Guid.NewGuid(); var store = Store(fixture);
        var session = store.Begin(Identity(sessionId, parent, prepared), Guid.Parse(parent.JournalId), fixture.StepId, prepared, DateTimeOffset.UtcNow);
        foreach (var resource in session.Resources) fixture.Install(resource.JournalId);
        var running = store.MarkRunning(sessionId, ConfigurationRunFixture.Subject, 1);
        Assert.True(running.IsNew); Assert.Equal(ConfigurationRunPhase.Running, running.Phase); Assert.Equal(2, running.Revision);
        Assert.False(running.IsWriterAdmissionClosed);
        foreach (var resource in running.Resources)
            Assert.Equal(1, new ConfigurationRevisionStore(fixture.Source.Instance, fixture.Source.Database, new()).Read(resource.Address)!.Revision);
        Assert.Equal(running.Resources.ToArray(), Store(fixture).Read(sessionId, ConfigurationRunFixture.Subject)!.Resources.ToArray());
        fixture.Restore();
    }

    [Theory(DisplayName = "Incomplete Installation Stale Baselines And Closed Admission Cannot Advance A Run Group To Running")]
    [InlineData("not-installed")]
    [InlineData("partial")]
    [InlineData("binding")]
    [InlineData("configuration")]
    [InlineData("closed-gate")]
    [InlineData("revision")]
    public void RejectsUnreadyGroups(string fault)
    {
        using var fixture = new ConfigurationRunFixture(additional: true);
        using var root = fixture.Acquire(fixture.Request); using var prepared = Prepare(fixture, root);
        var parent = fixture.Parent(prepared); fixture.Accept(parent); var sessionId = Guid.NewGuid(); var store = Store(fixture);
        var session = store.Begin(Identity(sessionId, parent, prepared), Guid.Parse(parent.JournalId), fixture.StepId, prepared, DateTimeOffset.UtcNow);
        foreach (var resource in session.Resources.Take(fault == "not-installed" ? 0 : fault == "partial" ? 1 : 2)) fixture.Install(resource.JournalId);
        if (fault == "binding") fixture.ChangeDefinition("binding-revision");
        if (fault == "configuration") fixture.SaveAgain();
        if (fault == "closed-gate") fixture.Execute($"UPDATE configuration_run_sessions SET writer_gate=1 WHERE session_id='{sessionId:D}';");
        var before = PersistentFingerprints(fixture.Source.Root);
        Assert.ThrowsAny<Exception>(() => store.MarkRunning(sessionId, ConfigurationRunFixture.Subject, fault == "revision" ? 2 : 1));
        Assert.Equal(before, PersistentFingerprints(fixture.Source.Root));
        var after = store.Read(sessionId, ConfigurationRunFixture.Subject)!;
        Assert.Equal(ConfigurationRunPhase.Prepared, after.Phase); Assert.Equal(1, after.Revision);
        fixture.Restore();
    }

    private static PreparedConfigurationRun Prepare(ConfigurationRunFixture fixture, NexusPipeline.Core.Maintenance.ResourceOperationLease root) =>
        fixture.Preparation.Prepare(root, fixture.Owner, fixture.Execution.Plan, fixture.StepId, fixture.Request);
    private static ConfigurationRunStore Store(ConfigurationRunFixture fixture) => new(fixture.Source.Instance, fixture.Source.Database, new());
    private static OperationIdentity Identity(Guid session, RunAttemptPlan parent, PreparedConfigurationRun prepared) =>
        ConfigurationRunStore.BeginIdentity(session, ConfigurationRunFixture.Subject, Guid.Parse(parent.JournalId), parent.ConfigurationPlans[0].StepId, prepared);
    private static Dictionary<string, long> Counts(ConfigurationRunFixture fixture) => Tables.ToDictionary(table => table, fixture.Count, StringComparer.Ordinal);
    private static string[] PersistentFingerprints(string root)
    {
        var wal = Path.Combine(root, "runtime", "state", "nexus.db-wal");
        // SQLite may create or remove an empty WAL without transaction frames; nonempty WAL bytes remain evidence.
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "instance.lock" && !path.EndsWith("-shm", StringComparison.Ordinal) &&
                !(path.Equals(wal, StringComparison.OrdinalIgnoreCase) && new FileInfo(path).Length == 0))
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    }
}

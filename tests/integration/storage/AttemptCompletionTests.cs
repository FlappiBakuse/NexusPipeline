using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AttemptCompletionTests
{
    [Theory(DisplayName = "A Real Accepted Configuration Group Defers Its Stopped Sealed Parent And Retains Every Isolation")]
    [InlineData("registered")]
    [InlineData("installed")]
    [InlineData("restored")]
    public void DefersBoundParent(string stage)
    {
        using var fixture = new AttemptCompletionFixture();
        if (stage != "registered") foreach (var resource in fixture.Group.Resources) fixture.Source.Install(resource.JournalId);
        if (stage == "restored") fixture.Source.Restore();
        fixture.Seal(); var before = fixture.ParentBytes();
        var registry = new RecoveryJournalRegistry(fixture.Source.Source.Database); var snapshot = registry.ReadSnapshot();
        var groups = ConfigurationCompletionBinding.Read(fixture.Source.Source.Instance, fixture.Source.Source.Database, new(), fixture.ParentJournalId);
        Assert.Equal(fixture.Group.SessionId, Assert.Single(groups).SessionId);
        Assert.Equal(JournalRecoveryResult.PendingConfigurationCompletion, fixture.Recover());
        Assert.Equal(JournalRecoveryResult.PendingConfigurationCompletion, fixture.Recover());
        var after = registry.ReadSnapshot();
        Assert.Equal(snapshot.Revision, after.Revision); Assert.Equal(snapshot.Isolations.ToArray(), after.Isolations.ToArray());
        var parent = Assert.Single(after.Journals, item => item.JournalId == fixture.ParentJournalId);
        Assert.Equal(JournalPhase.Prepared, parent.Phase); Assert.Equal(1, parent.Revision);
        Assert.Equal(before, fixture.ParentBytes());
        Assert.Null(new AttemptReceiptStore(fixture.Source.Source.Database).Read(Guid.Parse(fixture.Parent.AttemptId)));
        Assert.Equal(EffectGateState.Sealed, new JournalEffectGate(fixture.Source.Source.Database).Read(fixture.ParentJournalId).State);
        if (stage == "installed") Assert.Contains("\"one\":true", File.ReadAllText(
            Path.Combine(fixture.Source.Request.Resources[0].Target.TargetPath, "tasks.json")));
    }

    [Theory(DisplayName = "Unowned Nonconfiguration Effects Cannot Use The Configuration Pending Branch")]
    [InlineData("log-content")]
    [InlineData("log-identity")]
    [InlineData("output")]
    [InlineData("log-directory")]
    public void RejectsUnownedEffects(string fault)
    {
        using var fixture = new AttemptCompletionFixture(); fixture.Seal();
        var log = Assert.Single(fixture.Parent.InitialLogs).Path;
        if (fault == "log-content") File.WriteAllText(log, "unexpected external log");
        if (fault == "log-identity") { var bytes = File.ReadAllBytes(log); File.Move(log, log + ".original"); File.WriteAllBytes(log, bytes); }
        if (fault == "output") Directory.CreateDirectory(Path.Combine(fixture.Source.Source.Instance.RuntimeRoot, fixture.Parent.OutputDirectory));
        if (fault == "log-directory") Directory.CreateDirectory(Path.Combine(fixture.Source.Source.Instance.RuntimeRoot, fixture.Parent.LogDirectory));
        Assert.Throws<IOException>(() => ConfigurationCompletionBinding.Read(fixture.Source.Source.Instance,
            fixture.Source.Source.Database, new(), fixture.ParentJournalId));
        Assert.Equal(JournalRecoveryResult.Quarantined, fixture.Recover());
        Assert.Null(new AttemptReceiptStore(fixture.Source.Source.Database).Read(Guid.Parse(fixture.Parent.AttemptId)));
        Assert.NotEmpty(new RecoveryJournalRegistry(fixture.Source.Source.Database).ReadSnapshot().Isolations);
    }

    [Theory(DisplayName = "Group And Frozen Child Metadata Cannot Be Partially Matched To Authorize Pending Recovery")]
    [InlineData("parent-subject")]
    [InlineData("group-subject")]
    [InlineData("child-subject")]
    [InlineData("group-owner")]
    [InlineData("group-parent")]
    [InlineData("child-plan")]
    [InlineData("resource-operation")]
    [InlineData("child-mode")]
    [InlineData("orphan-child")]
    public void RequiresWholeBinding(string fault)
    {
        using var fixture = new AttemptCompletionFixture(); fixture.Seal();
        var resource = fixture.Group.Resources[0]; var changed = new string('c', 64);
        var sql = fault switch
        {
            "parent-subject" => $"UPDATE main.operations SET subject_id='foreign' WHERE operation_id='{fixture.Source.Owner:D}';",
            "group-subject" => $"UPDATE main.operations SET subject_id='foreign' WHERE operation_id='{fixture.Group.SessionId:D}';",
            "child-subject" => $"UPDATE main.operations SET subject_id='foreign' WHERE operation_id='{resource.OperationId:D}';",
            "group-owner" => $"UPDATE configuration.configuration_run_sessions SET owner_operation_id='{fixture.Group.SessionId:D}' WHERE session_id='{fixture.Group.SessionId:D}';",
            "group-parent" => $"UPDATE configuration.configuration_run_sessions SET parent_journal_id='{resource.JournalId:D}' WHERE session_id='{fixture.Group.SessionId:D}';",
            "child-plan" => $"UPDATE configuration.configuration_run_resources SET plan_path='blobs/plans/{changed}.json',plan_digest='{changed}' WHERE journal_id='{resource.JournalId:D}';",
            "resource-operation" => $"UPDATE configuration.configuration_run_resources SET operation_id='{fixture.Group.SessionId:D}' WHERE journal_id='{resource.JournalId:D}';",
            "child-mode" => $"UPDATE main.journal_resource_claims SET mode=0 WHERE journal_id='{resource.JournalId:D}' AND resource_key LIKE 'configuration:%';",
            _ => $"DELETE FROM configuration.configuration_run_resources WHERE session_id='{fixture.Group.SessionId:D}'; DELETE FROM configuration.configuration_run_sessions WHERE session_id='{fixture.Group.SessionId:D}';"
        };
        fixture.Source.Execute(sql); var before = fixture.ParentBytes();
        Assert.Throws<IOException>(() => ConfigurationCompletionBinding.Read(fixture.Source.Source.Instance,
            fixture.Source.Source.Database, new(), fixture.ParentJournalId));
        Assert.Equal(before, fixture.ParentBytes());
        Assert.Null(new AttemptReceiptStore(fixture.Source.Source.Database).Read(Guid.Parse(fixture.Parent.AttemptId)));
    }

    [Fact(DisplayName = "A Registered Native Job That Disappeared Without A Closure Cannot Become Pending Evidence")]
    public void RequiresNativeClosure()
    {
        using var fixture = new AttemptCompletionFixture(); fixture.Seal();
        using (var job = new OwnedWorkerJob())
            new JournalWriterStore(fixture.Source.Source.Database).RegisterJob(fixture.Group.Resources.Select(item => item.JournalId).ToArray(), job);
        Assert.Throws<IOException>(() => ConfigurationCompletionBinding.Read(fixture.Source.Source.Instance,
            fixture.Source.Source.Database, new(), fixture.ParentJournalId));
        Assert.Equal(JournalRecoveryResult.Quarantined, fixture.Recover());
        Assert.Null(new AttemptReceiptStore(fixture.Source.Source.Database).Read(Guid.Parse(fixture.Parent.AttemptId)));
    }

    [Fact(DisplayName = "An Entered Parent Cannot Defer Even When Its Configuration Group Is Completely Bound")]
    public void RejectsEnteredDeferral()
    {
        using var fixture = new AttemptCompletionFixture();
        var parent = new RecoveryJournalRegistry(fixture.Source.Source.Database).ReadSnapshot().Journals.Single(item => item.JournalId == fixture.ParentJournalId);
        new JournalEffectGate(fixture.Source.Source.Database).Enter(parent, 1);
        Assert.Throws<IOException>(() => ConfigurationCompletionBinding.Read(fixture.Source.Source.Instance,
            fixture.Source.Source.Database, new(), fixture.ParentJournalId));
        Assert.Equal(JournalRecoveryResult.Quarantined, fixture.Recover());
        Assert.Null(new AttemptReceiptStore(fixture.Source.Source.Database).Read(Guid.Parse(fixture.Parent.AttemptId)));
    }

    [Fact(DisplayName = "A Quarantined Configuration Child Cannot Supply The Pending Parent Evidence")]
    public void RejectsQuarantinedChildren()
    {
        using var fixture = new AttemptCompletionFixture(); fixture.Seal();
        var child = fixture.Group.Resources[0].JournalId;
        new ConfigurationEditExchange(fixture.Source.Source.Instance,
            fixture.Source.Source.Database, new()).Materialize(child);
        new ConfigurationRecoverySession(fixture.Source.Source.Instance, child, new(), fixture.Source.Source.Database, () =>
        {
            var observed = new JournalWriterStore(fixture.Source.Source.Database).ObserveForCurrentExecutor(child);
            return new(fixture.Source.Source.Instance.InstanceId, child, observed.IsComplete, observed.State);
        }).Quarantine("fixture_configuration_unknown");
        Assert.Throws<IOException>(() => ConfigurationCompletionBinding.Read(fixture.Source.Source.Instance,
            fixture.Source.Source.Database, new(), fixture.ParentJournalId));
        Assert.Equal(JournalRecoveryResult.Quarantined, fixture.Recover());
        var snapshot = new RecoveryJournalRegistry(fixture.Source.Source.Database).ReadSnapshot();
        Assert.Equal(JournalPhase.Quarantined, Assert.Single(snapshot.Journals, item => item.JournalId == child).Phase);
        Assert.Contains(snapshot.Isolations, item => item.JournalId == child);
        Assert.Null(new AttemptReceiptStore(fixture.Source.Source.Database).Read(Guid.Parse(fixture.Parent.AttemptId)));
    }

    [Fact(DisplayName = "A Real Typed Completion Intent Ahead Of Its Sql Prefix Reconciles As Prepared Without A No Effect Receipt")]
    public void ReconcilesTypedIntent()
    {
        using var fixture = new AttemptCompletionFixture(); fixture.Seal();
        var exchange = new ConfigurationEditExchange(fixture.Source.Source.Instance, fixture.Source.Source.Database, new());
        foreach (var resource in fixture.Group.Resources) Assert.Equal(ConfigurationRecoveryResult.NotExecuted, exchange.Restore(resource.JournalId));
        new WriterAdmissionStore(fixture.Source.Source.Instance, fixture.Source.Source.Database, new())
            .CloseRunAdmission(fixture.Group.SessionId, 0, requireNoJobs: true);
        var group = new ConfigurationRunStore(fixture.Source.Source.Instance, fixture.Source.Source.Database, new())
            .Read(fixture.Group.SessionId, ConfigurationRunFixture.Subject)!;
        var completion = Guid.NewGuid();
        try
        {
            Assert.Throws<SqliteException>(() => new SnapshotCompletionOwner(fixture.Source.Source.Instance,
                fixture.Source.Source.Database, new(), fixture.Source.Lease).Prepare(completion, ConfigurationRunFixture.Subject, group, () =>
                fixture.Source.Execute("CREATE TRIGGER main.fixture_fail_parent_projection BEFORE UPDATE ON operation_journals " +
                    $"WHEN OLD.journal_id='{fixture.ParentJournalId:D}' BEGIN SELECT RAISE(ABORT,'fixture_parent_projection'); END;")));
        }
        finally { fixture.Source.Execute("DROP TRIGGER IF EXISTS fixture_fail_parent_projection;"); }
        var registry = new RecoveryJournalRegistry(fixture.Source.Source.Database);
        Assert.Equal(1, Assert.Single(registry.ReadSnapshot().Journals, item => item.JournalId == fixture.ParentJournalId).Revision);
        var before = fixture.ParentBytes();
        Assert.Equal(2, new JournalStore(fixture.Source.Source.Instance, new()).Read(fixture.ParentJournalId).Revision);
        Assert.Equal(JournalRecoveryResult.PendingConfigurationCompletion, fixture.Recover());
        var parent = Assert.Single(registry.ReadSnapshot().Journals, item => item.JournalId == fixture.ParentJournalId);
        Assert.Equal(JournalPhase.Prepared, parent.Phase); Assert.Equal(2, parent.Revision);
        Assert.Equal(before, fixture.ParentBytes());
        var physical = new JournalStore(fixture.Source.Source.Instance, new()).Read(fixture.ParentJournalId);
        Assert.Equal(completion, Assert.Single(SnapshotIntentCodec.Read(physical.Root)).CompletionOperationId);
        Assert.DoesNotContain(physical.Root.GetProperty("receipts").EnumerateArray(), receipt => receipt.GetProperty("stepId").GetString() == "verify-no-effect");
        Assert.Null(new AttemptReceiptStore(fixture.Source.Source.Database).Read(Guid.Parse(fixture.Parent.AttemptId)));
        Assert.Contains(registry.ReadSnapshot().Isolations, item => item.JournalId == fixture.ParentJournalId);
    }
}

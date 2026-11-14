using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExecutionHooksTests
{
    [Theory(DisplayName = "Real Pre Main And Post Jobs Keep Hook Bytes Out Of Main Business And Restore Only After Post")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdersHookJobs(bool configuration)
    {
        using var fixture = new ExecutionHooksFixture(configuration);
        var bytes = File.ReadAllBytes(fixture.Source.Configuration); var original = PhysicalObjectIdentity.Read(fixture.Source.Configuration);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
        Assert.Equal(new[] { "preHook", "process", "postHook" }, fixture.State.Jobs.Select(job => job.Role));
        Assert.Equal(3, fixture.State.Jobs.Length); Assert.Single(fixture.State.Steps);
        Assert.True(File.Exists(fixture.Marker + ".pre")); Assert.True(File.Exists(fixture.Marker + ".main")); Assert.True(File.Exists(fixture.Marker + ".post"));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Source.Configuration)); Assert.Equal(original, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
        AssertStoppedHistory(fixture);
        if (configuration) AssertSavedStage(fixture, 2, "post");
    }

    [Theory(DisplayName = "A Real Failed Pre Hook Suppresses Main And Post Without Allocating Their Jobs Or Inventing Business")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuppressesFailedPre(bool configuration)
    {
        using var fixture = new ExecutionHooksFixture(configuration, preExit: 9);
        var bytes = File.ReadAllBytes(fixture.Source.Configuration); var original = PhysicalObjectIdentity.Read(fixture.Source.Configuration);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Empty(fixture.State.Steps); Assert.Equal("preHook", Assert.Single(fixture.State.Jobs).Role);
        var suppressed = Assert.Single(BindingSuppressionCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        Assert.Equal(new[] { "process", "postHook" }, suppressed.Roles); Assert.Equal("execution_pre_hook_failed", suppressed.ReasonCode);
        Assert.Null(suppressed.DispatchOperationId); Assert.Empty(StepResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        Assert.False(File.Exists(fixture.Marker + ".main")); Assert.False(File.Exists(fixture.Marker + ".post"));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Source.Configuration)); Assert.Equal(original, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
        AssertStoppedHistory(fixture);
        if (configuration) AssertSavedStage(fixture, 1, null);
    }

    [Theory(DisplayName = "A Real Post Failure Changes Engine While Preserving The Durable Main Business And Completed Lifecycle")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesMainBusiness(bool configuration)
    {
        using var fixture = new ExecutionHooksFixture(configuration, postExit: 7);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business); Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        var main = Assert.Single(StepResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        Assert.Equal(EngineOutcome.Succeeded, main.Latest.Engine); Assert.Equal(BusinessOutcome.Succeeded, main.Latest.Business);
        var post = Assert.Single(HookResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total), hook => hook.Role == "postHook");
        Assert.Equal(EngineOutcome.Failed, post.Engine); AssertStoppedHistory(fixture);
    }

    [Fact(DisplayName = "Accepted Cancellation During The Real Pre Hook Stops Its Family And Never Dispatches Main Or Post")]
    public async Task CancelsPreFamily()
    {
        using var fixture = new ExecutionHooksFixture(configuration: true, waitPre: true);
        var bytes = File.ReadAllBytes(fixture.Source.Configuration); var original = PhysicalObjectIdentity.Read(fixture.Source.Configuration);
        var running = fixture.Consumer.RunNextAsync(); await fixture.Ready("pre"); fixture.Cancel();
        var receipt = Assert.IsType<AttemptReceipt>(await running);
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Equal("preHook", Assert.Single(fixture.State.Jobs).Role); Assert.Empty(fixture.State.Steps);
        Assert.Empty(StepResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        Assert.False(File.Exists(fixture.Marker + ".main")); Assert.False(File.Exists(fixture.Marker + ".post"));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Source.Configuration)); Assert.Equal(original, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
        AssertSavedStage(fixture, 1, null); AssertStoppedHistory(fixture);
    }

    [Fact(DisplayName = "A Real Second Role Child Registration Failure Retains The Actual Empty Main Owner And Restores The Pre Modified Scene")]
    public async Task RetainsFailedRegistration()
    {
        using var fixture = new ExecutionHooksFixture(configuration: true);
        var bytes = File.ReadAllBytes(fixture.Source.Configuration); var original = PhysicalObjectIdentity.Read(fixture.Source.Configuration);
        fixture.Base.Execute("CREATE TRIGGER main.fixture_hook_child_registration BEFORE INSERT ON journal_writer_jobs " +
            "WHEN NEW.journal_id IN (SELECT journal_id FROM main.operation_journals WHERE operation_kind='configExchange') AND " +
            "(SELECT count(*) FROM main.journal_writer_jobs WHERE journal_id=NEW.journal_id)=1 " +
            "BEGIN SELECT RAISE(ABORT,'fixture_hook_child_registration'); END;");
        try { await Assert.ThrowsAsync<SqliteException>(() => fixture.Consumer.RunNextAsync()); }
        finally { fixture.Base.Execute("DROP TRIGGER main.fixture_hook_child_registration;"); }
        var state = fixture.State; Assert.Equal(JournalPhase.Committed, state.Journal.Phase);
        var main = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal(EngineOutcome.NotStarted, main.Latest.Engine); Assert.Equal(BusinessOutcome.Unverified, main.Latest.Business);
        var job = Assert.Single(state.Jobs, job => job.Role == "process");
        var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadForExecutionEvidence(state.Journal.JournalId, job.WriterId, job.JobName, job.SessionId)!;
        Assert.False(closure.WasExecuted); Assert.Empty(closure.Family); Assert.Equal(0, closure.RunElapsedMilliseconds);
        Assert.False(File.Exists(fixture.Marker + ".main")); Assert.False(File.Exists(fixture.Marker + ".post"));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Source.Configuration)); Assert.Equal(original, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
        Assert.Equal(0, fixture.Consumer.PendingReconciliationCount); AssertSavedStage(fixture, 1, null);
        var receipt = new AttemptReceiptStore(fixture.Source.Database).Read(Guid.Parse(state.Attempt.AttemptId))!;
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle); AssertStoppedHistory(fixture);
    }

    [Theory(DisplayName = "Accepted Cancellation Before Hook Consumption Creates No Auxiliary Or Primary Job")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelsBeforeConsumption(bool configuration)
    {
        using var fixture = new ExecutionHooksFixture(configuration);
        var bytes = File.ReadAllBytes(fixture.Source.Configuration); fixture.Cancel();
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business); Assert.Equal(RecoveryOutcome.NotRequired, receipt.Facts.Recovery);
        Assert.Empty(fixture.State.Jobs); Assert.Empty(fixture.State.Steps); Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
        Assert.All(fixture.State.Coverage, item => Assert.Equal(StepCompletionKind.NotDispatched, item.Kind));
        Assert.False(File.Exists(fixture.Marker + ".pre")); Assert.False(File.Exists(fixture.Marker + ".main"));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Source.Configuration)); Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Fact(DisplayName = "A Real Registered Empty Pre Job Can Be Cancelled Before Entry Without Manufacturing A Main Job")]
    public async Task CancelsEmptyPre()
    {
        using var fixture = new ExecutionHooksFixture(configuration: true); var source = fixture.Source;
        var bytes = File.ReadAllBytes(source.Configuration); var original = PhysicalObjectIdentity.Read(source.Configuration);
        var accepted = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
        using var session = new AttemptRuntimeSession(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index, accepted);
        session.PrepareConfigurationGroups(); session.PrepareNextStep(session.StepIds.Single()); fixture.Cancel();
        session.FreezeDispatch(ExecutionStopReason.Cancelled); await session.CollectRemainingAsync(); session.CloseUndispatched();
        await session.RestoreRemainingAsync(ExecutionStopReason.Cancelled);
        var receipt = session.Commit(ExecutionStopReason.Cancelled);
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.NotStarted, receipt.Facts.Engine);
        Assert.Equal(RecoveryOutcome.NotRequired, receipt.Facts.Recovery); Assert.Equal(EffectGateState.Sealed, fixture.State.Gate.State);
        var job = Assert.Single(fixture.State.Jobs); Assert.Equal("preHook", job.Role); Assert.Empty(fixture.State.Steps);
        var closure = new JobClosureStore(source.Instance, source.Database, new())
            .ReadForExecutionEvidence(fixture.State.Journal.JournalId, job.WriterId, job.JobName, job.SessionId)!;
        Assert.False(closure.WasExecuted); Assert.Empty(closure.Family); Assert.Equal(0, closure.RunElapsedMilliseconds);
        Assert.Equal(bytes, File.ReadAllBytes(source.Configuration)); Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration));
        Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots")); Assert.Equal(0, fixture.Base.Count("resource_isolations"));
    }

    [Fact(DisplayName = "Historical Hook Raw References Remain Readable When The External Configuration Is Temporarily Absent")]
    public async Task ReadsHistoricalHooks()
    {
        using var fixture = new ExecutionHooksFixture(configuration: true);
        _ = await fixture.Consumer.RunNextAsync();
        var path = fixture.Source.Configuration; var moved = path + ".saved";
        File.Move(path, moved);
        try
        {
            var before = fixture.Source.Snapshot();
            foreach (var hook in HookResultCodec.Read(fixture.State.Latest.Root, fixture.State.Total))
            {
                var reader = new HookEvidenceReader(fixture.Source.Instance, fixture.Source.Database, new());
                var document = reader.Read(hook.Binding, hook.Role, hook.Artifact);
                Assert.Equal(hook.Engine, document.Engine); Assert.Contains(document.RawOutput!, reader.ReadReferences(hook.Binding, hook.Role, hook.Artifact));
            }
            var after = fixture.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        }
        finally { File.Move(moved, path); }
    }

    private static void AssertStoppedHistory(ExecutionHooksFixture fixture)
    {
        var state = fixture.State; var source = fixture.Source;
        var store = new JobClosureStore(source.Instance, source.Database, new());
        foreach (var job in state.Jobs)
        {
            var closure = Assert.IsType<FrozenJobClosure>(store.ReadForExecutionEvidence(state.Journal.JournalId, job.WriterId, job.JobName, job.SessionId));
            Assert.Equal(job.ClosedElapsedMilliseconds, closure.RunElapsedMilliseconds);
            Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            Assert.Equal(closure.Family.Length, closure.Family.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count());
        }
        Assert.All(state.Coverage, item => Assert.Equal(StepCompletionKind.Collected, item.Kind));
        Assert.Equal(0, fixture.Base.Count("resource_isolations")); Assert.True(fixture.Base.Index.Current.IsReady);
        var budget = JournalCapacityPolicy.Measure(state.Total);
        var history = new JournalStore(source.Instance, new()).ReadAllRevisions(state.Journal.JournalId);
        Assert.True(history.Count <= budget.Revisions); Assert.True(history.Sum(item => item.Export().LongLength) <= budget.HistoryBytes);
        Assert.All(history, item => Assert.True(item.Export().LongLength <= budget.MaximumDocumentBytes));
    }

    private static void AssertSavedStage(ExecutionHooksFixture fixture, long revision, string? stage)
    {
        var path = fixture.Source.Definitions.Read().Scripts.Single(script => script.Id == fixture.Source.ScriptId).ConfigurationPaths.Single();
        var saved = new ConfigurationRevisionStore(fixture.Source.Instance, fixture.Source.Database, new())
            .Read(new(fixture.Source.BindingA, GenericConfigurationPolicy.ResourceId(path)))!;
        Assert.Equal(revision, saved.Revision);
        var file = saved.Tree.Entries.Single(entry => entry.Kind == FileObjectKind.File);
        using var content = new ImmutableBlobStore(fixture.Source.Instance, new())
            .OpenVerified(new($"blobs/content/{file.Sha256}.bin", file.SizeBytes, file.Sha256!));
        using var document = JsonDocument.Parse(content);
        if (stage is null) Assert.True(document.RootElement.GetProperty("fixture").GetBoolean());
        else Assert.Equal(stage, document.RootElement.GetProperty("stage").GetString());
    }
}

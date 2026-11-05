using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BusinessPreservationTests
{
    [Fact(DisplayName = "Persisted Raw Bindings And User Overrides Decay Once Per Observed Day And Round Trip Through The Current Format")]
    public void PreservesDailyOverrides()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var before = source.Definitions.Read();
        var user = before.Users.Single(user => user.Id == source.UserA) with
            { BindingOverrides = new() { General = new(true, true, 3, 2) } };
        var changes = new AutomationChanges(before.Revision, [], [], [user], before.Bindings.Select(binding => binding with
            { RunDays = 7, MaximumSuccessfulRunsPerDay = 3 }).ToImmutableArray(), [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "business-fixture", changes), changes, DateTimeOffset.UtcNow);
        var clock = new Clock();
        var maintenance = new RunDaysMaintenance(source.Instance, source.Database, source.Lease, fixture.Resources, clock);
        maintenance.Tick(); var initial = source.Definitions.Read();
        var commits = source.Database.CommitSequence;
        maintenance.Tick(); maintenance.Tick();
        Assert.Equal(commits, source.Database.CommitSequence);
        Assert.Equal(3, initial.Users.Single(item => item.Id == source.UserA).BindingOverrides.General.RunDays);
        clock.Now += TimeSpan.FromDays(3); maintenance.Tick(); maintenance.Tick();
        var saved = source.Definitions.Read();
        commits = source.Database.CommitSequence;
        clock.Now -= TimeSpan.FromDays(1); maintenance.Tick();
        Assert.Equal(commits, source.Database.CommitSequence);
        clock.Now += TimeSpan.FromDays(1);
        Assert.Equal(2, saved.Users.Single(item => item.Id == source.UserA).BindingOverrides.General.RunDays);
        Assert.Equal(7, saved.Bindings.Single(item => item.Id == source.BindingA).RunDays);
        Assert.Equal(6, saved.Bindings.Single(item => item.Id == source.BindingB).RunDays);
        clock.Now += TimeSpan.FromDays(3);
        new RunDaysMaintenance(source.Instance, source.Database, source.Lease, fixture.Resources, clock).Tick();
        Assert.Equal(saved.Revision, source.Definitions.Read().Revision);
        var codec = new AutomationBundleCodec(new());
        var imported = codec.Decode(codec.Encode(saved, clock.Now), saved.Roots.ToDictionary(root => root.Id, root => root.Path), 0).Definitions;
        Assert.Equal(2, imported.Users.Single(item => item.Id == source.UserA).BindingOverrides.General.RunDays);
        Assert.Equal(3, imported.Bindings.Single(item => item.Id == source.BindingA).MaximumSuccessfulRunsPerDay);
    }

    [Fact(DisplayName = "Actual Failed Queue Members Skip Only Their Named Dependents And Do Not Create A Worker For The Skipped Binding")]
    public async Task SkipsFailedDependencies()
    {
        using var fixture = new ExecutionRuntimeFixture(independentSuccessors: true, successDependency: true);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var state = fixture.State;
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        var skip = Assert.Single(BindingSkipCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal(state.Total.Steps[1].StepId, skip.StepId);
        Assert.Equal("execution_queue_dependency_not_successful", skip.ReasonCode);
        Assert.Equal(new[] { state.Total.Steps[0].StepId, state.Total.Steps[2].StepId }, state.Steps.Select(step => step.StepId));
        Assert.Equal(2, fixture.Base.Count("journal_writer_job_roots"));
        Assert.True(ExecutionBindingRoles.IsComplete(state.Latest.Root, state.Total, skip.StepId));
        Assert.Empty(state.Jobs.Where(job => job.StepId == skip.StepId));
        Assert.True(fixture.Base.Index.Current.IsReady); Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
    }

    [Fact(DisplayName = "Actual Unlimited Native Execution Remains Cancellable And Persists Its Original Unlimited Budget")]
    public async Task CancelsUnlimitedExecution()
    {
        using var fixture = new ExecutionRuntimeFixture(mode: "wait", limits: new(1, 0, 0));
        var running = fixture.Consumer.RunNextAsync(); await fixture.Ready(".output-ready"); fixture.Cancel();
        var receipt = Assert.IsType<AttemptReceipt>(await running);
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
        Assert.Equal(0, fixture.State.Total.Budget.TotalTimeoutSeconds);
        Assert.Equal(DateTimeOffset.MaxValue, DateTimeOffset.Parse(fixture.State.Total.RetryPolicy.DeadlineUtc,
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, fixture.Base.Count("journal_writer_job_roots"));
        RunBudgetInventory.Verify(fixture.Source.Instance, fixture.Source.Database, new());
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Fact(DisplayName = "Frozen Effective Hooks Do Not Require The Inactive Raw Hook Root Or Change The Saved Definitions")]
    public void FreezesEffectiveHooks()
    {
        using var fixture = new PlanBuilderFixture(); var catalog = fixture.Definitions.Read();
        var raw = catalog.Bindings.Single(binding => binding.Id == fixture.OtherBinding);
        var script = catalog.Scripts.Single(script => script.Id == raw.ScriptId);
        var inactiveRoot = catalog.Roots.Single(root => root.Kind == AutomationRootKind.External);
        var user = catalog.Users.Single(user => user.Id == raw.UserId) with { BindingOverrides = new()
            { Advanced = new(true, new(new(script.RootId, Path.GetFileName(fixture.OtherImage)), []), null, false, false) } };
        var changes = new AutomationChanges(catalog.Revision, [], [], [user],
            [raw with { PreHook = new(new(inactiveRoot.Id, "inactive-hook.exe"), []) }], [], [], []);
        fixture.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "effective-hook-fixture", changes), changes, DateTimeOffset.UtcNow);
        using var draft = fixture.Prepare(new BindingTarget(raw.Id));
        var plan = Assert.IsType<FrozenExecutionPlan>(draft.ReadPlan()).ReadWire();
        Assert.Single(plan.RootBindings);
        Assert.Equal(fixture.OtherImage, Assert.Single(Assert.Single(plan.Steps).Hooks!).Command.Path);
        Assert.Equal(inactiveRoot.Id, fixture.Definitions.Read().Bindings.Single(binding => binding.Id == raw.Id).PreHook!.Executable.RootId);
    }

    [Fact(DisplayName = "A User General Override Can Enable The Real Saved Configuration Of A Raw Disabled Binding")]
    public void AppliesUserOverrides()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var catalog = source.Definitions.Read();
        var binding = catalog.Bindings.Single(binding => binding.Id == source.BindingA);
        var user = catalog.Users.Single(user => user.Id == binding.UserId) with
            { BindingOverrides = new() { General = new(true, true, -1, -1) } };
        var changes = new AutomationChanges(catalog.Revision, [], [], [user], [binding with { IsEnabled = false }], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "override-configuration-fixture", changes), changes, DateTimeOffset.UtcNow);
        var saved = new NexusPipeline.Platform.Storage.Configuration.ConfigurationRevisionStore(source.Instance, source.Database, new());
        var before = catalog.Scripts.Single(script => script.Id == binding.ScriptId).ConfigurationPaths.Single();
        var address = new NexusPipeline.Core.Configuration.ConfigurationAddress(binding.Id,
            NexusPipeline.Platform.Storage.Configuration.GenericConfigurationPolicy.ResourceId(before));
        var original = saved.Read(address)!;
        var update = new NexusPipeline.Core.Configuration.ConfigurationRevisionChange(address, original.Revision,
            binding.Revision + 1, original.Contract, original.Tree, NexusPipeline.Core.Configuration.ConfigurationUpdateKind.Manual);
        saved.Commit(NexusPipeline.Platform.Storage.Configuration.ConfigurationRevisionStore.Identity(Guid.NewGuid(), "override-configuration-fixture", update), update, DateTimeOffset.UtcNow);
        var accepted = fixture.Service.Start(Guid.NewGuid(), "override-configuration-fixture", source.Request(new BindingTarget(binding.Id)), DateTimeOffset.UtcNow);
        Assert.True(accepted.Acceptance.IsNew); Assert.Equal(1, fixture.Inbox.PendingCount);
        Assert.False(source.Definitions.Read().Bindings.Single(item => item.Id == binding.Id).IsEnabled);
    }

    [Fact(DisplayName = "Ordinary Failed Bindings Retry Before The Next Member And Retain Every Physical Invocation And Log")]
    public async Task RetriesBeforeSuccessor()
    {
        using var fixture = new ExecutionRuntimeFixture(configuration: true, independentSuccessors: true, limits: new(3, 30, 120));
        var original = File.ReadAllBytes(fixture.Source.Configuration);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var state = fixture.State; var first = state.Total.Steps[0].StepId;
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine);
        Assert.False(state.Total.RetryPolicy.HostApproved);
        Assert.Equal(2, ExecutionInvocationCodec.Read(state.Latest.Root, state.Total).Count(item => item.StepId == first));
        Assert.Equal(new[] { first, first, first, state.Total.Steps[1].StepId, state.Total.Steps[2].StepId },
            state.AllJobs.Where(job => job.Role == "process").Select(job => job.StepId));
        Assert.Equal(5, fixture.Base.Count($"journal_writer_job_roots WHERE journal_id='{state.Journal.JournalId:D}'"));
        Assert.Equal(5, fixture.Base.Count("(SELECT DISTINCT job_name FROM journal_writer_job_roots)"));
        Assert.Equal(3, fixture.Base.Count($"journal_writer_job_roots j JOIN configuration_run_resources r ON r.journal_id=j.journal_id " +
            $"JOIN configuration_run_sessions s ON s.session_id=r.session_id WHERE s.parent_journal_id='{state.Journal.JournalId:D}' " +
            "AND s.primary_job_name=j.job_name AND s.invocation_ordinal BETWEEN 1 AND 3"));
        Assert.Equal(3, fixture.Base.Count("configuration_run_sessions"));
        Assert.Equal(0, fixture.Base.Count("execution_retry_intents"));
        Assert.Equal(original, File.ReadAllBytes(fixture.Source.Configuration));
        new LogCatalogProjection(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease).OnCommitted(receipt);
        var logs = new ExecutionLogReader(fixture.Source.Instance, fixture.Source.Database, new());
        var key = new NexusPipeline.Core.Records.RunLogKey(receipt.Facts.RunId, receipt.Facts.AttemptId, first);
        var description = logs.Describe(key);
        using var content = logs.OpenOriginal(key, description.SnapshotSha256);
        using var text = new StreamReader(content, System.Text.Encoding.UTF8);
        Assert.Equal(3, (await text.ReadToEndAsync()).Split("中文尾行", StringSplitOptions.None).Length - 1);
        Assert.True(fixture.Base.Index.Current.IsReady); Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

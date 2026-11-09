using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class BusinessPreservationTests
{
    [Fact(DisplayName = "User Binding Overrides Apply Enabled Groups And Disabled Overrides Preserve Saved Values")]
    public void FreezesBindingOverrides()
    {
        var fixture = new ExecutionSelectionFixture(); var original = fixture.Catalog.Bindings[0];
        var task = Guid.NewGuid(); var root = fixture.Catalog.Roots[0].Id;
        var hook = new AutomationHook(new(root, "before.exe"), ["saved"]);
        var user = fixture.Catalog.Users.Single(user => user.Id == original.UserId) with
        {
            BindingOverrides = new() { General = new(true, true, 4, 2), Notification = new(true, false, [task]),
                Advanced = new(true, hook, null, true, false) }
        };
        var catalog = fixture.Catalog with { Users = fixture.Catalog.Users.Select(item => item.Id == user.Id ? user : item).ToImmutableArray() };
        var selected = Assert.Single(fixture.Select(new BindingTarget(original.Id), catalog).Items).Binding;
        Assert.Equal(4, selected.RunDays); Assert.Equal(2, selected.MaximumSuccessfulRunsPerDay);
        Assert.False(selected.NotifyEnabled); Assert.Equal(task, Assert.Single(selected.NotificationTaskIds));
        Assert.NotNull(selected.PreHook);
        Assert.Equal(hook.Executable, selected.PreHook.Executable);
        Assert.Equal(hook.Arguments.ToArray(), selected.PreHook.Arguments.ToArray());
        Assert.True(selected.PreOnceAfterSuccess);
        var raw = AutomationBindingPolicy.Resolve(user with { BindingOverrides = new() }, original);
        Assert.Equal(original, raw);
    }

    [Theory(DisplayName = "Exhausted Run Days And The Actual Daily Success Quota Skip Before Input Preparation")]
    [InlineData(0, -1, 0, "execution_run_days_exhausted")]
    [InlineData(-1, 2, 2, "execution_daily_success_limit")]
    public void SkipsExhaustedBindings(int days, int limit, int successes, string reason)
    {
        var fixture = new ExecutionSelectionFixture(); var first = fixture.Catalog.Bindings[0] with
            { RunDays = days, MaximumSuccessfulRunsPerDay = limit };
        var catalog = fixture.Catalog with { Bindings = fixture.Catalog.Bindings.Select(item => item.Id == first.Id ? first : item).ToImmutableArray() };
        var selection = ExecutionSelectionPolicy.Select(catalog, fixture.Settings,
            new(new BindingTarget(first.Id), catalog.Revision, fixture.Settings.Revision), new Dictionary<Guid, int> { [first.Id] = successes });
        Assert.Equal(ExecutionSelectionDisposition.Skipped, Assert.Single(selection.Items).Disposition);
        Assert.Equal(reason, selection.Items[0].ReasonCode);
        if (reason == "execution_daily_success_limit")
        {
            Assert.Equal(reason, selection.ReasonCode);
            Assert.Equal(reason, ExecutionSelectionPolicy.Explain(selection, []).ReasonCode);
        }
    }

    [Fact]
    public void EnforcesGroupDependencies()
    {
        var fixture = new ExecutionSelectionFixture();
        var third = fixture.Catalog.Scripts[1] with { Id = Guid.NewGuid(), Name = "Independent" };
        var participant = new AutomationBinding(Guid.NewGuid(), 1, fixture.UserA, third.Id, true, 0);
        var queue = fixture.Catalog.Queues[0] with { ScriptIds = [fixture.ScriptId, fixture.OtherScriptId, third.Id],
            SuccessDependencies = [new(fixture.OtherScriptId, [fixture.ScriptId])] };
        var catalog = fixture.Catalog with { Queues = [queue], Scripts = fixture.Catalog.Scripts.Add(third), Bindings = fixture.Catalog.Bindings
            .Select(item => item.ScriptId == fixture.ScriptId ? item with { RunDays = 0 } : item).Append(participant).ToImmutableArray() };
        var selection = fixture.Select(new QueueTarget(queue.Id), catalog);
        Assert.Equal("execution_queue_dependency_not_successful", selection.Items.Single(item => item.Script.Id == fixture.OtherScriptId).ReasonCode);
        Assert.Equal(ExecutionSelectionDisposition.Candidate, selection.Items.Single(item => item.Script.Id == third.Id).Disposition);
        Assert.Throws<AutomationDefinitionException>(() => AutomationCatalogPolicy.Validate(catalog with
            { Queues = [queue with { SuccessDependencies = [new(fixture.ScriptId, [fixture.OtherScriptId])] }] }));
    }

    [Fact(DisplayName = "Explicit Unlimited Execution Tracks Elapsed Time Without Adding An Artificial Deadline Or Stall Limit")]
    public void PreservesUnlimitedExecution()
    {
        var clock = new Clock(); var budget = new ExecutionTimeBudget(clock, clock.GetTimestamp(), TimeSpan.Zero, DateTimeOffset.MaxValue);
        clock.Timestamp += TimeSpan.FromDays(30).Ticks;
        var snapshot = budget.Read();
        Assert.Equal((long)TimeSpan.FromDays(30).TotalMilliseconds, snapshot.ConsumedMilliseconds);
        Assert.False(snapshot.IsExhausted); Assert.Equal(TimeSpan.FromSeconds(5), budget.Limit(TimeSpan.FromSeconds(5)));
        Assert.Equal(StepStopCause.Continue, StepStopPolicy.Evaluate(new(3, 0, 0),
            TimeSpan.FromDays(30), TimeSpan.FromDays(30), null, null, true));
        Assert.Equal(StepStopCause.BusinessFailed, StepStopPolicy.Evaluate(new(3, 0, 0),
            TimeSpan.FromDays(30), TimeSpan.FromDays(30), BusinessOutcome.Failed, TimeSpan.Zero, true));
    }

    private sealed class Clock : TimeProvider
    {
        internal long Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-22T00:00:00Z");
    }
}

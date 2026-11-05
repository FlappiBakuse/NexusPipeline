using System.Collections.Immutable;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class AdapterTaskTests
{
    [Fact(DisplayName = "Execution Occurrence Aliases Are Preserved And Cannot Collapse Distinct Business Tasks")]
    public void BindsExecutionOccurrences()
    {
        var mapped = Original.SetItem(0, Original[0] with { ExecutionTaskId = "native-occurrence" });
        Assert.Equal("native-occurrence", AdapterTaskPolicy.Freeze(mapped)[0].ExecutionTaskId);
        foreach (var invalid in new[] { "", "line\nbreak", new string('x', 121), "unsafe" })
            Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.Freeze(mapped.SetItem(0, mapped[0] with { ExecutionTaskId = invalid })));
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.Freeze(mapped.SetItem(1, mapped[1] with { ExecutionTaskId = "native-occurrence" })));
    }

    private static readonly ImmutableArray<AdapterTask> Original =
    [new("mail", "Mail", true, true, [], true), new("unsafe", "Purchase", true, true, ["mail"], false),
        new("disabled", "Disabled", false, false, [], true)];

    [Fact(DisplayName = "Only Original Selected Enabled Safe Tasks With An Actual Failure Can Be Proposed")]
    public void ProposesSafeRetries()
    {
        ImmutableArray<AdapterTaskObservation> observations =
        [new("mail", AdapterObservationState.Failed, "explicit"), new("unsafe", AdapterObservationState.Failed, "explicit")];
        AdapterTaskPolicy.RequireOriginalResults(Original, observations, ["mail"]);
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.RequireOriginalResults(Original, observations, ["unsafe"]));
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.RequireOriginalResults(Original, observations, ["disabled"]));
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.RequireOriginalResults(Original, observations.SetItem(0,
            observations[0] with { State = AdapterObservationState.Unknown }), ["mail"]));
    }

    [Fact(DisplayName = "Observation Facts Cannot Add Omit Duplicate Or Invent States And Reason Identity")]
    public void RejectsForgedObservations()
    {
        ImmutableArray<AdapterTaskObservation> observations =
        [new("mail", AdapterObservationState.Failed, "explicit"), new("unsafe", AdapterObservationState.Unknown, "unproven")];
        foreach (var changed in new[] { observations.RemoveAt(1), observations.Add(observations[0]),
            observations.SetItem(1, new("other", AdapterObservationState.Unknown, "unproven")),
            observations.SetItem(0, observations[0] with { State = (AdapterObservationState)999 }),
            observations.SetItem(0, observations[0] with { ReasonCode = "line\nbreak" }) })
            Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.RequireOriginalResults(Original, changed, []));
    }

    [Theory(DisplayName = "A Safe Flag Cannot Turn Technical Or Cleanup Work Into A Business Retry Target")]
    [InlineData(AdapterWorkflowRole.Business, true)]
    [InlineData(AdapterWorkflowRole.Technical, false)]
    [InlineData(AdapterWorkflowRole.Cleanup, false)]
    public void RequiresBusinessRetry(AdapterWorkflowRole role, bool allowed)
    {
        ImmutableArray<AdapterTask> tasks = [new("target", "Target", true, true, [], true, role)];
        ImmutableArray<AdapterTaskObservation> observations = [new("target", AdapterObservationState.Failed, "explicit")];
        AdapterTaskPolicy.RequireOriginalResults(tasks, observations, []);
        if (allowed) AdapterTaskPolicy.RequireOriginalResults(tasks, observations, ["target"]);
        else Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.RequireOriginalResults(tasks, observations, ["target"]));
    }

    [Fact(DisplayName = "Frozen Task Selection Requires The Original Ordered Enabled Dependency Closure")]
    public void RequiresDependencyClosure()
    {
        Assert.Equal(Original.ToArray(), AdapterTaskPolicy.Freeze(Original).ToArray());
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.Freeze(Original.SetItem(0, Original[0] with { IsSelected = false })));
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.Freeze(Original.SetItem(0, Original[0] with { Dependencies = ["unsafe"] })));
        Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.Freeze(Original.Add(Original[0])));
    }

    [Theory(DisplayName = "Business Outcome Uses Only Selected Business Tasks And Preserves Explicit Failures")]
    [InlineData(AdapterObservationState.Succeeded, BusinessOutcome.Succeeded)]
    [InlineData(AdapterObservationState.Partial, BusinessOutcome.Partial)]
    [InlineData(AdapterObservationState.Failed, BusinessOutcome.Failed)]
    [InlineData(AdapterObservationState.Unknown, BusinessOutcome.Unverified)]
    public void CountsBusinessTasks(AdapterObservationState state, BusinessOutcome expected)
    {
        ImmutableArray<AdapterTask> tasks = [new("prepare", "Prepare", true, true, [], false, AdapterWorkflowRole.Technical),
            new("mail", "Mail", true, true, ["prepare"], true), new("cleanup", "Cleanup", true, true, [], false, AdapterWorkflowRole.Cleanup)];
        ImmutableArray<AdapterTaskObservation> observations = [new("prepare", AdapterObservationState.Unknown, "no_business_fact"),
            new("mail", state, "actual_business_fact"), new("cleanup", AdapterObservationState.Unknown, "no_business_fact")];
        Assert.Equal(expected, AdapterBusinessPolicy.Decide(tasks, observations).Outcome);
        Assert.Equal(BusinessOutcome.Unverified, AdapterBusinessPolicy.Decide(tasks.RemoveAt(1), observations.RemoveAt(1)).Outcome);
    }

    [Theory(DisplayName = "Normal Completion Metadata Must Agree With The Frozen Observation State")]
    [InlineData(AdapterCompletion.Satisfied, AdapterObservationState.Succeeded, true)]
    [InlineData(AdapterCompletion.Inapplicable, AdapterObservationState.Succeeded, true)]
    [InlineData(AdapterCompletion.NotExecuted, AdapterObservationState.Unknown, true)]
    [InlineData(AdapterCompletion.Satisfied, AdapterObservationState.Failed, false)]
    [InlineData(AdapterCompletion.NotExecuted, AdapterObservationState.Succeeded, false)]
    public void ValidatesCompletionKinds(AdapterCompletion completion, AdapterObservationState state, bool valid)
    {
        ImmutableArray<AdapterTaskObservation> observations = [new("mail", state, "observed", completion),
            new("unsafe", AdapterObservationState.Unknown, "unproven")];
        if (valid) AdapterTaskPolicy.RequireOriginalResults(Original, observations, []);
        else Assert.Throws<ArgumentException>(() => AdapterTaskPolicy.RequireOriginalResults(Original, observations, []));
    }
}

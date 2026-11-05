using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class TargetCommandTests
{
    [Fact(DisplayName = "Two Explicit Offline Confirmations Preserve Both Native Failures And Successful Cleanup")]
    public void ConfirmsOfflineCleanup()
    {
        var primary = KnownPrimary();
        var commands = new[] { Probe(0), Probe(1) };
        var result = TargetFactsPolicy.Compose(primary, Ready(), commands, Offline(commands));
        Assert.Equal(primary, result.Primary); Assert.Equal(primary, result.Combined);
        Assert.Equal(commands, result.Commands.ToArray());
        Assert.All(result.Commands, command => Assert.Equal(EngineOutcome.Failed, command.Engine));
        Assert.Equal(commands.Select(command => command.CommandId), result.Cleanup.ConfirmationCommands);
    }

    [Theory(DisplayName = "Read Only Kind Alone Cannot Hide An Unconfirmed Native Failure Or Unknown")]
    [InlineData(EngineOutcome.Failed)]
    [InlineData(EngineOutcome.Unknown)]
    public void PreservesNativeFailures(EngineOutcome actual)
    {
        var command = Command(0, actual) with { Kind = TargetCommandKind.ReadOnlyProbe };
        var result = TargetFactsPolicy.Compose(KnownPrimary(), Ready(), [command],
            new(TargetCleanupOutcome.Completed, "execution_cleanup_observed"));
        Assert.Equal(actual, result.Combined.Engine); Assert.Equal(command.ReasonCode, result.Combined.ReasonCode);
        Assert.Equal(KnownPrimary().Business, result.Combined.Business);
        Assert.Equal(command, Assert.Single(result.Commands));
    }

    [Fact(DisplayName = "A Failed Effect Cannot Be Hidden By Controlled Ready Or Successful Cleanup")]
    public void RejectsHiddenEffects()
    {
        var failed = Command(0, EngineOutcome.Failed);
        var result = TargetFactsPolicy.Compose(KnownPrimary(), Ready(), [failed],
            new(TargetCleanupOutcome.Completed, "execution_cleanup_observed"));
        Assert.Equal(EngineOutcome.Failed, result.Combined.Engine); Assert.Equal(failed.ReasonCode, result.Combined.ReasonCode);
        Assert.Equal(KnownPrimary(), result.Primary); Assert.Equal(BusinessOutcome.Succeeded, result.Combined.Business);
    }

    [Theory(DisplayName = "Successful Device Cleanup Requires Two Consecutive Exact Offline Confirmations")]
    [InlineData("single")]
    [InlineData("intervening")]
    [InlineData("notOffline")]
    public void RequiresConsecutiveOffline(string change)
    {
        var first = Probe(0); var last = Probe(change == "intervening" ? 2 : 1);
        TargetCommandFact[] commands = change == "single" ? [first] :
            change == "intervening" ? [first, Command(1, EngineOutcome.Succeeded), last] :
            [first, last with { Observation = TargetCommandObservation.Complete }];
        var cleanup = new TargetCleanupFact(TargetCleanupOutcome.Completed, "execution_android_shutdown_verified")
        {
            Verification = TargetCleanupVerification.DeviceOffline,
            ConfirmationCommands = change == "single" ? [first.CommandId] : [first.CommandId, last.CommandId]
        };
        Assert.Throws<ArgumentException>(() => TargetFactsPolicy.Compose(KnownPrimary(), Ready(), commands, cleanup));
    }

    [Theory(DisplayName = "Timeout Truncated Bytes Or Contradictory Foreground Cannot Be Hidden By Semantic Success")]
    [InlineData(TargetCommandObservation.Unverified)]
    [InlineData(TargetCommandObservation.Incomplete)]
    [InlineData(TargetCommandObservation.Contradictory)]
    public void RejectsIncompleteCommands(TargetCommandObservation observation)
    {
        var actual = Command(0, EngineOutcome.Succeeded) with
        { Kind = TargetCommandKind.ReadOnlyProbe, Observation = observation };
        var result = TargetFactsPolicy.Compose(KnownPrimary(), Ready(), [actual],
            new(TargetCleanupOutcome.Completed, "execution_cleanup_observed"));
        Assert.Equal(EngineOutcome.Unknown, result.Combined.Engine); Assert.Equal(actual.ReasonCode, result.Combined.ReasonCode);
        Assert.Equal(KnownPrimary().Business, result.Combined.Business); Assert.Equal(actual, Assert.Single(result.Commands));
    }

    [Theory(DisplayName = "Target Commands Must Be The Exact Ordered Distinct Observed Sequence")]
    [InlineData("duplicate")]
    [InlineData("gap")]
    public void RequiresOrderedCommands(string change)
    {
        var first = Command(0, EngineOutcome.Succeeded);
        var second = Command(change == "gap" ? 2 : 1, EngineOutcome.Failed) with
        { CommandId = change == "duplicate" ? first.CommandId : Guid.NewGuid() };
        Assert.Throws<ArgumentException>(() => TargetFactsPolicy.Compose(KnownPrimary(), Ready(), [first, second]));
    }

    [Theory(DisplayName = "Primary Reason Wins An Equal Dominant Engine Before Stage Cleanup And Commands")]
    [InlineData(EngineOutcome.Unknown, TargetExecutionOutcome.UnverifiedDispatch)]
    [InlineData(EngineOutcome.Failed, TargetExecutionOutcome.ControlledReady)]
    [InlineData(EngineOutcome.Succeeded, TargetExecutionOutcome.ControlledReady)]
    public void KeepsPrimaryReason(EngineOutcome engine, TargetExecutionOutcome outcome)
    {
        var primary = KnownPrimary() with { Engine = engine, ReasonCode = "execution_primary_known" };
        var cleanup = new TargetCleanupFact(engine == EngineOutcome.Unknown ? TargetCleanupOutcome.Unverified :
            engine == EngineOutcome.Failed ? TargetCleanupOutcome.Failed : TargetCleanupOutcome.Completed, "execution_cleanup_known");
        var result = TargetFactsPolicy.Compose(primary, new(outcome, "execution_target_known"), [Command(0, engine)], cleanup);
        Assert.Equal(engine, result.Combined.Engine); Assert.Equal(primary.ReasonCode, result.Combined.ReasonCode);
        Assert.Equal(primary.Business, result.Combined.Business);
    }

    [Theory]
    [InlineData("commandKind")]
    [InlineData("observation")]
    [InlineData("cleanup")]
    [InlineData("verification")]
    public void RejectsInvalidInput(string change)
    {
        var command = Command(0, EngineOutcome.Succeeded) with
        {
            Kind = change == "commandKind" ? (TargetCommandKind)999 : TargetCommandKind.ReadOnlyProbe,
            Observation = change == "observation" ? (TargetCommandObservation)999 : TargetCommandObservation.Complete
        };
        var cleanup = new TargetCleanupFact(change == "cleanup" ? (TargetCleanupOutcome)999 :
            TargetCleanupOutcome.Completed, "execution_cleanup_known")
        { Verification = change == "verification" ? (TargetCleanupVerification)999 : TargetCleanupVerification.None };
        Assert.Throws<ArgumentException>(() => TargetFactsPolicy.Compose(KnownPrimary(), Ready(), [command], cleanup));
    }

    [Theory(DisplayName = "Semantic Cleanup Failure Or Uncertainty Preserves Known Main Business")]
    [InlineData(TargetCleanupOutcome.Failed, EngineOutcome.Failed)]
    [InlineData(TargetCleanupOutcome.Unverified, EngineOutcome.Unknown)]
    public void PreservesKnownBusiness(TargetCleanupOutcome cleanup, EngineOutcome expected)
    {
        var result = TargetFactsPolicy.Compose(KnownPrimary(), Ready(), [Probe(0)], new(cleanup, "execution_cleanup_problem"));
        Assert.Equal(expected, result.Combined.Engine); Assert.Equal("execution_cleanup_problem", result.Combined.ReasonCode);
        Assert.Equal(KnownPrimary().Business, result.Combined.Business); Assert.Equal(EngineOutcome.Failed, result.Commands[0].Engine);
    }

    [Fact(DisplayName = "Frozen Raw Commands Keep Every Original Identifier Sequence And Native Outcome")]
    public void KeepsRawOutcomes()
    {
        var first = Probe(0); var second = Probe(1); var commands = new[] { first, second };
        var result = TargetFactsPolicy.Compose(KnownPrimary(), Ready(), commands, Offline(commands));
        commands[0] = Command(0, EngineOutcome.Unknown);
        Assert.Equal(new[] { first, second }, result.Commands.ToArray());
        Assert.Equal(ImmutableArray.Create(first.CommandId, second.CommandId).ToArray(), result.Cleanup.ConfirmationCommands.ToArray());
    }

    private static ExecutionStepFact KnownPrimary() =>
        new("binding-fixture", EngineOutcome.Succeeded, BusinessOutcome.Succeeded, "execution_primary_known");
    private static TargetExecutionFact Ready() =>
        new(TargetExecutionOutcome.ControlledReady, "execution_target_application_ready");
    private static TargetCommandFact Command(int sequence, EngineOutcome engine) =>
        new(Guid.NewGuid(), sequence, engine, "execution_target_command_observed")
        { Observation = TargetCommandObservation.Complete };
    private static TargetCommandFact Probe(int sequence) => Command(sequence, EngineOutcome.Failed) with
    { Kind = TargetCommandKind.ReadOnlyProbe, Observation = TargetCommandObservation.ExpectedOffline };
    private static TargetCleanupFact Offline(IReadOnlyList<TargetCommandFact> commands) =>
        new(TargetCleanupOutcome.Completed, "execution_android_shutdown_verified")
        { Verification = TargetCleanupVerification.DeviceOffline, ConfirmationCommands = commands.Select(command => command.CommandId).ToImmutableArray() };
}

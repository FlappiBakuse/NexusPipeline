using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class ProviderProtocolTests
{
    [Fact(DisplayName = "The First Bound Terminal Survives Cancellation And Cannot Be Replaced By A Late Terminal")]
    public void KeepsFirstTerminal()
    {
        var identity = Identity(); var owner = new ProviderProtocolState(identity);
        Assert.True(owner.Observe(new(identity, 1, ProviderEventKind.Started, null, null), 100));
        Assert.True(owner.Observe(new(identity, 2, ProviderEventKind.Terminal, EngineOutcome.Succeeded, "provider_finished"), 100));
        owner.CloseAdmission();
        Assert.False(owner.Observe(new(identity, 3, ProviderEventKind.Terminal, EngineOutcome.Failed, "late_failure"), 100));
        Assert.Equal(EngineOutcome.Succeeded, owner.Decision!.Engine);
    }

    [Fact(DisplayName = "Closing Admission Before The First Terminal Never Creates A New Terminal Fact")]
    public void ClosesTerminalAdmission()
    {
        var identity = Identity(); var owner = new ProviderProtocolState(identity);
        Assert.True(owner.Observe(new(identity, 1, ProviderEventKind.Started, null, null), 100));
        owner.CloseAdmission();
        Assert.False(owner.Observe(new(identity, 2, ProviderEventKind.Terminal, EngineOutcome.Succeeded, "late_success"), 100));
        Assert.Null(owner.Decision);
    }

    [Theory(DisplayName = "Another Attempt Entry Activation Or Nonce Cannot Advance The Bound Protocol")]
    [InlineData("attempt")]
    [InlineData("step")]
    [InlineData("nonce")]
    [InlineData("activation")]
    [InlineData("entry")]
    public void RejectsForeignBindings(string field)
    {
        var identity = Identity(); var owner = new ProviderProtocolState(identity);
        var other = field switch
        {
            "attempt" => identity with { AttemptId = Guid.NewGuid() }, "step" => identity with { StepId = "other-step" },
            "nonce" => identity with { Nonce = new string('b', 64) }, "activation" => identity with { ActivationId = Guid.NewGuid() },
            _ => identity with { EntryId = "other-entry" }
        };
        Assert.Throws<ArgumentException>(() => owner.Observe(new(other, 1, ProviderEventKind.Started, null, null), 100));
        Assert.True(owner.Observe(new(identity, 1, ProviderEventKind.Started, null, null), 100)); Assert.Null(owner.Decision);
    }

    [Theory(DisplayName = "Provider Engine Composition Preserves Every Independently Judged Business Fact")]
    [InlineData(EngineOutcome.Succeeded, EngineOutcome.Succeeded, EngineOutcome.Succeeded)]
    [InlineData(EngineOutcome.Failed, EngineOutcome.Succeeded, EngineOutcome.Failed)]
    [InlineData(EngineOutcome.Succeeded, EngineOutcome.Failed, EngineOutcome.Failed)]
    [InlineData(EngineOutcome.Unknown, EngineOutcome.Succeeded, EngineOutcome.Unknown)]
    public void PreservesJudgedBusiness(EngineOutcome native, EngineOutcome provider, EngineOutcome expected)
    {
        var original = new ExecutionStepFact("main", native, BusinessOutcome.Unverified, "native_fact");
        var combined = ProviderFactsPolicy.Compose(original, new(provider, "provider_fact", 2), true);
        Assert.Equal(expected, combined.Engine); Assert.Equal(original.Business, combined.Business);
        Assert.Equal(native, original.Engine);
        if (expected == native) Assert.Equal(original.ReasonCode, combined.ReasonCode);
    }

    [Fact(DisplayName = "Missing Terminal Is Unknown For Executed Workers And Never Synthesizes Success For An Empty Job")]
    public void KeepsMissingTerminal()
    {
        var actual = new ExecutionStepFact("main", EngineOutcome.Succeeded, BusinessOutcome.NotApplicable, "native_fact");
        Assert.Equal(EngineOutcome.Unknown, ProviderFactsPolicy.Compose(actual, null, true).Engine);
        var empty = new ExecutionStepFact("main", EngineOutcome.NotStarted, BusinessOutcome.Unverified, "not_started");
        Assert.Equal(empty, ProviderFactsPolicy.Compose(empty, null, false));
        Assert.Throws<ArgumentException>(() => ProviderFactsPolicy.Compose(empty, new(EngineOutcome.Succeeded, "false_success", 2), false));
    }

    [Fact(DisplayName = "Progress Cannot Use The Reserved Terminal Bytes Or Consume The Final Sequence Slot")]
    public void ReservesTerminalCapacity()
    {
        var identity = Identity(); var owner = new ProviderProtocolState(identity);
        var bytes = ProviderProtocolState.TerminalReserveBytes;
        Assert.True(owner.Observe(new(identity, 1, ProviderEventKind.Started, null, null), bytes));
        long sequence = 1;
        for (var used = bytes; used + bytes <= ProviderProtocolState.MaximumEncodedBytes - bytes; used += bytes)
            Assert.True(owner.Observe(new(identity, ++sequence, ProviderEventKind.Progress, null, null), bytes));
        Assert.Throws<InvalidOperationException>(() => owner.Observe(new(identity, sequence + 1, ProviderEventKind.Progress, null, null), bytes));
        Assert.True(owner.Observe(new(identity, sequence + 1, ProviderEventKind.Terminal, EngineOutcome.Failed, "native_failed"), bytes));
    }

    private static ExecutionProviderSession Identity() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), "main", "fixture-provider", "main", Guid.NewGuid(), new string('a', 64), new string('c', 64));
}

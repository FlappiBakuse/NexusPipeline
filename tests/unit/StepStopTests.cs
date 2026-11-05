using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class StepStopTests
{
    [Theory(DisplayName = "Success Keeps Its Full Natural Exit Grace Despite Silence")]
    [InlineData(59999, StepStopCause.Continue)]
    [InlineData(60000, StepStopCause.SuccessGraceExpired)]
    [InlineData(60001, StepStopCause.SuccessGraceExpired)]
    public void KeepsExitGrace(long milliseconds, StepStopCause expected)
        => Assert.Equal(expected, StepStopPolicy.Evaluate(new(1, 1, 300), TimeSpan.FromMilliseconds(milliseconds),
            TimeSpan.FromMilliseconds(milliseconds), BusinessOutcome.Succeeded, TimeSpan.FromMilliseconds(milliseconds), true));

    [Fact(DisplayName = "Failure Ends The Step Immediately Without Changing Engine Facts")]
    public void StopsBusinessFailure()
        => Assert.Equal(StepStopCause.BusinessFailed, StepStopPolicy.Evaluate(new(1, 30, 300), TimeSpan.FromSeconds(1),
            TimeSpan.Zero, BusinessOutcome.Failed, TimeSpan.Zero, true));

    [Theory(DisplayName = "Stall Requires The Frozen Threshold And A Complete Observation Scope")]
    [InlineData(999, true, StepStopCause.Continue)]
    [InlineData(1000, true, StepStopCause.StallTimeout)]
    [InlineData(1000, false, StepStopCause.Continue)]
    public void RequiresCompleteObservation(long idle, bool observed, StepStopCause expected)
        => Assert.Equal(expected, StepStopPolicy.Evaluate(new(1, 1, 30), TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(idle), null, null, observed));

    [Theory(DisplayName = "Step Total Is Independent Of Active Output")]
    [InlineData(2999, StepStopCause.Continue)]
    [InlineData(3000, StepStopCause.StepTotalTimeout)]
    public void EnforcesStepBudget(long elapsed, StepStopCause expected)
        => Assert.Equal(expected, StepStopPolicy.Evaluate(new(1, 30, 3), TimeSpan.FromMilliseconds(elapsed),
            TimeSpan.Zero, null, null, true));

    [Theory(DisplayName = "Invalid Monotonic Observations Are Rejected")]
    [InlineData(-1, 0)]
    [InlineData(1, 2)]
    public void RejectsInvalidClocks(long elapsed, long idle)
        => Assert.Throws<ArgumentException>(() => StepStopPolicy.Evaluate(new(1, 30, 300),
            TimeSpan.FromMilliseconds(elapsed), TimeSpan.FromMilliseconds(idle), null, null, true));
}

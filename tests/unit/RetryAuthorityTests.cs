using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Settings;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class RetryAuthorityTests
{
    private static readonly Guid Run = Guid.Parse("23c0dd6e-343b-4f21-95a6-29c4a68d7843");
    private static readonly DateTimeOffset Deadline = DateTimeOffset.Parse("2026-10-21T12:00:00Z");

    private static ProviderRetryDeclaration Declaration => new(RetrySafety.Safe, "workerAndUpstream",
        "stable-run-binding/1", [AttemptOutcome.Interrupted, AttemptOutcome.Failed]);
    private static ExecutionRetrySurface Worker(int count = 3) => new(Declaration, count, false, false, false, false, false);

    [Fact(DisplayName = "Original Host Approval Defaults To Disabled Even For An Explicit Whole Worker Declaration")]
    public void RequiresHostApproval()
    {
        var policy = RetryAuthorityPolicy.Capture(Run, [Worker()], HostSettings.Default.AutomaticRetry, Deadline);
        Assert.Equal(RetrySafety.Safe, policy.Safety); Assert.False(policy.HostApproved);
        Assert.Equal(3, policy.MaximumAttempts); Assert.Equal(TimeSpan.FromSeconds(30), policy.Backoff);
    }

    [Theory(DisplayName = "An Undeclared Execution Surface Cannot Borrow Whole Worker Safety From Another Surface")]
    [InlineData("generic")]
    [InlineData("hook")]
    [InlineData("target")]
    [InlineData("script-judgement")]
    [InlineData("configuration")]
    [InlineData("launcher")]
    public void SeparatesDeclaredSurfaces(string surface)
    {
        var worker = surface switch
        {
            "generic" => Worker() with { Provider = null },
            "hook" => Worker() with { HasHooks = true },
            "target" => Worker() with { HasTarget = true },
            "script-judgement" => Worker() with { HasScriptJudgement = true },
            "configuration" => Worker() with { Provider = null, HasManagedConfiguration = true },
            _ => Worker() with { HasRetainedLauncher = true }
        };
        var policy = RetryAuthorityPolicy.Capture(Run, [Worker(), worker], new(true, 0), Deadline);
        Assert.Equal(RetrySafety.Unknown, policy.Safety); Assert.True(policy.HostApproved);
        Assert.Equal(1, policy.MaximumAttempts);
    }

    [Fact(DisplayName = "Every Declared Worker Caps The Single Frozen Run Budget")]
    public void CapsFrozenBudget()
    {
        var policy = RetryAuthorityPolicy.Capture(Run, [Worker(5), Worker(2)], new(true, 3), Deadline);
        Assert.Equal(RetrySafety.Safe, policy.Safety); Assert.Equal(2, policy.MaximumAttempts);
        Assert.Equal(Deadline, policy.DeadlineUtc); Assert.Equal(Run.ToString("D"), policy.IdempotencyKey);
    }

    [Fact(DisplayName = "Declared Worker Configuration Still Requires Actual Restoration Before Any Retry")]
    public void RequiresRestoredConfiguration()
    {
        var policy = RetryAuthorityPolicy.Capture(Run, [Worker() with { HasManagedConfiguration = true }], new(true, 0), Deadline);
        Assert.Equal(RetrySafety.Safe, policy.Safety);
        var now = Deadline.AddMinutes(-1);
        var evidence = new RetryEvidence(AttemptOutcome.Failed, WriterState.Stopped, false, false, true, 1, now);
        Assert.Equal("recovery_not_confirmed", RetryDecision.Evaluate(policy, evidence, now).ReasonCode);
        Assert.True(RetryDecision.Evaluate(policy, evidence with { IsConfigurationRestored = true }, now).IsEligible);
    }

    [Theory(DisplayName = "Malformed Authority Cannot Be Frozen As Safe")]
    [InlineData("scope")]
    [InlineData("protocol")]
    [InlineData("safety")]
    [InlineData("duplicate-outcome")]
    [InlineData("missing-outcome")]
    public void RejectsMalformedAuthority(string damage)
    {
        var declaration = damage switch
        {
            "scope" => Declaration with { Scope = "task" },
            "protocol" => Declaration with { IdempotencyProtocol = "caller-defined" },
            "safety" => Declaration with { Safety = RetrySafety.Unknown },
            "duplicate-outcome" => Declaration with { Outcomes = [AttemptOutcome.Failed, AttemptOutcome.Failed] },
            _ => Declaration with { Outcomes = [AttemptOutcome.Failed] }
        };
        Assert.Throws<ArgumentException>(() => RetryAuthorityPolicy.Capture(Run, [Worker() with { Provider = declaration }], new(true, 0), Deadline));
    }

    [Fact(DisplayName = "Stable Business Key Uses The Original Run And Step While Attempt And Nonce Are Independent")]
    public void PreservesBusinessKeys()
    {
        var key = RetryAuthorityPolicy.BindingKey(Run, "binding-bd40943e-ae76-4598-bd13-3d585cf53845");
        Assert.Equal(key, RetryAuthorityPolicy.BindingKey(Run, "binding-bd40943e-ae76-4598-bd13-3d585cf53845"));
        Assert.NotEqual(key, RetryAuthorityPolicy.BindingKey(Guid.NewGuid(), "binding-bd40943e-ae76-4598-bd13-3d585cf53845"));
        Assert.NotEqual(key, RetryAuthorityPolicy.BindingKey(Run, "binding-e980869d-e22f-4a39-bada-7f3517a5cd91"));
        Assert.Equal(64, key.Length);
    }

    [Theory(DisplayName = "Invalid Host Backoff Is Rejected By The Existing Settings Owner")]
    [InlineData(-1)]
    [InlineData(86401)]
    public void RejectsInvalidBackoff(int seconds)
    {
        var value = HostSettings.Default with { AutomaticRetry = new(true, seconds) };
        Assert.Equal("settings_retry_backoff_invalid", Assert.Throws<HostSettingsException>(() => HostSettingsPolicy.Validate(value)).Message);
    }
}

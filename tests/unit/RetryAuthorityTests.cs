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

public sealed class RetryHookTests
{
    private static readonly Guid Run = Guid.Parse("95de4b59-0a27-4e71-9586-8b3747e34a81");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
    private static ExecutionRetrySurface Worker => new(new(RetrySafety.Safe, "workerAndUpstream", "stable-run-binding/1",
        [AttemptOutcome.Failed, AttemptOutcome.Interrupted]), 3, true, false, false, true, false)
    { Hooks = [new(ExecutionHookRole.Pre, true, false), new(ExecutionHookRole.Post, false, true)] };
    private static FrozenRetryPolicy Policy => RetryAuthorityPolicy.Capture(Run, [Worker], new(true, 0), Now.AddMinutes(5));

    [Fact(DisplayName = "A Single Declared Binding Has An Explicit Once Pre Final Post Authority Scope")]
    public void BindsHookScope()
    {
        var description = RetryHookPolicy.Describe([Worker]);
        Assert.Equal(RetrySafety.Safe, description.Safety); Assert.Equal("once-pre-final-post", description.Scope);
        Assert.Equal("retry_authority_verified", description.ReasonCode); Assert.Equal(3, Policy.MaximumAttempts);
    }

    [Theory(DisplayName = "Undeclared Repeatable Hook Effects Never Borrow Worker Safety")]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RequiresHookSafety(bool once, bool final)
    {
        var worker = Worker with { Hooks = [new(ExecutionHookRole.Pre, once, false), new(ExecutionHookRole.Post, false, final)] };
        var result = RetryHookPolicy.Describe([worker]);
        Assert.Equal(RetrySafety.Unknown, result.Safety); Assert.Equal("retry_hook_effects_unverified", result.ReasonCode);
    }

    [Fact(DisplayName = "Multi Binding Final Post Does Not Guess The Future Whole Attempt Decision")]
    public void RejectsAmbiguousDeferral()
    {
        var result = RetryHookPolicy.Describe([Worker, Worker]);
        Assert.Equal(RetrySafety.Unknown, result.Safety); Assert.Equal("retry_hook_finality_unverified", result.ReasonCode);
        Assert.Equal(1, RetryAuthorityPolicy.Capture(Run, [Worker, Worker], new(true, 0), Now.AddMinutes(5)).MaximumAttempts);
    }

    [Fact(DisplayName = "Post Deferral Is Conditional And Does Not Claim Configuration Has Already Been Restored")]
    public void PreservesConditionalDeferral()
    {
        var actual = new RetryEvidence(AttemptOutcome.Failed, WriterState.Stopped, false, false, true, 1, Now);
        var budget = new ExecutionBudgetSnapshot(5000, TimeSpan.FromMinutes(1), null);
        Assert.Equal("recovery_not_confirmed", RetryDecision.Evaluate(Policy, actual, budget, Now).ReasonCode);
        var decision = RetryDecision.EvaluatePostDeferral(Policy, actual, budget, Now);
        Assert.True(decision.ShouldDefer); Assert.True(decision.RequiresConfigurationRestoration);
        Assert.Equal("retry_post_pending_restoration", decision.ReasonCode);
        Assert.True(RetryDecision.Evaluate(Policy, actual with { IsConfigurationRestored = true }, budget, Now).IsEligible);
    }

    [Theory(DisplayName = "A Post Cannot Be Deferred When Another Actual Gate Has Already Rejected Retry")]
    [InlineData("cancelled")]
    [InlineData("partial")]
    [InlineData("writers")]
    [InlineData("budget")]
    [InlineData("approval")]
    [InlineData("attempt-limit")]
    public void HonorsRejectedRetry(string cause)
    {
        var policy = Policy;
        var evidence = new RetryEvidence(AttemptOutcome.Failed, WriterState.Stopped, false, false, true, 1, Now);
        var budget = new ExecutionBudgetSnapshot(5000, TimeSpan.FromMinutes(1), null);
        switch (cause)
        {
            case "cancelled": evidence = evidence with { PreviousOutcome = AttemptOutcome.Cancelled }; break;
            case "partial": evidence = evidence with { PreviousOutcome = AttemptOutcome.Partial }; break;
            case "writers": evidence = evidence with { Writers = WriterState.Unknown }; break;
            case "budget": budget = budget with { Remaining = TimeSpan.Zero }; break;
            case "approval": policy = policy with { HostApproved = false }; break;
            default: evidence = evidence with { CompletedAttempts = 3 }; break;
        }
        Assert.False(RetryDecision.EvaluatePostDeferral(policy, evidence, budget, Now).ShouldDefer);
    }

    [Theory(DisplayName = "Pre Safety Distinguishes Unstarted Jobs From Failed Or Unknown Effects")]
    [InlineData(EngineOutcome.Failed, false)]
    [InlineData(EngineOutcome.Unknown, false)]
    [InlineData(EngineOutcome.NotStarted, true)]
    [InlineData(EngineOutcome.Succeeded, true)]
    public void DistinguishesUnstartedHooks(EngineOutcome pre, bool expected)
    {
        Assert.Equal(expected, RetryHookPolicy.CanRetry(Worker.Hooks,
            [new(ExecutionHookRole.Pre, RetryHookDisposition.Collected, pre),
                new(ExecutionHookRole.Post, RetryHookDisposition.Deferred, EngineOutcome.NotStarted)]));
    }

    [Theory(DisplayName = "An Executed Post Never Authorizes Repeating The Worker")]
    [InlineData(EngineOutcome.Failed)]
    [InlineData(EngineOutcome.Succeeded)]
    [InlineData(EngineOutcome.Unknown)]
    public void RejectsExecutedPost(EngineOutcome post)
    {
        Assert.False(RetryHookPolicy.CanRetry(Worker.Hooks,
            [new(ExecutionHookRole.Pre, RetryHookDisposition.InheritedSuccess, EngineOutcome.Succeeded),
                new(ExecutionHookRole.Post, RetryHookDisposition.Collected, post)]));
    }
}

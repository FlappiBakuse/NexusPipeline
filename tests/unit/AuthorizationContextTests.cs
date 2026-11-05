using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class AuthorizationContextTests
{
    [Fact(DisplayName = "Authorization Binds The Exact Saved Source Subject Without Exposing Mutable Group Storage")]
    public void BindsSavedSubjects()
    {
        var intent = Intent(); var sources = new[] { new CompletionAuthorizationSource(intent, "local:original") };
        var context = CompletionAuthorizationContext.Create(CompletionActionPolicy.Combine([intent]), sources);
        sources[0] = sources[0] with { SubjectId = "remote:other" };
        Assert.Equal("local:original", context.Sources[0].SubjectId);
        var changed = CompletionAuthorizationContext.Create(context.Frozen, [sources[0]]);
        Assert.False(context.Matches(changed));
    }

    [Theory(DisplayName = "A Grant For Another Frozen Queue Revision Action Or Receipt Is Not The Same Context")]
    [InlineData("queue")]
    [InlineData("revision")]
    [InlineData("action")]
    [InlineData("receipt")]
    public void RejectsChangedContexts(string field)
    {
        var intent = Intent();
        var other = field switch
        {
            "queue" => intent with { QueueId = Guid.NewGuid() },
            "revision" => intent with { QueueRevision = 2 },
            "action" => intent with { Action = AutomationCompletionAction.Restart },
            _ => intent with { SourceReceiptDigest = new('c', 64) }
        };
        var a = CompletionAuthorizationContext.Create(CompletionActionPolicy.Combine([intent]), [new(intent, "local:user")]);
        var b = CompletionAuthorizationContext.Create(CompletionActionPolicy.Combine([other]), [new(other, "local:user")]);
        Assert.False(a.Matches(b));
        Assert.Throws<ArgumentException>(() => CompletionAuthorizationContext.Create(a.Frozen, b.Sources));
    }

    [Fact(DisplayName = "Every Member Of An Aggregated Completion Group Retains Its Own Actual Subject")]
    public void PreservesGroupSubjects()
    {
        var first = Intent(); var second = Intent(); var group = CompletionActionPolicy.Combine([second, first]);
        var context = CompletionAuthorizationContext.Create(group, [new(first, "local:user"), new(second, "host:scheduler")]);
        Assert.Equal(2, context.Sources.Length);
        Assert.Contains(context.Sources, item => item.Intent == second && item.SubjectId == "host:scheduler");
        Assert.Throws<ArgumentException>(() => CompletionAuthorizationContext.Create(group, [new(first, "local:user")]));
    }

    private static FrozenCompletionIntent Intent() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
        AutomationCompletionAction.Shutdown, new('a', 64), new('b', 64));
}

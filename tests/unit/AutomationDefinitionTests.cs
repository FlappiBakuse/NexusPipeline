using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class AutomationDefinitionTests
{
    [Theory(DisplayName = "A Stable Script Id Cannot Change Its Kind Or Borrow Another Extensions Configuration Identity")]
    [InlineData("generic-to-specialized")]
    [InlineData("specialized-to-generic")]
    [InlineData("another-extension")]
    public void PreservesScriptIdentity(string change)
    {
        var current = Create();
        var original = current.Scripts[0];
        if (change != "generic-to-specialized")
        {
            original = original with { Kind = AutomationKind.Specialized, ExtensionId = "first-extension" };
            current = current with { Scripts = [original] };
        }
        var changed = change == "specialized-to-generic" ? original with { Kind = AutomationKind.Generic, ExtensionId = null }
            : original with { Kind = AutomationKind.Specialized, ExtensionId = "another-extension" };
        var edits = Empty(current.Revision) with { Scripts = [changed] };
        var error = Assert.Throws<AutomationDefinitionException>(() => AutomationEdits.Apply(current, edits));
        Assert.Equal("automation_script_identity_changed", error.Message);
    }

    [Fact(DisplayName = "A Stable Binding Id Cannot Be Reassigned To Another Users Configuration Identity")]
    public void PreservesBindingIdentity()
    {
        var current = Create();
        var binding = current.Bindings[0];
        var edits = Empty(current.Revision) with { Bindings = [binding with { UserId = Guid.NewGuid() }] };
        Assert.Equal("automation_binding_identity_changed", Assert.Throws<AutomationDefinitionException>(() => AutomationEdits.Apply(current, edits)).Message);
    }

    [Fact]
    public void PreservesQueueGroups()
    {
        var current = Create();
        var deleted = current.Users[0];
        var owned = current.Bindings.Single(value => value.UserId == deleted.Id);
        var queue = current.Queues.Single();
        var edits = Empty(current.Revision) with { Removed = [new(AutomationDefinitionKind.User, deleted.Id, deleted.Revision)] };
        var next = AutomationEdits.Apply(current, edits);
        Assert.DoesNotContain(next.Users, value => value.Id == deleted.Id);
        Assert.DoesNotContain(next.Bindings, value => value.Id == owned.Id);
        Assert.Equal(queue.ScriptIds, next.Queues.Single().ScriptIds);
        Assert.Equal(queue.Revision, next.Queues.Single().Revision);
        Assert.Equal(current.Scripts.ToArray(), next.Scripts.ToArray());
    }

    [Fact]
    public void ReordersScriptGroups()
    {
        var current = Create();
        var extra = current.Scripts[0] with { Id = Guid.NewGuid(), Revision = 0, Name = "Second" };
        current = AutomationEdits.Apply(current, Empty(current.Revision) with { Scripts = [extra],
            Queues = [current.Queues[0] with { ScriptIds = [current.Scripts[0].Id, extra.Id] }] });
        var queue = current.Queues.Single();
        var order = queue.ScriptIds.Reverse().ToImmutableArray();
        var next = AutomationEdits.Apply(current, Empty(current.Revision) with { Queues = [queue with { ScriptIds = order }] });
        Assert.Equal(order, next.Queues.Single().ScriptIds);
        Assert.Equal(queue.Revision + 1, next.Queues.Single().Revision);
        Assert.Equal(current.Bindings.ToArray(), next.Bindings.ToArray());
        Assert.Equal(current.Revision + 1, next.Revision);
    }

    [Theory(DisplayName = "Stale Catalog Or Entity Revisions Cannot Overwrite A Committed Definition")]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsStaleRevisions(bool catalogStale)
    {
        var current = Create();
        var user = current.Users[0];
        var edits = Empty(catalogStale ? current.Revision - 1 : current.Revision) with
        {
            Users = [user with { Name = "Conflicting name", Revision = catalogStale ? user.Revision : user.Revision - 1 }]
        };
        Assert.Throws<AutomationConflictException>(() => AutomationEdits.Apply(current, edits));
    }

    [Fact]
    public void RejectsUnknownGroups()
    {
        var current = Create();
        var queue = current.Queues.Single();
        var edits = Empty(current.Revision) with { Queues = [queue with { ScriptIds = queue.ScriptIds.Add(Guid.NewGuid()) }] };
        Assert.Equal("automation_queue_invalid", Assert.Throws<AutomationDefinitionException>(() => AutomationEdits.Apply(current, edits)).Message);
    }

    [Fact(DisplayName = "Distinct Binding Ids Cannot Create A Second Binding For The Same User And Script")]
    public void RejectsDuplicateBindings()
    {
        var current = Create();
        var duplicate = current.Bindings[0] with { Id = Guid.NewGuid(), Revision = 0 };
        var edits = Empty(current.Revision) with { Bindings = [duplicate] };
        Assert.Equal("automation_binding_invalid", Assert.Throws<AutomationDefinitionException>(() => AutomationEdits.Apply(current, edits)).Message);
    }

    [Theory(DisplayName = "Portable Path Traversal And Unbound Components Cannot Become Definitions")]
    [InlineData("../outside")]
    [InlineData("a//b")]
    [InlineData("C:\\outside")]
    [InlineData("/absolute")]
    [InlineData("a/./b")]
    [InlineData("a\0b")]
    public void RejectsUnboundPaths(string path)
    {
        var current = Create();
        var script = current.Scripts.Single();
        var edits = Empty(current.Revision) with { Scripts = [script with { Executable = script.Executable with { RelativePath = path } }] };
        Assert.Equal("automation_portable_path_invalid", Assert.Throws<AutomationDefinitionException>(() => AutomationEdits.Apply(current, edits)).Message);
    }

    [Fact(DisplayName = "Saving A Binding For A User Deleted In The Same Command Is Rejected As A Contradictory Edit")]
    public void RejectsContradictoryEdits()
    {
        var current = Create();
        var user = current.Users[0];
        var binding = current.Bindings.Single(value => value.UserId == user.Id);
        var edits = Empty(current.Revision) with
        {
            Bindings = [binding with { IsEnabled = false }], Removed = [new(AutomationDefinitionKind.User, user.Id, user.Revision)]
        };
        Assert.Equal("automation_deletion_reference_conflict", Assert.Throws<AutomationDefinitionException>(() => AutomationEdits.Apply(current, edits)).Message);
    }

    internal static AutomationChanges Empty(long revision) => new(revision, [], [], [], [], [], [], []);

    internal static AutomationCatalog Create()
    {
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Fixture", AutomationRootKind.Script, @"D:\Fixture");
        var script = new AutomationScript(Guid.NewGuid(), 0, "Script", AutomationKind.Generic, root.Id, null, new(root.Id, "run.exe"),
            [], [], [], new(2, 30, 120), new(AutomationJudgementLanguage.Keywords, ["done"], ["error"], null));
        var users = Enumerable.Range(0, 3).Select(index => new AutomationUser(Guid.NewGuid(), 0, "User " + index, index)).ToImmutableArray();
        var bindings = users.Select((user, index) => new AutomationBinding(Guid.NewGuid(), 0, user.Id, script.Id, true, index)).ToImmutableArray();
        var queue = new AutomationQueue(Guid.NewGuid(), 0, "Queue", [script.Id], AutomationCompletionAction.None);
        return AutomationEdits.Apply(AutomationCatalog.Empty, Empty(0) with { Roots = [root], Scripts = [script], Users = users,
            Bindings = bindings, Queues = [queue] });
    }
}

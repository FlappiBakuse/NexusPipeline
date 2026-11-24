using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using Xunit;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.CoreTests;

public sealed class AutomationDefinitionTests
{
    [Theory(DisplayName = "Every Binding Override Group Independently Selects Its Saved Values Including Empty Hooks Lists And Limit Boundaries")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ResolvesOverrideGroups(int groups)
    {
        var fixture = new ExecutionSelectionFixture();
        var hook = new AutomationHook(new(fixture.Catalog.Roots[0].Id, "before.exe"), ["raw"]);
        var tasks = ImmutableArray.Create(Guid.NewGuid(), Guid.NewGuid());
        var raw = fixture.Catalog.Bindings[0] with { IsEnabled = true, RunDays = 7, MaximumSuccessfulRunsPerDay = 3,
            NotifyEnabled = true, NotificationTaskIds = tasks, PreHook = hook, PostHook = hook,
            PreOnceAfterSuccess = true, PostFinalOnly = true };
        var general = (groups & 1) != 0; var notification = (groups & 2) != 0; var advanced = (groups & 4) != 0;
        foreach (var (days, limit) in new[] { (-1, -1), (0, 1), (36500, 9999) })
        {
            var user = fixture.Catalog.Users.Single(item => item.Id == raw.UserId) with { BindingOverrides = new()
            {
                General = new(general, false, days, limit), Notification = new(notification, false, []),
                Advanced = new(advanced, null, null, false, false)
            } };
            var actual = AutomationBindingPolicy.Resolve(user, raw);
            Assert.Equal(!general, actual.IsEnabled);
            Assert.Equal(general ? days : 7, actual.RunDays);
            Assert.Equal(general ? limit : 3, actual.MaximumSuccessfulRunsPerDay);
            Assert.Equal(!notification, actual.NotifyEnabled);
            Assert.Equal(notification ? [] : tasks.ToArray(), actual.NotificationTaskIds.ToArray());
            Assert.Equal(advanced ? null : hook, actual.PreHook); Assert.Equal(advanced ? null : hook, actual.PostHook);
            Assert.Equal(!advanced, actual.PreOnceAfterSuccess); Assert.Equal(!advanced, actual.PostFinalOnly);
            Assert.Equal(raw.Id, actual.Id); Assert.Equal(raw.Revision, actual.Revision);
            Assert.Equal(raw.UserId, actual.UserId); Assert.Equal(raw.ScriptId, actual.ScriptId); Assert.Equal(raw.Priority, actual.Priority);
        }
    }

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

public sealed class AutomationHookTests
{
    [Fact(DisplayName = "Binding Constructor Keeps Hooks Absent And Explicit Hook Flags Round Trip Through Core Edits")]
    public void PreservesHookFlags()
    {
        var catalog = Catalog();
        var binding = Assert.Single(catalog.Bindings);
        Assert.Null(binding.PreHook); Assert.Null(binding.PostHook); Assert.False(binding.PreOnceAfterSuccess); Assert.False(binding.PostFinalOnly);
        var hook = new AutomationHook(new(Assert.Single(catalog.Roots).Id, "before.cmd"), ["one", "two words"]);
        var changed = binding with { PreHook = hook, PostHook = hook with { Executable = hook.Executable with { RelativePath = "after.ps1" } },
            PreOnceAfterSuccess = true, PostFinalOnly = true };
        var next = AutomationEdits.Apply(catalog, new(catalog.Revision, [], [], [], [changed], [], [], []));
        var saved = Assert.Single(next.Bindings);
        Assert.Equal(2, saved.Revision); Assert.True(saved.PreOnceAfterSuccess); Assert.True(saved.PostFinalOnly);
        Assert.Equal(hook.Arguments.ToArray(), saved.PreHook!.Arguments.ToArray()); Assert.Equal("after.ps1", saved.PostHook!.Executable.RelativePath);
    }

    [Theory(DisplayName = "Hook Policy Rejects Missing Roots Escapes Unsupported Extensions And Default Arguments")]
    [InlineData("missingRoot")]
    [InlineData("escape")]
    [InlineData("python")]
    [InlineData("defaultArguments")]
    public void RejectsInvalidHooks(string damage)
    {
        var catalog = Catalog();
        var hook = new AutomationHook(new(Assert.Single(catalog.Roots).Id, "before.exe"), []);
        hook = damage switch
        {
            "missingRoot" => hook with { Executable = hook.Executable with { RootId = Guid.NewGuid() } },
            "escape" => hook with { Executable = hook.Executable with { RelativePath = "../before.exe" } },
            "python" => hook with { Executable = hook.Executable with { RelativePath = "before.py" } },
            "defaultArguments" => hook with { Arguments = default },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<AutomationDefinitionException>(() => AutomationCatalogPolicy.Validate(catalog with
            { Bindings = [Assert.Single(catalog.Bindings) with { PreHook = hook }] }));
    }

    [Theory(DisplayName = "Once And Final Only Require Their Own Hook")]
    [InlineData(true)]
    [InlineData(false)]
    public void RequiresSelectedHooks(bool pre)
    {
        var catalog = Catalog(); var binding = Assert.Single(catalog.Bindings);
        Assert.Throws<AutomationDefinitionException>(() => AutomationCatalogPolicy.Validate(catalog with
            { Bindings = [binding with { PreOnceAfterSuccess = pre, PostFinalOnly = !pre }] }));
    }

    private static AutomationCatalog Catalog()
    {
        var root = Guid.NewGuid(); var script = Guid.NewGuid(); var user = Guid.NewGuid();
        return new(1, [new(root, 1, "Root", AutomationRootKind.Script, "D:/core-hook-root")],
            [new(script, 1, "Script", AutomationKind.Generic, root, null, new(root, "main.exe"), [], [], [],
                new(1, 10, 120), new(AutomationJudgementLanguage.Keywords, [], [], null))],
            [new(user, 1, "User", 0)], [new(Guid.NewGuid(), 1, user, script, true, 0)], [], []);
    }
}

public sealed class AutomationTargetTests
{
    [Theory]
    [InlineData("custom-engine", true)]
    [InlineData("engine.variant-2", true)]
    [InlineData("../engine", false)]
    [InlineData("", false)]
    public void AcceptsProviderVendor(string vendor, bool valid)
    {
        var root = Guid.NewGuid();
        var target = new AutomationTarget(AutomationTargetKind.Android, true, 10, false, null,
            new(AutomationAndroidDriver.GenericAdb, new(root, "device.exe"), "127.0.0.1:5555", "test.game", [])
            { Emulator = new(vendor, root, new string('a', 64), new string('b', 64))
                { ExtensionId = "arbitrary-provider", ProviderId = "devices" } });
        var error = Record.Exception(() => AutomationTargetPolicy.Validate(target, new HashSet<Guid> { root }));
        if (valid) Assert.Null(error); else Assert.IsType<AutomationDefinitionException>(error);
    }
    [Theory(DisplayName = "Invalid Targets Cannot Enter The Definition Graph")]
    [InlineData("root")]
    [InlineData("escape")]
    [InlineData("wildcard")]
    [InlineData("ready-script")]
    [InlineData("missing-kind")]
    [InlineData("both-kinds")]
    [InlineData("wait")]
    [InlineData("arguments")]
    public void RejectsInvalidTargets(string damage)
    {
        var root = Guid.NewGuid();
        var target = Pc(root);
        target = damage switch
        {
            "root" => target with { Pc = target.Pc! with { ReadyExecutable = new(Guid.NewGuid(), "game.exe") } },
            "escape" => target with { Pc = target.Pc! with { LaunchExecutable = new(root, "../launch.exe") } },
            "wildcard" => target with { Pc = target.Pc! with { ReadyExecutable = new(root, "*.exe") } },
            "ready-script" => target with { Pc = target.Pc! with { ReadyExecutable = new(root, "game.cmd") } },
            "missing-kind" => target with { Pc = null },
            "both-kinds" => target with { Android = new(AutomationAndroidDriver.GenericAdb, new(root, "adb.exe"), "127.0.0.1:5555", "test.game", []) },
            "wait" => target with { WaitSeconds = -1 },
            "arguments" => target with { Pc = target.Pc! with { Arguments = default } },
            _ => throw new InvalidOperationException()
        };
        Assert.Equal("automation_target_invalid", Assert.Throws<AutomationDefinitionException>(() =>
            AutomationTargetPolicy.Validate(target, new HashSet<Guid> { root })).Message);
    }

    [Theory(DisplayName = "Android Definitions Require An Explicit Endpoint And A Literal Package")]
    [InlineData("127.0.0.1:5555", "test.game", true)]
    [InlineData("[::1]:5555", "test.game", true)]
    [InlineData("device.local:5555", "test.game", true)]
    [InlineData("device.local", "test.game", false)]
    [InlineData("user@device.local:5555", "test.game", false)]
    [InlineData("device.local:5555/path", "test.game", false)]
    [InlineData("device.local:5555", "test.game;id", false)]
    public void RequiresExplicitEndpoints(string endpoint, string package, bool valid)
    {
        var root = Guid.NewGuid();
        var target = new AutomationTarget(AutomationTargetKind.Android, true, 10, false, null,
            new(AutomationAndroidDriver.GenericAdb, new(root, "adb.exe"), endpoint, package, []));
        var error = Record.Exception(() => AutomationTargetPolicy.Validate(target, new HashSet<Guid> { root }));
        if (valid) Assert.Null(error); else Assert.IsType<AutomationDefinitionException>(error);
    }

    [Fact(DisplayName = "Target Only Roots And Their Revisions Belong To The Same Frozen Selection")]
    public void FreezesTargetRoots()
    {
        var fixture = new ExecutionSelectionFixture();
        var game = new AutomationRoot(Guid.NewGuid(), 7, "Game", AutomationRootKind.External, @"D:\CoreFixture\game");
        var original = fixture.Catalog.Scripts.Single(value => value.Id == fixture.ScriptId);
        var target = Pc(game.Id);
        var catalog = fixture.Catalog with { Roots = fixture.Catalog.Roots.Add(game),
            Scripts = fixture.Catalog.Scripts.Replace(original, original with { Target = target }) };
        var selection = fixture.Select(new BindingTarget(fixture.BindingB), catalog);
        var selected = Assert.Single(selection.Items);
        Assert.Contains(selected.Roots, value => value.Id == game.Id && value.Revision == 7);
        Assert.Contains(selection.SourceRevisions, value => value.Kind == AutomationDefinitionKind.Root && value.EntityId == game.Id && value.Revision == 7);
        Assert.Equal(target.Pc!.Arguments.ToArray(), selected.Script.Target!.Pc!.Arguments.ToArray());
        Assert.Equal(new AutomationPath(game.Id, "launch.cmd"), selected.Script.Target.Pc.LaunchExecutable);
        Assert.Equal(new AutomationPath(game.Id, "game.exe"), selected.Script.Target.Pc.ReadyExecutable);
    }

    private static AutomationTarget Pc(Guid root) => new(AutomationTargetKind.Pc, true, 30, false,
        new(new(root, "launch.cmd"), ["literal argument"], new(root, "game.exe"), AutomationPcReadiness.VisibleWindow), null);
}

public sealed class MuMuScreenshotTests
{
    [Theory(DisplayName = "Only An Explicit Mu Mu Executable Inside A Declared Literal Root Can Be The Paired Screenshot Tool")]
    [InlineData("generic")]
    [InlineData("unknownRoot")]
    [InlineData("escape")]
    [InlineData("script")]
    [InlineData("wildcard")]
    public void RequiresExplicitTool(string damage)
    {
        var root = Guid.NewGuid();
        var android = new AutomationAndroidTarget(AutomationAndroidDriver.Mumu, new(root, "manager.exe"),
            "127.0.0.1:16384", "com.nexus.fixture", []) { ScreenshotTool = new(root, "paired/adb.exe") };
        android = damage switch
        {
            "generic" => android with { Driver = AutomationAndroidDriver.GenericAdb },
            "unknownRoot" => android with { ScreenshotTool = new(Guid.NewGuid(), "adb.exe") },
            "escape" => android with { ScreenshotTool = new(root, "../adb.exe") },
            "script" => android with { ScreenshotTool = new(root, "adb.cmd") },
            _ => android with { ScreenshotTool = new(root, "*.exe") }
        };
        var target = new AutomationTarget(AutomationTargetKind.Android, true, 15, false, null, android);
        Assert.Throws<AutomationDefinitionException>(() => AutomationTargetPolicy.Validate(target, new HashSet<Guid> { root }));
    }

    [Fact(DisplayName = "Pairing Adds Its Own Frozen Root Without Making The Optional Mu Mu Tool Mandatory")]
    public void FreezesOptionalTool()
    {
        var manager = Guid.NewGuid(); var screenshot = Guid.NewGuid();
        var target = new AutomationTarget(AutomationTargetKind.Android, false, 0, false, null,
            new(AutomationAndroidDriver.Mumu, new(manager, "manager.exe"), "127.0.0.1:16384", "com.nexus.fixture", []));
        var roots = new HashSet<Guid> { manager, screenshot };
        AutomationTargetPolicy.Validate(target, roots);
        Assert.Single(AutomationTargetPolicy.Paths(target));
        target = target with { Android = target.Android! with { ScreenshotTool = new(screenshot, "adb.exe") } };
        AutomationTargetPolicy.Validate(target, roots);
        var frozen = AutomationTargetPolicy.Freeze(target)!;
        Assert.Equal(target.Android!.ScreenshotTool, frozen.Android!.ScreenshotTool);
        Assert.Contains(AutomationTargetPolicy.Paths(frozen), path => path.RootId == screenshot);
    }
}

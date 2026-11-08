using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

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
        Assert.NotSame(target, selected.Script.Target);
        Assert.NotSame(target.Pc, selected.Script.Target.Pc);
    }

    private static AutomationTarget Pc(Guid root) => new(AutomationTargetKind.Pc, true, 30, false,
        new(new(root, "launch.cmd"), ["literal argument"], new(root, "game.exe"), AutomationPcReadiness.VisibleWindow), null);
}

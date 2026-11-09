using NexusPipeline.Core.Automation;
using Xunit;

namespace NexusPipeline.CoreTests;

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

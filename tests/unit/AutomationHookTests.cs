using NexusPipeline.Core.Automation;
using Xunit;

namespace NexusPipeline.CoreTests;

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

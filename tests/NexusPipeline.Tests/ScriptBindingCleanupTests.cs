using NexusPipeline.Models;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class ScriptBindingCleanupTests
{
    [Fact]
    public void RemoveForScript_RemovesEveryMatchingBindingAndCanRestoreOrder()
    {
        var user = new NexusUser
        {
            Bindings =
            [
                new UserScriptBinding { ScriptInstanceId = "deleted" },
                new UserScriptBinding { ScriptInstanceId = "keep-a" },
                new UserScriptBinding { ScriptInstanceId = "deleted" },
                new UserScriptBinding { ScriptInstanceId = "keep-b" },
            ],
        };

        List<RemovedScriptBinding> removed = ScriptBindingCleanup.RemoveForScript(
            new[] { user },
            "deleted");

        Assert.Equal(new[] { "keep-a", "keep-b" }, user.Bindings.Select(binding => binding.ScriptInstanceId));
        Assert.Equal(2, removed.Count);

        ScriptBindingCleanup.Restore(removed);

        Assert.Equal(
            new[] { "deleted", "keep-a", "deleted", "keep-b" },
            user.Bindings.Select(binding => binding.ScriptInstanceId));
    }

    [Fact]
    public void RemoveMissingScriptBindings_RemovesOnlyOrphanedReferences()
    {
        var user = new NexusUser
        {
            Bindings =
            [
                new UserScriptBinding { ScriptInstanceId = "present" },
                new UserScriptBinding { ScriptInstanceId = "deleted" },
            ],
        };

        int removed = ScriptBindingCleanup.RemoveMissingScriptBindings(
            new[] { user },
            new HashSet<string>(new[] { "present" }, StringComparer.Ordinal));

        Assert.Equal(1, removed);
        Assert.Single(user.Bindings);
        Assert.Equal("present", user.Bindings[0].ScriptInstanceId);
    }
}

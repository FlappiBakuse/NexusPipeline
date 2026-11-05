using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class ProviderPolicyTests
{
    [Fact(DisplayName = "Frozen Provider Arguments Cannot Follow Later Caller Array Changes")]
    public void FreezesArgumentArrays()
    {
        var arguments = new[] { "original" };
        var frozen = ExecutionProviderPolicy.Freeze(new("main", "worker/Provider.exe", arguments.ToImmutableArray(), null));
        arguments[0] = "changed";
        Assert.Equal("original", Assert.Single(frozen.Arguments));
    }

    [Theory(DisplayName = "Worker Entry Must Be A Portable Native Package Entry")]
    [InlineData("")]
    [InlineData("../escape.exe")]
    [InlineData("folder\\worker.exe")]
    [InlineData("/absolute.exe")]
    [InlineData("worker.cmd")]
    public void RequiresPortableEntry(string entry)
    {
        Assert.Throws<ArgumentException>(() => ExecutionProviderPolicy.Freeze(new("main", entry, [], null)));
    }

    [Theory]
    [InlineData(true, "worker-terminal/1")]
    [InlineData(false, "self-reported")]
    public void RejectsInvalidInput(bool writes, string protocol)
    {
        var declaration = new ExecutionProviderDeclaration("main", "Worker.exe", [], new("Launcher.exe", [], writes, protocol));
        Assert.Throws<ArgumentException>(() => ExecutionProviderPolicy.Freeze(declaration));
    }

    [Fact(DisplayName = "A Living Launcher Declaration Is Separate From The Necessary Worker Entry")]
    public void SeparatesLauncherEntry()
    {
        var declaration = ExecutionProviderPolicy.Freeze(new("main", "Worker.exe", [], new("Launcher.exe", [], false, "worker-terminal/1")));
        Assert.Equal("Worker.exe", declaration.WorkerEntry);
        Assert.Equal("Launcher.exe", declaration.Launcher!.Entry);
        Assert.False(declaration.Launcher.WritesManagedConfiguration);
    }
}

using NexusPipeline.Core.Execution;
using Xunit;
using System.Collections.Immutable;

namespace NexusPipeline.CoreTests;

public sealed class ProviderPolicyTests
{
    [Fact(DisplayName = "Frozen Provider Arguments Preserve Order Spaces And Empty Values")]
    public void PreservesArguments()
    {
        var frozen = ExecutionProviderPolicy.Freeze(new("main", "worker/Provider.exe", ["--profile", "two words", ""], null));
        Assert.Equal(new[] { "--profile", "two words", "" }, frozen.Arguments.ToArray());
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

public sealed class ProviderConfigurationTests
{
    [Theory(DisplayName = "Verified Resource Declarations Reject Ambiguous Or Unbounded Identities")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-index")]
    [InlineData("negative-index")]
    [InlineData("excess-index")]
    [InlineData("zero-revision")]
    [InlineData("excess-revision")]
    [InlineData("invalid-id")]
    [InlineData("default")]
    public void RejectsAmbiguousResources(string fault)
    {
        var first = new ProviderConfigurationDeclaration("main-config", 0, 1);
        ImmutableArray<ProviderConfigurationDeclaration> resources = fault switch
        {
            "duplicate-id" => [first, first with { DeclarationIndex = 1 }],
            "duplicate-index" => [first, first with { ResourceId = "other" }],
            "negative-index" => [first with { DeclarationIndex = -1 }],
            "excess-index" => [first with { DeclarationIndex = 32 }],
            "zero-revision" => [first with { DefinitionRevision = 0 }],
            "excess-revision" => [first with { DefinitionRevision = 9007199254740992 }],
            "invalid-id" => [first with { ResourceId = "Other/Resource" }],
            _ => default
        };
        Assert.Throws<ArgumentException>(() => ExecutionProviderPolicy.Freeze(new("main", "worker.exe", [], null)
            { ConfigurationResources = resources }));
    }
}

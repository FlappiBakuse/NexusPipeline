using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

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

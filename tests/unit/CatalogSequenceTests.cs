using NexusPipeline.Core.Extensions;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class CatalogSequenceTests
{
    [Theory(DisplayName = "Release Floor And The Already Accepted Maximum Are Both Required")]
    [InlineData(4, 5, 0, false)]
    [InlineData(5, 5, 0, true)]
    [InlineData(6, 5, 0, true)]
    [InlineData(5, 1, 6, false)]
    [InlineData(6, 1, 6, true)]
    [InlineData(7, 1, 6, true)]
    public void RequiresReleaseFloor(long sequence, long floor, long maximum, bool accepted)
    {
        var candidate = new ExtensionCatalogIdentity(ExtensionStoreChannel.Stable, Guid.NewGuid(), sequence, new('a', 64));
        var head = maximum == 0 ? null : new ExtensionCatalogWatermark(maximum, candidate.Sha256);
        if (accepted) CatalogSequencePolicy.Require(candidate, floor, head);
        else Assert.Throws<InvalidOperationException>(() => CatalogSequencePolicy.Require(candidate, floor, head));
    }

    [Fact(DisplayName = "An Equal Sequence Requires The Same Original Byte Digest")]
    public void BindsEqualSequences()
    {
        var candidate = new ExtensionCatalogIdentity(ExtensionStoreChannel.Develop, Guid.NewGuid(), 12, new('a', 64));
        Assert.Throws<InvalidOperationException>(() =>
            CatalogSequencePolicy.Require(candidate, 1, new(12, new('b', 64))));
        CatalogSequencePolicy.Require(candidate, 1, new(12, candidate.Sha256));
    }

    [Theory(DisplayName = "Malformed Identity Is Never A Valid Watermark")]
    [InlineData("zero")]
    [InlineData("overflow")]
    [InlineData("empty-generation")]
    [InlineData("digest")]
    [InlineData("channel")]
    public void RejectsMalformedIdentity(string fault)
    {
        var value = new ExtensionCatalogIdentity(ExtensionStoreChannel.Stable, Guid.NewGuid(), 1, new('a', 64));
        value = fault switch
        {
            "zero" => value with { Sequence = 0 },
            "overflow" => value with { Sequence = CatalogSequencePolicy.MaximumSequence + 1 },
            "empty-generation" => value with { GenerationId = Guid.Empty },
            "digest" => value with { Sha256 = new('A', 64) },
            _ => value with { Channel = (ExtensionStoreChannel)99 }
        };
        Assert.Throws<ArgumentException>(() => CatalogSequencePolicy.Require(value, 1, null));
    }
}

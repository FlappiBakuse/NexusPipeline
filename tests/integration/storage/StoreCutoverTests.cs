using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class StoreCutoverTests
{
    [Theory]
    [InlineData("catalog")]
    [InlineData("signature")]
    [InlineData("pointer")]
    public async Task RecoversInterruptedPublication(string stage)
    {
        using var inputs = new SignedStoreInputs();
        await using var mirror = await StoreMirror.StartAsync(); inputs.Populate(mirror);
        await using var runtime = new StoreMirrorRuntime(inputs, mirror);
        await runtime.Runtime.StartAsync(default);
        var original = await runtime.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        var pointerUrl = new Uri(ExtensionCatalogFixture.MetadataRoot, "stable/index.json").AbsoluteUri;
        StoreMirrorFailures.Publish(runtime, inputs, mirror, original.Identity.Sequence + 1);
        using var pointer = JsonDocument.Parse(mirror.Files[pointerUrl]);
        var failedUrl = stage == "pointer" ? pointerUrl : pointer.RootElement
            .GetProperty(stage == "catalog" ? "catalogUrl" : "signatureUrl").GetString()!;
        Assert.True(mirror.Files.TryRemove(failedUrl, out var withheld));
        await Assert.ThrowsAsync<IOException>(() => runtime.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default));
        Assert.Equal(original.Identity, runtime.Service.ReadCached(ExtensionStoreChannel.Stable)!.Identity);
        mirror.Files[failedUrl] = withheld!;
        var accepted = await runtime.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        Assert.Equal(original.Identity.Sequence + 1, accepted.Identity.Sequence);
        Assert.Equal(original.Entries.ToArray(), accepted.Entries.ToArray());
        Assert.Empty(runtime.Runtime.ReadStates());
        inputs.VerifyUnchanged();
    }

    [Fact]
    public async Task RestoresPriorContent()
    {
        using var inputs = new SignedStoreInputs();
        await using var mirror = await StoreMirror.StartAsync(); inputs.Populate(mirror);
        await using var runtime = new StoreMirrorRuntime(inputs, mirror);
        await runtime.Runtime.StartAsync(default);
        var original = await runtime.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        StoreMirrorFailures.Publish(runtime, inputs, mirror, original.Identity.Sequence + 1,
            catalog => catalog["entries"]!.AsArray().RemoveAt(0));
        var changed = await runtime.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        Assert.Equal(13, changed.Entries.Length);
        StoreMirrorFailures.Publish(runtime, inputs, mirror, original.Identity.Sequence + 2);
        var restored = await runtime.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        Assert.Equal(original.Identity.Sequence + 2, restored.Identity.Sequence);
        Assert.Equal(original.Entries.ToArray(), restored.Entries.ToArray());
        Assert.NotEqual(original.Identity.GenerationId, restored.Identity.GenerationId);
        inputs.VerifyUnchanged();
    }
}

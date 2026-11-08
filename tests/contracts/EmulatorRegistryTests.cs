using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Emulation;
using NexusPipeline.Platform.Extensions;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class EmulatorRegistryTests
{
    [Theory(DisplayName = "Emulator Providers Require A Managed Manifest With A Distinct Declared Provider Id And Matching Capability")]
    [InlineData("capability")]
    [InlineData("ids")]
    [InlineData("duplicate")]
    [InlineData("invalid")]
    [InlineData("kind")]
    public void RequiresDeclaredProvider(string changed)
    {
        var manifest = Manifest();
        if (changed == "capability") manifest["capabilities"] = new JsonArray();
        else if (changed == "ids") manifest["managed"]!.AsObject().Remove("emulators");
        else if (changed == "duplicate") manifest["managed"]!["emulators"] = new JsonArray("vendors", "vendors");
        else if (changed == "invalid") manifest["managed"]!["emulators"] = new JsonArray("../vendors");
        else { manifest["kind"] = "data-specialized"; manifest.Remove("managed"); manifest["specialized"] = new JsonObject { ["adapterEntry"] = "adapter.js" }; }
        Assert.Throws<JsonException>(() => new ExtensionManifestAdmission(new()).ReadCurrent(Encoding.UTF8.GetBytes(manifest.ToJsonString()), "sample-provider"));
    }

    [Fact(DisplayName = "The Actual Manifest Reader Retains The Declared Emulator Provider Without Adding File Or Process Permissions")]
    public void AcceptsDeclaredProvider()
    {
        var manifest = new ExtensionManifestAdmission(new()).ReadCurrent(Encoding.UTF8.GetBytes(Manifest().ToJsonString()), "sample-provider");
        Assert.Equal("vendors", Assert.Single(manifest.Managed!.Emulators!));
        Assert.Null(manifest.Permissions.FileAccess); Assert.Null(manifest.Permissions.ProcessAccess);
    }

    [Fact(DisplayName = "Emulator Publication Is Scoped And Drains An Actual Inflight Host Callback Before Removing Registration")]
    public async Task DrainsPublishedProvider()
    {
        var registry = new EmulatorProviderRegistry(); var scope = new ExtensionScope("sample-provider");
        await scope.OwnAsync(registry.Register(scope, "vendors", new Provider()));
        await Assert.ThrowsAsync<IOException>(() => registry.UseAsync("sample-provider", "vendors", (_, _) => ValueTask.FromResult(true), default).AsTask());
        scope.Publish();
        Assert.Throws<IOException>(() => registry.Register(scope, "vendors", new Provider()));
        await Assert.ThrowsAsync<IOException>(() => registry.UseAsync("other", "vendors", (_, _) => ValueTask.FromResult(true), default).AsTask());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = registry.UseAsync("sample-provider", "vendors", async (_, current) =>
        { entered.SetResult(); await release.Task; current(); return true; }, default).AsTask();
        await entered.Task; var stop = scope.DisposeAsync().AsTask();
        Assert.True(scope.Cancellation.IsCancellationRequested); Assert.False(stop.IsCompleted); release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); await stop;
        await Assert.ThrowsAsync<IOException>(() => registry.UseAsync("sample-provider", "vendors", (_, _) => ValueTask.FromResult(true), default).AsTask());
    }

    [Fact]
    public async Task SelectsProvider()
    {
        var registry = new EmulatorProviderRegistry();
        await using var first = new ExtensionScope("alpha-device");
        await using var second = new ExtensionScope("beta-device");
        await first.OwnAsync(registry.Register(first, "devices", new Provider())); first.Publish();
        Assert.Equal(("alpha-device", "devices"), registry.Select());
        await second.OwnAsync(registry.Register(second, "inventory", new Provider())); second.Publish();
        Assert.Equal("emulator_provider_ambiguous", Assert.Throws<IOException>(() => registry.Select()).Message);
        Assert.Equal(("beta-device", "inventory"), registry.Select("beta-device", "inventory"));
        await second.DisposeAsync();
        Assert.Equal(("alpha-device", "devices"), registry.Select());
    }

    private static JsonObject Manifest() => new()
    {
        ["format"] = "nexus.extension.manifest", ["version"] = 1, ["extensionId"] = "sample-provider", ["artifactName"] = "SampleProvider",
        ["displayName"] = "Fixture", ["packageVersion"] = "0.5.0", ["kind"] = "managed-code", ["minHostVersion"] = "0.18.0",
        ["sdkVersion"] = "0.1.0", ["contractFingerprint"] = SdkIdentity.ContractFingerprint,
        ["capabilities"] = new JsonArray("emulator.provide"), ["permissions"] = new JsonObject(),
        ["managed"] = new JsonObject { ["entryAssembly"] = "Fixture.dll", ["entryType"] = "Fixture.Extension", ["emulators"] = new JsonArray("vendors") }
    };

    private sealed class Provider : IEmulatorProvider
    {
        public IReadOnlyList<EmulatorLayout> Layouts => [];
        public EmulatorTargetDescription Describe(EmulatorTarget target) => throw new NotSupportedException();
        public ValueTask<EmulatorDiscoveryResult> DiscoverAsync(IEmulatorInventory inventory, bool supportsAndroid, CancellationToken cancellationToken) => throw new NotSupportedException();
        public EmulatorTarget Select(IReadOnlyList<EmulatorSource> sources, string targetId, string endpoint, bool supportsAndroid) => throw new NotSupportedException();
        public IEmulatorSession Open(IEmulatorHost host, EmulatorTarget target) => throw new NotSupportedException();
    }
}

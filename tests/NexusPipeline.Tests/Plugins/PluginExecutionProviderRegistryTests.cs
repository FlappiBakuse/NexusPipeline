using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Plugins.Runtime;
using Xunit;

namespace NexusPipeline.Tests.Plugins;

public sealed class PluginExecutionProviderRegistryTests
{
    [Fact]
    public void RegistrationIsOwnerBoundUniqueAndRevokedOnDispose()
    {
        var registry = new PluginExecutionProviderRegistry();
        var provider = new FakeProvider("maa-framework");
        Assert.Throws<ArgumentException>(() => registry.Register("other-plugin", provider));
        using IDisposable registration = registry.Register("maa-framework", provider);
        Assert.Same(provider, registry.Resolve("maa-framework", _ => true));
        Assert.Null(registry.Resolve("maa-framework", _ => false));
        Assert.Throws<InvalidOperationException>(() => registry.Register("maa-framework", new FakeProvider("maa-framework")));
        registration.Dispose();
        Assert.Null(registry.Resolve("maa-framework", _ => true));
        using IDisposable replacement = registry.Register("maa-framework", new FakeProvider("maa-framework"));
        Assert.NotNull(registry.Resolve("maa-framework", _ => true));
        registry.Clear();
        Assert.Null(registry.Resolve("maa-framework", _ => true));
    }

    [Theory]
    [InlineData("MaaFramework")]
    [InlineData("bad/route")]
    [InlineData("double--hyphen")]
    public void RejectsInvalidProviderIds(string id)
    {
        var registry = new PluginExecutionProviderRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(id, new FakeProvider(id)));
    }

    private sealed class FakeProvider(string id) : IPluginExecutionProvider
    {
        public string Id => id;
        public ValueTask<PluginProviderInspection> InspectAsync(PluginProviderInspectRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PluginProviderInspection("test", "1", [], new JsonObject()));
        public ValueTask<PluginProviderPlan> PrepareAsync(PluginProviderPrepareRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PluginProviderPlan("plan", request.ConfigRevision, "fingerprint", [], [], new JsonObject()));
        public Task<PluginProviderRunResult> RunAsync(PluginProviderRunContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new PluginProviderRunResult("succeeded", "", true));
    }
}

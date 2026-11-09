using System.Text;
using System.Text.Json;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class ExtensionAdmissionTests
{
    private static string ValidManifest => """
        {"format":"nexus.extension.manifest","version":1,"extensionId":"fixture","artifactName":"Fixture",
        "displayName":"Fixture","packageVersion":"0.5.0","kind":"managed-code","minHostVersion":"0.18.0",
        "sdkVersion":"0.1.0","contractFingerprint":"__fingerprint__","capabilities":[],"permissions":{},"managed":{"entryAssembly":"Fixture.dll","entryType":"Fixture.Extension"}}
        """.Replace("__fingerprint__", SdkIdentity.ContractFingerprint, StringComparison.Ordinal);

    [Theory(DisplayName = "Invalid Manifest Is Rejected Before Extension Construction")]
    [InlineData("legacy")]
    [InlineData("sdk")]
    [InlineData("capability")]
    [InlineData("identity")]
    [InlineData("future-host")]
    [InlineData("fingerprint")]
    [InlineData("missing-fingerprint")]
    public async Task RejectsInvalidManifests(string fault)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(ValidManifest)!;
        if (fault == "legacy") { node["format"] = "plugin"; node["version"] = "1.7"; }
        if (fault == "sdk") node["sdkVersion"] = "1.7";
        if (fault == "capability") node["capabilities"] = new System.Text.Json.Nodes.JsonArray("execute-anything");
        if (fault == "future-host") node["minHostVersion"] = "0.19.0";
        if (fault == "fingerprint") node["contractFingerprint"] = new string('0', 64);
        if (fault == "missing-fingerprint") node.AsObject().Remove("contractFingerprint");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        var original = bytes.ToArray();
        var constructed = 0;
        await Assert.ThrowsAsync<JsonException>(async () => await new ExtensionActivator(new ContractReader()).ActivateManagedAsync(
            bytes, fault == "identity" ? "other" : "fixture", (_, _) => { constructed++; return new Probe([], false); }, CancellationToken.None));
        Assert.Equal(0, constructed);
        Assert.Equal(original, bytes);
    }

    [Fact(DisplayName = "Failed Activation Aborts And Releases All Partial Registrations In Reverse Order")]
    public async Task RollsBackActivation()
    {
        var events = new List<string>();
        var probe = new Probe(events, true);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new ExtensionActivator(new ContractReader()).ActivateManagedAsync(
            Encoding.UTF8.GetBytes(ValidManifest), "fixture", (_, _) => probe, CancellationToken.None));
        Assert.True(probe.Scope!.Cancellation.IsCancellationRequested);
        Assert.Equal(["release:second:True", "release:first:True"], events);
    }

    [Fact(DisplayName = "Successful Activation Keeps Registrations Until Its Scope Is Disposed")]
    public async Task KeepsPublishedRegistrations()
    {
        var events = new List<string>();
        await using (var scope = await new ExtensionActivator(new ContractReader()).ActivateManagedAsync(
            Encoding.UTF8.GetBytes(ValidManifest), "fixture", (_, _) => new Probe(events, false), CancellationToken.None))
        {
            Assert.False(scope.Cancellation.IsCancellationRequested);
            Assert.Empty(events);
            Assert.True(scope.IsPublished);
        }
        Assert.Equal(["release:second:True", "release:first:True"], events);
    }

    [Fact(DisplayName = "Cancelled Activation Does Not Construct An Extension")]
    public async Task SkipsCancelledConstruction()
    {
        var constructed = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await new ExtensionActivator(new ContractReader()).ActivateManagedAsync(
            Encoding.UTF8.GetBytes(ValidManifest), "fixture", (_, _) => { constructed++; return new Probe([], false); }, new CancellationToken(true)));
        Assert.Equal(0, constructed);
    }

    [Fact(DisplayName = "Publication Waits For Activation And Revocation Precedes Cancellation And Cleanup")]
    public async Task OrdersScopePublication()
    {
        var entered = new TaskCompletionSource<ExtensionScope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revoked = new List<bool>();
        var activation = new ExtensionActivator(new ContractReader()).ActivateManagedAsync(
            Encoding.UTF8.GetBytes(ValidManifest), "fixture", (_, scope) => new DelayedProbe(scope, entered, release, revoked),
            CancellationToken.None).AsTask();
        var pending = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsPublished);
        Assert.False(activation.IsCompleted);
        release.SetResult();
        var published = await activation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pending.IsPublished);
        Assert.True(published.IsPublished);
        await published.DisposeAsync();
        Assert.False(published.IsPublished);
        Assert.False(pending.IsPublished);
        Assert.True(pending.Cancellation.IsCancellationRequested);
        Assert.Equal([true, true], revoked);
    }

    private sealed class DelayedProbe(ExtensionScope scope, TaskCompletionSource<ExtensionScope> entered,
        TaskCompletionSource release, List<bool> revoked) : IExtension
    {
        public async ValueTask ActivateAsync(IExtensionScope lifetime, CancellationToken cancellationToken)
        {
            lifetime.Cancellation.Register(() => revoked.Add(!scope.IsPublished));
            await lifetime.OwnAsync(new VisibilityResource(scope, revoked));
            entered.SetResult(scope);
            await release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class VisibilityResource(ExtensionScope scope, List<bool> revoked) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            revoked.Add(!scope.IsPublished);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Probe(List<string> events, bool fails) : IExtension
    {
        public IExtensionScope? Scope { get; private set; }

        public async ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken)
        {
            Scope = scope;
            Assert.False(((ExtensionScope)scope).IsPublished);
            await scope.OwnAsync(new Registration("first", scope, events));
            await scope.OwnAsync(new Registration("second", scope, events));
            if (fails) throw new InvalidOperationException("controlled_activation_failure");
        }
    }

    private sealed class Registration(string name, IExtensionScope scope, List<string> events) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            events.Add($"release:{name}:{scope.Cancellation.IsCancellationRequested}");
            return ValueTask.CompletedTask;
        }
    }
}

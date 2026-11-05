using System.Runtime.Versioning;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ProviderPackageTests
{
    [Fact(DisplayName = "A Controlled Package Registration Is Invisible Until Its Actual Activation Publishes And Revokes With That Scope")]
    public async Task PublishesScopedRegistration()
    {
        using var fixture = new ProviderPackageFixture();
        var before = fixture.Fingerprint(); var package = fixture.Verify();
        var registry = new ExecutionProviderRegistry(); await using var scope = new ExtensionScope("provider-fixture");
        var port = new ExecutionRegistrationPort(scope, package, registry);
        await port.RegisterAsync(default);
        Assert.Throws<IOException>(() => registry.Acquire("provider-fixture"));
        scope.Publish();
        using var prepared = registry.Acquire("provider-fixture");
        Assert.Equal(scope.ActivationId, prepared.ActivationId);
        Assert.Equal(package.PackageSha256, prepared.Package.PackageSha256);
        await scope.DisposeAsync();
        Assert.True(prepared.Cancellation.IsCancellationRequested);
        Assert.Throws<IOException>(() => prepared.Use(value => value.PackageSha256));
        Assert.Throws<IOException>(() => registry.Acquire("provider-fixture"));
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Fact(DisplayName = "Returned Worker And Upstream Runtime Arrays Cannot Mutate The Verified Package Or Later Captures")]
    public async Task FreezesPackageArrays()
    {
        using var fixture = new ProviderPackageFixture(); var package = fixture.Verify();
        var registry = new ExecutionProviderRegistry(); await using var scope = new ExtensionScope("provider-fixture");
        await new ExecutionRegistrationPort(scope, package, registry).RegisterAsync(default); scope.Publish();
        using var prepared = registry.Acquire("provider-fixture");
        var upstream = package.Worker; var arguments = new[] { "original" };
        var first = ExecutionProviderInputs.Capture(prepared, upstream, arguments);
        var expected = first.Worker.RuntimeFiles[0];
        ((IList<ExecutionPlanRuntimeFile>)first.Worker.RuntimeFiles)[0] = expected with { Sha256 = new string('a', 64) };
        ((IList<ExecutionPlanRuntimeFile>)first.Upstream.RuntimeFiles)[0] = expected with { Sha256 = new string('b', 64) };
        ((IList<ExecutionPlanRuntimeFile>)upstream.RuntimeFiles)[0] = expected with { Sha256 = new string('c', 64) };
        ((IList<string>)first.UpstreamArguments)[0] = "returned mutation"; arguments[0] = "caller mutation";
        var second = ExecutionProviderInputs.Capture(prepared, package.Worker, ["original"]);
        Assert.Equal(expected, second.Worker.RuntimeFiles[0]);
        Assert.Equal(expected, second.Upstream.RuntimeFiles[0]);
        Assert.Equal(expected, package.Worker.RuntimeFiles[0]);
        Assert.Equal("original", Assert.Single(second.UpstreamArguments));
    }

    [Theory(DisplayName = "A Deployed Package Mismatch Never Produces An Authority And Preserves The Exact Input Bytes")]
    [InlineData("content")]
    [InlineData("missing")]
    [InlineData("extra")]
    public void PreservesMismatchedPackages(string damage)
    {
        using var fixture = new ProviderPackageFixture();
        if (damage == "content") File.AppendAllText(fixture.Worker, "changed");
        else if (damage == "missing") File.Delete(fixture.Worker);
        else File.WriteAllText(Path.Combine(fixture.Deployed, "unexpected.txt"), "unexpected");
        var before = fixture.Fingerprint();
        Assert.Throws<IOException>(() => fixture.Verify());
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Fact(DisplayName = "A Changed Package After Verification Cannot Be Registered Or Resolved From Its Former Descriptor")]
    public async Task RejectsChangedPackages()
    {
        using var fixture = new ProviderPackageFixture();
        var package = fixture.Verify(); File.AppendAllText(fixture.Worker, "changed");
        var registry = new ExecutionProviderRegistry(); await using var scope = new ExtensionScope("provider-fixture");
        var before = fixture.Fingerprint();
        await Assert.ThrowsAsync<IOException>(async () =>
            await new ExecutionRegistrationPort(scope, package, registry).RegisterAsync(default));
        scope.Publish();
        Assert.Throws<IOException>(() => registry.Acquire("provider-fixture"));
        Assert.Equal(before, fixture.Fingerprint());
    }
}

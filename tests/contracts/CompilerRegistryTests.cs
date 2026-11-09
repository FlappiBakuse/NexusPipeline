using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Compilation;
using NexusPipeline.Platform.Extensions;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class CompilerRegistryTests
{
    [Fact(DisplayName = "Compiler Registration Is Invisible Until Publication And Remains Bound To Its Original Activation")]
    public async Task BindsPublishedCompiler()
    {
        var registry = new ProjectCompilerRegistry(); var scope = new ExtensionScope("fixture-compiler");
        var compiler = new Compiler(); var registration = registry.Register(scope, "project", compiler);
        await scope.OwnAsync(registration);
        await Assert.ThrowsAsync<IOException>(() => registry.UseAsync("fixture-compiler", "project", Accept, default).AsTask());
        scope.Publish(); Assert.Equal("registered-project", await registry.UseAsync("fixture-compiler", "project", Accept, default));
        Assert.Throws<InvalidOperationException>(() => registry.Register(scope, "project", compiler));
        await Assert.ThrowsAsync<IOException>(() => registry.UseAsync("another", "project", Accept, default).AsTask());
        await registration.DisposeAsync();
        await Assert.ThrowsAsync<IOException>(() => registry.UseAsync("fixture-compiler", "project", Accept, default).AsTask());
        await scope.DisposeAsync();
        async ValueTask<string?> Accept(IProjectCompiler actual, Action check)
        {
            check();
            var result = await actual.DiscoverAsync(new Source(), JsonSerializer.SerializeToElement(new { }), default);
            return result.GetString();
        }
    }

    [Fact(DisplayName = "Compiler Retirement Cancels But Still Drains The Actual Inflight Callback Before Releasing Registration")]
    public async Task DrainsCompilerCallback()
    {
        var registry = new ProjectCompilerRegistry(); var scope = new ExtensionScope("fixture-compiler");
        await scope.OwnAsync(registry.Register(scope, "project", new Compiler())); scope.Publish();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = registry.UseAsync("fixture-compiler", "project", async (_, check) =>
        { entered.SetResult(); await release.Task; check(); return 1; }, default).AsTask();
        await entered.Task; var stop = scope.DisposeAsync().AsTask();
        Assert.True(scope.Cancellation.IsCancellationRequested); Assert.False(stop.IsCompleted); release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); await stop;
        await Assert.ThrowsAsync<IOException>(() => registry.UseAsync("fixture-compiler", "project", (_, _) => ValueTask.FromResult(1), default).AsTask());
    }

    [Theory(DisplayName = "A Compiler Manifest Must Declare Its Id Capability And Selected Source Permission Together")]
    [InlineData("capability")]
    [InlineData("permission")]
    [InlineData("ids")]
    [InlineData("duplicate")]
    [InlineData("self")]
    public void RequiresDeclaredCompiler(string changed)
    {
        var manifest = Manifest();
        if (changed == "capability") manifest["capabilities"] = new JsonArray();
        else if (changed == "permission") manifest["permissions"] = new JsonObject();
        else if (changed == "ids") manifest["managed"]!.AsObject().Remove("compilers");
        else if (changed == "duplicate") manifest["managed"]!["compilers"] = new JsonArray("project", "project");
        else
        {
            manifest["kind"] = "data-specialized"; manifest.Remove("managed"); manifest["capabilities"] = new JsonArray("task.adapt");
            manifest["specialized"] = new JsonObject { ["adapterEntry"] = "adapter.js",
                ["compiler"] = new JsonObject { ["extensionId"] = "fixture-compiler", ["compilerId"] = "project" } };
        }
        Assert.Throws<JsonException>(() => new ExtensionManifestAdmission(new()).ReadCurrent(Encoding.UTF8.GetBytes(manifest.ToJsonString()), "fixture-compiler"));
    }

    [Fact(DisplayName = "The Actual Manifest Reader Accepts The Declared Compiler Provider And A Separate Source Consumer")]
    public void AcceptsDeclaredCompiler()
    {
        var manifest = Manifest(); var reader = new ExtensionManifestAdmission(new());
        var provider = reader.ReadCurrent(Encoding.UTF8.GetBytes(manifest.ToJsonString()), "fixture-compiler");
        Assert.Equal("project", Assert.Single(provider.Managed!.Compilers!));
        manifest["kind"] = "data-specialized"; manifest.Remove("managed"); manifest["capabilities"] = new JsonArray("task.adapt");
        manifest["specialized"] = new JsonObject { ["adapterEntry"] = "adapter.js",
            ["compiler"] = new JsonObject { ["extensionId"] = "another-provider", ["compilerId"] = "project" } };
        var consumer = reader.ReadCurrent(Encoding.UTF8.GetBytes(manifest.ToJsonString()), "fixture-compiler");
        Assert.Equal("another-provider", consumer.Specialized!.Compiler!.ExtensionId);
    }

    private static JsonObject Manifest() => new()
    {
        ["format"] = "nexus.extension.manifest", ["version"] = 1, ["extensionId"] = "fixture-compiler", ["artifactName"] = "FixtureCompiler",
        ["displayName"] = "Fixture", ["packageVersion"] = "0.5.0", ["kind"] = "managed-code", ["minHostVersion"] = "0.18.0",
        ["sdkVersion"] = "0.1.0", ["contractFingerprint"] = SdkIdentity.ContractFingerprint,
        ["capabilities"] = new JsonArray("source.compile"), ["permissions"] = new JsonObject { ["fileAccess"] = new JsonArray("selected-script-root") },
        ["managed"] = new JsonObject { ["entryAssembly"] = "Fixture.dll", ["entryType"] = "Fixture.Extension", ["compilers"] = new JsonArray("project") }
    };

    private sealed class Source : ISelectedSource
    {
        public IReadOnlyList<SourceConfiguration> Configurations => [];
        public ValueTask<SourceDirectory> ListAsync(string relativeDirectory, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SourceDirectory(true, []));
        public ValueTask<SourceFile?> ReadAsync(string relativePath, CancellationToken cancellationToken) =>
            ValueTask.FromResult<SourceFile?>(null);
    }

    private sealed class Compiler : IProjectCompiler
    {
        public ValueTask<JsonElement> DiscoverAsync(ISelectedSource source, JsonElement selection, CancellationToken cancellationToken) =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement("registered-project"));
        public ValueTask<CompilationResult> CompileAsync(ISelectedSource source, JsonElement selection, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

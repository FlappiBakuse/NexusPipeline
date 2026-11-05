using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstalledCompilerTests
{
    [Theory(DisplayName = "An Actual Installed Compiler Uses The Host Selected Source And Either Persists Large Content Or Drains On Activation Retirement")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsesInstalledCompiler(bool cancel)
    {
        using var sourceFixture = new ProjectSourceFixture(); sourceFixture.Write("interface.json", "{\"task\":[]}"u8.ToArray());
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend", catalog: sourceFixture.Catalog, managedManifestEdit: manifest =>
        {
            manifest["capabilities"]!.AsArray().Add("source.compile"); manifest["managed"]!["compilers"] = new JsonArray("project");
            manifest["permissions"]!["fileAccess"]!.AsArray().Add("selected-script-root");
        });
        File.WriteAllText(Path.Combine(fixture.ActivationDirectory, "compiler-mode"), "enabled");
        _ = await FrontendInstalledTests.OpenAsync(fixture);
        var registry = fixture.Activator.Compilers;
        using var selected = sourceFixture.Open();
        SelectedSourcePort? retained = null;
        try
        {
            var running = registry.UseAsync("fixture-plugin", "project", async (provider, check) =>
            {
                using var source = new SelectedSourcePort(selected, check, default); retained = source;
                var discovered = await provider.DiscoverAsync(source, JsonSerializer.SerializeToElement(new { }), default);
                Assert.True(discovered.GetProperty("Exists").GetBoolean());
                var result = await provider.CompileAsync(source, JsonSerializer.SerializeToElement(new { wait = cancel }), default);
                var graph = CompilationGraph.Freeze(result); _ = selected.Freeze(default);
                var blobs = new ImmutableBlobStore(sourceFixture.Catalog.Instance, new()); var contents = graph.Persist(blobs, default);
                foreach (var item in contents) { using var file = blobs.OpenVerified(item.Content); Assert.Equal(item.Content.SizeBytes, file.Length); }
                Assert.Equal(3 * 1024 * 1024, contents.Single(item => item.Name == "artifact").Content.SizeBytes);
                return graph;
            }, default).AsTask();
            if (cancel)
            {
                var entered = Path.Combine(fixture.ActivationDirectory, "compiler-entered");
                var deadline = Environment.TickCount64 + 10000;
                while (!File.Exists(entered) && Environment.TickCount64 < deadline) await Task.Delay(20);
                Assert.True(File.Exists(entered)); await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
                Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "compiler-cancelled")));
            }
            else { Assert.Equal("interface.json", (await running).ExecutionView.GetProperty("Path").GetString()); selected.RequireCurrent(); }
            Assert.NotNull(retained);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => retained.ReadAsync("interface.json", default).AsTask());
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
        await Assert.ThrowsAsync<IOException>(() => registry.UseAsync("fixture-plugin", "project", (_, _) => ValueTask.FromResult(1), default).AsTask());
    }
}

using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ProjectSaveFixture : IAsyncDisposable
{
    internal ProjectSourceFixture Project { get; }
    internal ProjectSaveFixture(bool configuration = false) { Project = new(configuration: configuration); }
    internal InstallRuntimeFixture Provider { get; private set; } = null!;
    private InstallRuntimeFixture? consumer;
    internal ProjectCompilationService Service { get; private set; } = null!;
    internal SecretDocumentService Secrets { get; private set; } = null!;

    internal async Task StartAsync(ISecretPreparationObserver? observer = null)
    {
        Project.Write("interface.json", "{\"task\":[]}"u8.ToArray());
        Provider = new(managedMode: "frontend", catalog: Project.Catalog, managedManifestEdit: manifest =>
        {
            manifest["capabilities"]!.AsArray().Add("source.compile"); manifest["managed"]!["compilers"] = new JsonArray("project");
            manifest["permissions"]!["fileAccess"]!.AsArray().Add("selected-script-root");
            manifest["permissions"]!["secretAccess"] = new JsonArray("selected-binding");
        });
        File.WriteAllText(Path.Combine(Provider.ActivationDirectory, "compiler-mode"), "enabled");
        _ = await FrontendInstalledTests.OpenAsync(Provider);
        var manifest = JsonNode.Parse(Provider.Package.ManifestBytes)!.AsObject();
        manifest["extensionId"] = "maas"; manifest["artifactName"] = "FixtureConsumer"; manifest["kind"] = "data-specialized";
        manifest.Remove("managed"); manifest.Remove("frontend"); manifest.Remove("execution");
        manifest["capabilities"] = new JsonArray("task.adapt", "configuration.edit");
        manifest["permissions"] = new JsonObject { ["fileAccess"] = new JsonArray("selected-script-root") };
        manifest["specialized"] = new JsonObject { ["adapterEntry"] = "adapter.js", ["compiler"] = new JsonObject
            { ["extensionId"] = "fixture-plugin", ["compilerId"] = "project" } };
        consumer = new(catalog: Project.Catalog, originalManifest: manifest,
            managedPayloadEdit: directory => File.WriteAllText(Path.Combine(directory, "adapter.js"), "export function invoke() { return {}; }"),
            payloadRoots: Provider.Roots, existingActivator: Provider.Activator, catalogSequence: 2);
        _ = await consumer.Session().ApplyAsync(consumer.Prepared, default);
        consumer.Prepared.Dispose(); consumer.Verified.Dispose(); consumer.Root.Dispose();
        var catalog = Project.Catalog;
        Secrets = new(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources, catalog.Index, catalog.Contracts, observer);
        Service = Reopen();
    }

    internal ProjectCompilationService Reopen() => new(Project.Catalog.Instance, Project.Catalog.Database, Project.Catalog.Lease,
        Project.Catalog.Resources, Project.Catalog.Contracts, Provider.Roots, Provider.Activator, Secrets);

    internal async Task WaitAsync(string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(Provider.ActivationDirectory, name))) await Task.Delay(10, timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Provider is not null) await Provider.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
        consumer?.Dispose(); Provider?.Dispose(); Project.Dispose();
    }
}

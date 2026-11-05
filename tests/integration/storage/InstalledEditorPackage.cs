using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InstalledEditorPackage(InstallRuntimeFixture installation) : IAsyncDisposable
{
    internal ExtensionActivator Activator => installation.Activator;

    internal static async Task<InstalledEditorPackage> CreateAsync(ExecutionAdmissionFixture fixture,
        BuiltExtensionInputs? package = null)
    {
        var manifest = package?.Manifest ?? new JsonObject
        {
            ["format"] = "nexus.extension.manifest", ["version"] = 1,
            ["extensionId"] = "fixture-extension", ["artifactName"] = "FixtureEditor",
            ["displayName"] = "Controlled editor", ["packageVersion"] = "0.5.0", ["kind"] = "data-specialized",
            ["minHostVersion"] = "0.18.0", ["sdkVersion"] = SdkIdentity.Version,
            ["contractFingerprint"] = SdkIdentity.ContractFingerprint,
            ["capabilities"] = new JsonArray("task.adapt", "configuration.edit"), ["permissions"] = new JsonObject(),
            ["specialized"] = new JsonObject { ["adapterEntry"] = "adapter/main.js" }
        };
        var installed = new InstallRuntimeFixture(catalog: fixture.CreateCatalog(), originalManifest: manifest,
            originalManifestBytes: package?.ManifestBytes, packageArchive: package?.Archive,
            payloadRoots: new(Path.Combine(fixture.Source.Root, "host"), Path.Combine(fixture.Source.Instance.RuntimeRoot, "extensions")),
            managedPayloadEdit: directory =>
            {
                Directory.CreateDirectory(Path.Combine(directory, "adapter"));
                File.WriteAllText(Path.Combine(directory, "adapter", "main.js"),
                    "globalThis.nexusTaskAdapter = input => ({format:input.format,version:1,direction:'response',phase:input.phase,extensionId:input.extensionId,operationId:input.operationId,diagnostics:[]});\n");
            });
        try
        {
            var result = await installed.Session().ApplyAsync(installed.Prepared, default);
            Assert.True(result.IsInstalled); Assert.True(result.IsEnabled);
            installed.Prepared.Dispose(); installed.Verified.Dispose(); installed.Root.Dispose();
            return new(installed);
        }
        catch { installed.Dispose(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try { await installation.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
        finally { installation.Dispose(); }
    }
}

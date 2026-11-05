using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Configuration;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Control.Cli;
using NexusPipeline.Host.Control.Mcp;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class SourcePackageTests
{
    [Theory]
    [InlineData("baah")]
    [InlineData("bettergi")]
    public async Task DiscoversInstalledSources(string extensionId)
    {
        var setPath = Environment.GetEnvironmentVariable("NEXUS_PACKAGED_SET") ?? throw new IOException("package_set_required");
        using var packages = JsonDocument.Parse(File.ReadAllBytes(setPath));
        var package = Assert.Single(packages.RootElement.GetProperty("runs").EnumerateArray(),
            item => item.GetProperty("label").GetString() == extensionId + "-package");
        var receipt = package.GetProperty("receipt").GetString()!;
        Assert.Equal(package.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(receipt))));
        using var input = new BuiltExtensionInputs(extensionId, receipt, explicitPath: true);
        using var source = new ProjectSourceFixture(extensionId: extensionId);
        var catalog = source.Catalog;
        using var installed = new InstallRuntimeFixture(catalog: catalog, originalManifest: input.Manifest,
            originalManifestBytes: input.ManifestBytes, packageArchive: input.Archive);
        try
        {
            var activation = await installed.Session().ApplyAsync(installed.Prepared, default);
            Assert.True(activation.IsInstalled); Assert.True(activation.IsEnabled);
            installed.Prepared.Dispose(); installed.Verified.Dispose(); installed.Root.Dispose();
            ExtensionProjectionPublication.PublishIfDirty(catalog.Instance, catalog.Database, catalog.Lease, new(),
                installed.Roots, catalog.Resources, catalog.Index);
            var directory = Path.Combine(source.Path, "discover"); Directory.CreateDirectory(directory);
            var files = extensionId == "baah" ? new Dictionary<string, string> {
                ["BAAH.exe"] = "not executed", ["BAAH_CONFIGS/one.json"] = "{}", ["BAAH_CONFIGS/two.json"] = "{}",
                ["DATA/CONFIGS/software_config.json"] = "{}" } : new Dictionary<string, string> {
                ["BetterGI.exe"] = "not executed", ["User/config.json"] = "{}",
                ["User/OneDragon/one.json"] = "{\"Name\":\"First account\"}",
                ["User/OneDragon/two.json"] = "{\"Name\":\"Second account\"}" };
            foreach (var file in files)
            {
                var path = Path.Combine(directory, file.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, file.Value);
            }
            var service = new ScriptSourceService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources,
                catalog.Contracts, installed.Roots, installed.Activator);
            var cases = new SourceUseCases(service, catalog.Contracts);
            var enabled = true;
            var caller = new AuthenticatedControlRequest(ControlOrigin.Mcp, "source-reader", null, () => new(enabled, false));
            Assert.False(ControlCapabilityPolicy.IsAllowed(ControlOrigin.Mcp, ControlCapability.AutomationDeclare, new(true, true)));
            Assert.False(ControlCapabilityPolicy.IsAllowed(ControlOrigin.Remote, ControlCapability.AutomationDeclare, new(true, true)));
            Assert.True(ControlCapabilityPolicy.IsAllowed(ControlOrigin.Local, ControlCapability.AutomationDeclare, new(false, false)));
            var command = JsonSerializer.SerializeToElement(new { format = "nexus.control.script-source", version = 1,
                operationId = Guid.NewGuid(), extensionId, expectedInstalledRevision = 1, rootPath = directory });
            var result = JsonSerializer.SerializeToElement(await cases.DiscoverAsync(caller, command, default));
            var discovery = result.GetProperty("discovery");
            Assert.Equal(2, discovery.GetProperty("candidates").GetArrayLength());
            var arguments = discovery.GetProperty("candidates").EnumerateArray().Select(row => row.GetProperty("arguments").EnumerateArray().Last().GetString()).ToArray();
            Assert.Equal(extensionId == "baah" ? ["one.json", "two.json"] : new[] { "First account", "Second account" }, arguments);
            Assert.Equal("/control/v1/automation/scripts/discover", ControlCliRequest.Parse(
                ["script-discover", "--address", "http://127.0.0.1:27100", "--input", "request.json"]).Path);
            var tools = new McpAutomationTools(null!, sources: cases);
            Assert.Contains(tools.List(caller), item => item.Name == "script_discover");
            var repeated = await tools.DiscoverSourceAsync(caller, JsonSerializer.SerializeToElement(new { arguments = command }));
            Assert.NotNull(repeated);
            enabled = false;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cases.DiscoverAsync(caller, command, default).AsTask());
            foreach (var file in files) Assert.Equal(file.Value, File.ReadAllText(Path.Combine(directory, file.Key)));
            input.VerifyUnchanged();
        }
        finally { await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }
}

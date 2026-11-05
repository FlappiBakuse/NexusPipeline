using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Automation;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Automation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SpecializedPolicyTests
{
    [Theory(DisplayName = "Real Host Declaration Cannot Bypass An Installed Specialized Packages Required Configuration Synchronization")]
    [InlineData("baah")]
    [InlineData("bettergi")]
    [InlineData("maaend")]
    [InlineData("maas")]
    [InlineData("march7th")]
    [InlineData("oknte")]
    [InlineData("okww")]
    [InlineData("zzzonedragon")]
    public async Task EnforcesInstalledPolicy(string extensionId)
    {
        using var set = JsonDocument.Parse(File.ReadAllBytes(Environment.GetEnvironmentVariable("NEXUS_PACKAGED_SET")!));
        var record = set.RootElement.GetProperty("runs").EnumerateArray().Single(row => row.GetProperty("label").GetString() == extensionId + "-package");
        using var input = new BuiltExtensionInputs(extensionId, record.GetProperty("receipt").GetString()!, explicitPath: true);
        using var catalog = new ExtensionCatalogFixture();
        using var installed = new InstallRuntimeFixture(catalog: catalog, originalManifest: input.Manifest,
            originalManifestBytes: input.ManifestBytes, packageArchive: input.Archive);
        try
        {
            _ = await installed.Session().ApplyAsync(installed.Prepared, default);
            installed.Prepared.Dispose(); installed.Verified.Dispose(); installed.Root.Dispose();
            var service = new AutomationDefinitionService(catalog.Instance, new(), catalog.Database, catalog.Lease,
                catalog.Resources, catalog.Index, installed.Roots);
            var cases = new AutomationUseCases(service, new(new()));
            var changes = AutomationDefinitionSeed.Create(Path.Combine(Path.GetDirectoryName(catalog.Instance.RuntimeRoot)!, "policy-source"));
            changes = changes with { Scripts = [changes.Scripts[0] with { Kind = AutomationKind.Specialized,
                ExtensionId = extensionId, UpdateConfigurationAtFinish = false }] };
            var caller = new AuthenticatedControlRequest(ControlOrigin.Local, "policy-author", null, () => new(true, true));
            var error = Assert.Throws<AutomationDefinitionException>(() => cases.Change(caller, new(Guid.NewGuid(), changes)));
            Assert.Equal("automation_specialized_policy_conflict", error.Message);
            Assert.Equal(0, service.Read().Revision);
            input.VerifyUnchanged();
        }
        finally { await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }
}

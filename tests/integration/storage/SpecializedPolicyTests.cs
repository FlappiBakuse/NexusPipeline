using System.Runtime.Versioning;
using System.Collections.Immutable;
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
    [Fact(DisplayName = "Actual BetterGI Definition Changes And Imports Validate Every Script Sharing The Installed Manifest")]
    public async Task ValidatesSharedInstalledListing()
    {
        using var input = new SpecializedPackageInputs("bettergi", "NEXUS_FORMAL_BETTERGI_PACKAGE_RECEIPT");
        using var catalog = new ExtensionCatalogFixture();
        using var installed = new InstallRuntimeFixture(catalog: catalog, originalManifest: input.Manifest,
            originalManifestBytes: input.ManifestBytes, packageArchive: input.Archive);
        try
        {
            _ = await installed.Session().ApplyAsync(installed.Prepared, default);
            installed.Prepared.Dispose(); installed.Verified.Dispose(); installed.Root.Dispose();
            var contracts = new NexusPipeline.Platform.Serialization.ContractReader();
            var service = new AutomationDefinitionService(catalog.Instance, contracts, catalog.Database, catalog.Lease,
                catalog.Resources, catalog.Index, installed.Roots);
            var cases = new AutomationUseCases(service, new(contracts));
            var caller = new AuthenticatedControlRequest(ControlOrigin.Local, "policy-author", null, () => new(true, true));
            var seed = AutomationDefinitionSeed.Create(Path.Combine(Path.GetDirectoryName(catalog.Instance.RuntimeRoot)!, "shared-policy-source"));
            var template = seed.Scripts[0] with { Kind = AutomationKind.Specialized, ExtensionId = "bettergi", UpdateConfigurationAtFinish = true };
            var valid = seed with { Scripts = [template, template with { Id = Guid.NewGuid(), Name = "Second", Order = 1 },
                template with { Id = Guid.NewGuid(), Name = "Third", Order = 2 }] };
            var invalid = valid with { Scripts = valid.Scripts.SetItem(2, valid.Scripts[2] with { UpdateConfigurationAtFinish = false }) };
            var error = Assert.Throws<AutomationDefinitionException>(() => cases.Change(caller, new(Guid.NewGuid(), invalid)));
            Assert.Equal("automation_specialized_policy_conflict", error.Message);
            Assert.Equal(0, service.Read().Revision);

            _ = cases.Change(caller, new(Guid.NewGuid(), valid));
            var current = service.Read();
            Assert.Equal(3, current.Scripts.Length);
            Assert.All(current.Scripts, script => Assert.True(script.UpdateConfigurationAtFinish));
            var changed = current with { Scripts = current.Scripts.Select(script =>
                script with { Name = script.Name + " imported" }).ToImmutableArray() };
            var incoming = new AutomationBundleCodec(contracts).Encode(changed, DateTimeOffset.UtcNow);
            var importer = new AutomationImportService(catalog.Instance, contracts, catalog.Database, catalog.Lease,
                catalog.Resources, catalog.Index, installed.Roots);
            var roots = current.Roots.ToDictionary(root => root.Id, root => root.Path);
            var operation = Guid.NewGuid();
            var preview = importer.Preview(operation, incoming, roots, current.Revision);
            Assert.True(preview.CanApply);
            var imported = importer.Apply(operation, "policy-author", incoming, roots, current.Revision, preview.PreviewDigest,
                new Dictionary<Guid, string>());
            Assert.Equal(AutomationImportState.Published, imported.State);
            Assert.Equal(current.Revision + 1, service.Read().Revision);
            Assert.All(service.Read().Scripts, script =>
            {
                Assert.EndsWith(" imported", script.Name, StringComparison.Ordinal);
                Assert.True(script.UpdateConfigurationAtFinish);
            });
            Assert.Equal(imported with { IsNew = false }, importer.Apply(operation, "policy-author", incoming, roots,
                current.Revision, preview.PreviewDigest, new Dictionary<Guid, string>()));
            var bound = new NexusPipeline.Platform.Storage.Extensions.ExtensionInstalledEvidence(
                catalog.Instance, catalog.Database, contracts, installed.Roots).ReadCurrent("bettergi")!;
            var extra = Path.Combine(bound.Footprint.TargetPath, "fixture-extra-unlisted.txt");
            IEnumerable<AutomationScript> ChangedNamespace()
            {
                yield return current.Scripts[0];
                File.WriteAllText(extra, "unexpected member");
                yield return current.Scripts[1];
            }
            try
            {
                var changedNamespace = Assert.Throws<IOException>(() =>
                {
                    using var listings = SpecializedScriptPolicy.HoldAndRequire(catalog.Instance, catalog.Database,
                        contracts, installed.Roots, ChangedNamespace());
                });
                Assert.Equal("file_tree_lease_namespace_changed", changedNamespace.Message);
                Assert.True(File.Exists(extra));
            }
            finally { File.Delete(extra); }
            input.VerifyUnchanged();
        }
        finally { await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

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

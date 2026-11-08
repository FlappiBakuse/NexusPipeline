using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BindingSourceTests
{
    [Fact(DisplayName = "First Configuration Selection Is Atomic And Independent For Two Users Of The Same Installed Script")]
    public async Task SelectsIndependentConfigurations()
    {
        await using var fixture = new InstalledBettergiFixture();
        await fixture.InstallAsync();
        var source = fixture.Source;
        var directory = Path.Combine(source.Root, "binding-source");
        Directory.CreateDirectory(Path.Combine(directory, "User", "OneDragon"));
        File.Copy(source.Image, Path.Combine(directory, "BetterGI.exe"));
        foreach (var name in new[] { "first", "second" })
            File.WriteAllText(Path.Combine(directory, "User", "OneDragon", name + ".json"),
                "{\"Name\":\"" + name + "\",\"TaskEnabledList\":{\"领取邮件\":true},\"CompletionAction\":\"无\"}");
        File.WriteAllText(Path.Combine(directory, "User", "config.json"), "{\"selectedOneDragonFlowConfigName\":\"first\"}");
        var originals = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var catalog = source.Definitions.Read();
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Binding selection source", AutomationRootKind.Script, directory);
        var prior = catalog.Scripts.Single(item => item.Id == source.OtherScriptId);
        var access = new ConfigurationDocumentAccess([new(new([]), true)], [new(new([]), true)], []);
        var script = prior with { RootId = root.Id, Executable = new(root.Id, "BetterGI.exe"), Arguments = ["startOneDragon", "first"],
            ConfigurationPaths = [new(root.Id, "User/OneDragon/first.json"), new(root.Id, "User/config.json")],
            LogPaths = [], AdapterResources = [new("one-dragon", 0, "", 1, ConfigurationSyntax.Json, access),
                new("extra-user-config", 1, "", 1, ConfigurationSyntax.Json, access)] };
        var first = new AutomationBinding(Guid.NewGuid(), 0, source.UserA, script.Id, false, 0);
        var second = new AutomationBinding(Guid.NewGuid(), 0, source.UserC, script.Id, false, 0);
        var changes = new AutomationChanges(catalog.Revision, [root], [script], [], [first, second], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "binding-source", changes), changes, DateTimeOffset.UtcNow);
        catalog = source.Definitions.Read(); script = catalog.Scripts.Single(item => item.Id == script.Id);
        var installed = new ExtensionInstalledEvidence(source.Instance, source.Database, new(), fixture.Installation.Roots).ReadCurrent("bettergi")!;
        var imports = new ConfigurationImportService(source.Instance, source.Database, new(), source.Lease, fixture.Execution.Resources);
        var sources = new ScriptSourceService(source.Instance, source.Database, source.Lease, fixture.Execution.Resources, new(),
            fixture.Installation.Roots, fixture.Installation.Activator);
        var service = new BindingSourceImport(source.Database, sources, imports);
        ConfigurationImportRequest Request(Guid binding, string name) => new(Guid.NewGuid(), binding, 1, script.Revision,
            [new("one-dragon", 0), new("extra-user-config", 0)])
            { CandidateId = "User/OneDragon/" + name + ".json", ExpectedInstalledRevision = installed.Metadata.Revision };
        var a = Request(first.Id, "first"); var b = Request(second.Id, "second");
        await Assert.ThrowsAsync<ConfigurationException>(() => service.ImportAsync("binding-source", a with { CandidateId = "missing" }, () => {}, default).AsTask());
        Assert.Null(new ConfigurationRevisionStore(source.Instance, source.Database, new()).Read(new(first.Id, BindingConfigurationSource.ResourceId)));
        Assert.True((await service.ImportAsync("binding-source", a, () => {}, default)).IsNew);
        Assert.True((await service.ImportAsync("binding-source", b, () => {}, default)).IsNew);
        Assert.False((await service.ImportAsync("binding-source", a, () => {}, default)).IsNew);
        await Assert.ThrowsAsync<ConfigurationException>(() => service.ImportAsync("binding-source", Request(first.Id, "second"), () => {}, default).AsTask());
        var saved = source.Definitions.Read();
        var firstBinding = saved.Bindings.Single(item => item.Id == first.Id);
        var secondBinding = saved.Bindings.Single(item => item.Id == second.Id);
        var firstScript = BindingScriptSource.Resolve(script, firstBinding);
        var secondScript = BindingScriptSource.Resolve(script, secondBinding);
        Assert.Equal("first", firstScript.Arguments.Last()); Assert.Equal("second", secondScript.Arguments.Last());
        Assert.EndsWith("first.json", firstScript.ConfigurationPaths[0].RelativePath);
        Assert.EndsWith("second.json", secondScript.ConfigurationPaths[0].RelativePath);
        var views = new ConfigurationOverviewReader(source.Instance, source.Database, source.Lease, new());
        Assert.Contains(views.Read(first.Id).Resources, item => item.Label.EndsWith("first.json"));
        Assert.Contains(views.Read(second.Id).Resources, item => item.Label.EndsWith("second.json"));
        var wire = new ContractReader().Read<NexusPipeline.Platform.Serialization.Protocol.AutomationBundle>(
            new AutomationBundleCodec(new()).Encode(saved, DateTimeOffset.UtcNow), "automation-bundle.schema.json");
        var frozen = BindingSourceCodec.Resolve(wire.Scripts.Single(item => item.Id == script.Id.ToString("D")),
            wire.Bindings.Single(item => item.Id == second.Id.ToString("D")).Source);
        Assert.Equal("second", frozen.Arguments.Last());
        foreach (var item in originals) Assert.Equal(item.Value, File.ReadAllBytes(item.Key));
        fixture.Inputs.VerifyUnchanged();
    }
}

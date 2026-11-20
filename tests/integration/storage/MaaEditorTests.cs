using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MaaEditorTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PreservesEditorLifecycle(bool save, bool missingInstance)
    {
        var set = Environment.GetEnvironmentVariable("NEXUS_PACKAGED_SET") ?? throw new IOException("package_set_required");
        using var packages = JsonDocument.Parse(File.ReadAllBytes(set));
        var input = Assert.Single(packages.RootElement.GetProperty("runs").EnumerateArray(), item => item.GetProperty("label").GetString() == "maas-package");
        var receipt = input.GetProperty("receipt").GetString()!;
        Assert.Equal(input.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(receipt))));
        using var package = new BuiltExtensionInputs("maas", receipt, explicitPath: true);
        using var fixture = new ExecutionAdmissionFixture(saved: false);
        await using var installed = await InstalledEditorPackage.CreateAsync(fixture, package);
        var source = fixture.Source;
        var directory = Path.Combine(source.Root, "maas-editor");
        Directory.CreateDirectory(Path.Combine(directory, "config", "instances"));
        InstalledEditorPackage.PrepareImage(directory, "MFAAvalonia.exe");
        File.WriteAllText(Path.Combine(directory, "interface.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "config/config.json"), "{}");
        if (!missingInstance) File.WriteAllText(Path.Combine(directory, "config/instances/A.json"), "{}");
        var settings = Path.Combine(directory, "appsettings.json");
        var original = "{\"NoAutoStart\":\"False\",\"ordinary\":\"initial\"}"u8.ToArray();
        File.WriteAllBytes(settings, original);
        var selector = new ConfigurationSelector([new ConfigurationProperty("NoAutoStart")]);
        var resource = new AutomationAdapterResource("settings", 0, "", 1, ConfigurationSyntax.Json,
            new([new(selector, false)], [new(selector, false)], []));
        var catalog = source.Definitions.Read();
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Controlled Maa editor", AutomationRootKind.Script, directory);
        var script = catalog.Scripts[0] with { Id = Guid.NewGuid(), Revision = 0, Kind = AutomationKind.Specialized,
            RootId = root.Id, Executable = new(root.Id, "MFAAvalonia.exe"), Arguments = ["--instance", "A"],
            ConfigurationPaths = [new(root.Id, "appsettings.json")], LogPaths = [],
            ExtensionId = "maas", AdapterResources = [resource], UpdateConfigurationAtFinish = true };
        var binding = new AutomationBinding(Guid.NewGuid(), 0, source.UserA, script.Id, true, 0);
        var changes = new AutomationChanges(catalog.Revision, [root], [script], [], [binding], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "maa-editor", changes), changes, DateTimeOffset.UtcNow);
        script = source.Definitions.Read().Scripts.Single(item => item.Id == script.Id);
        resource = Assert.Single(script.AdapterResources);
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var tree = new FileTreeArchive(new(SnapshotBudget.Default), blobs).Capture(settings, default).Snapshot;
        var saved = new ConfigurationRevisionChange(new(binding.Id, resource.ResourceId), 0, 1,
            AdapterConfigurationPolicy.Contract(script, resource), tree, ConfigurationUpdateKind.Manual);
        new ConfigurationRevisionStore(source.Instance, source.Database, new()).Commit(
            ConfigurationRevisionStore.Identity(Guid.NewGuid(), "maa-editor", saved), saved, DateTimeOffset.UtcNow);
        var contracts = new ContractReader();
        using var controller = new ConfigurationEditController(source.Instance, source.Database, contracts, source.Lease,
            fixture.Resources, fixture.Index, activator: installed.Activator);
        var native = new NativeEditorService(source.Instance, source.Database, contracts, source.Lease, fixture.Resources, controller);
        var sources = new ScriptSourceService(source.Instance, source.Database, source.Lease, fixture.Resources, contracts,
            source.PayloadRoots, installed.Activator);
        var service = new BindingEditorService(source.Instance, source.Database, contracts, fixture.Resources, controller, native, sources);
        var selection = new BindingEditorSelection(Guid.NewGuid(), binding.Id, 1, script.Revision, null);
        try
        {
            var opened = Assert.IsType<ConfigurationEditStatus>((await service.OpenAsync("maa-editor", selection, () => { }, default)).Session);
            Assert.Equal("{\"NoAutoStart\":\"True\",\"ordinary\":\"initial\"}", File.ReadAllText(settings));
            File.WriteAllText(settings, "{\"NoAutoStart\":\"True\",\"ordinary\":\"edited\"}");
            if (save) Assert.NotNull((await controller.SaveBindingAsync(Guid.NewGuid(), "maa-editor", selection.SessionId,
                opened.Revision, null, DateTimeOffset.UtcNow)).Saved);
            else await controller.CancelAsync(Guid.NewGuid(), "maa-editor", selection.SessionId, opened.Revision, DateTimeOffset.UtcNow);
            Assert.Equal(original, File.ReadAllBytes(settings));
            var current = new ConfigurationRevisionStore(source.Instance, source.Database, new()).Read(new(binding.Id, resource.ResourceId))!;
            Assert.Equal(save ? 2 : 1, current.Revision);
            var entry = Assert.Single(current.Tree.Entries);
            using var bytes = blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
            using var value = JsonDocument.Parse(bytes);
            Assert.Equal("False", value.RootElement.GetProperty("NoAutoStart").GetString());
            Assert.Equal(save ? "edited" : "initial", value.RootElement.GetProperty("ordinary").GetString());
            package.VerifyUnchanged();
        }
        finally { await controller.StopAsync(TimeSpan.FromSeconds(15)); }
    }
}

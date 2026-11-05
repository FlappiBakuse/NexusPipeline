using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MaaEditorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesEditorLifecycle(bool save)
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
        var original = "{\"NoAutoStart\":\"False\",\"ordinary\":\"initial\"}"u8.ToArray();
        File.WriteAllBytes(source.Configuration, original);
        var selector = new ConfigurationSelector([new ConfigurationProperty("NoAutoStart")]);
        var resource = new AutomationAdapterResource("settings", 0, "", 1, ConfigurationSyntax.Json,
            new([new(selector, false)], [new(selector, false)], []));
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var catalog = source.Definitions.Read();
        var script = catalog.Scripts[0] with { Id = Guid.NewGuid(), Revision = 0, Kind = AutomationKind.Specialized,
            ExtensionId = "maas", AdapterResources = [resource], UpdateConfigurationAtFinish = true };
        var binding = new AutomationBinding(Guid.NewGuid(), 0, source.UserA, script.Id, true, 0);
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Controlled Maa editor", AutomationRootKind.External, Path.GetDirectoryName(executable)!);
        var changes = new AutomationChanges(catalog.Revision, [root], [script], [], [binding], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "maa-editor", changes), changes, DateTimeOffset.UtcNow);
        script = source.Definitions.Read().Scripts.Single(item => item.Id == script.Id);
        resource = Assert.Single(script.AdapterResources);
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var tree = new FileTreeArchive(new(SnapshotBudget.Default), blobs).Capture(source.Configuration, default).Snapshot;
        var saved = new ConfigurationRevisionChange(new(binding.Id, resource.ResourceId), 0, 1,
            AdapterConfigurationPolicy.Contract(script, resource), tree, ConfigurationUpdateKind.Manual);
        new ConfigurationRevisionStore(source.Instance, source.Database, new()).Commit(
            ConfigurationRevisionStore.Identity(Guid.NewGuid(), "maa-editor", saved), saved, DateTimeOffset.UtcNow);
        var inputPath = Path.Combine(source.Root, "maa-editor.json"); var report = inputPath + ".ready";
        File.WriteAllBytes(inputPath, JsonSerializer.SerializeToUtf8Bytes(new { operations = new[] { new {
            target = source.Configuration, kind = "file", expectedBefore = "{\"NoAutoStart\":\"True\",\"ordinary\":\"initial\"}",
            value = "{\"NoAutoStart\":\"True\",\"ordinary\":\"edited\"}" } } }));
        using var controller = new ConfigurationEditController(source.Instance, source.Database, new(), source.Lease,
            fixture.Resources, fixture.Index, activator: installed.Activator);
        var service = new NativeEditorService(source.Instance, source.Database, new(), source.Lease, fixture.Resources, controller);
        var selection = new GenericEditorSelection(Guid.NewGuid(), binding.Id, 1, script.Revision, root.Id, 1,
            Path.GetFileName(executable), ["configuration-editor-writer", inputPath, report], [new(resource.ResourceId, 1)]);
        try
        {
            var opened = await service.BeginAsync("maa-editor", selection, DateTimeOffset.UtcNow);
            var pid = (await OwnedCrashProcess.ReadReportAsync(report)).GetProperty("processId").GetInt32();
            using var process = Process.GetProcessById(pid);
            if (save) await controller.SaveAsync(Guid.NewGuid(), "maa-editor", selection.SessionId, opened.Revision, DateTimeOffset.UtcNow);
            else await controller.CancelAsync(Guid.NewGuid(), "maa-editor", selection.SessionId, opened.Revision, DateTimeOffset.UtcNow);
            Assert.True(process.HasExited);
            Assert.Equal(original, File.ReadAllBytes(source.Configuration));
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

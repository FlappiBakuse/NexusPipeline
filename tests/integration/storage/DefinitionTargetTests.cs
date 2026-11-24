using AutomationTarget = NexusPipeline.Core.Automation.AutomationTarget;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DefinitionTargetTests
{
    [Theory(DisplayName = "Target Definitions Persist And Template Imports Remap Every Independent Target Root")]
    [InlineData("pc")]
    [InlineData("genericAdb")]
    [InlineData("mumu")]
    [InlineData("mumu-same-root")]
    [InlineData("mumu-separate-root")]
    [InlineData("generic-emulator")]
    public async Task RemapsTargetRoots(string kind)
    {
        var fixture = Create(kind);
        var catalog = fixture.Store.Read();
        var target = fixture.Target;
        Assert.Equal(JsonSerializer.Serialize(target), JsonSerializer.Serialize(Assert.Single(catalog.Scripts).Target));
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "inspection"), new JournalStartupAdmission(fixture.Instance, new()));
        Assert.Equal(JsonSerializer.Serialize(target), JsonSerializer.Serialize(Assert.Single(new AutomationDefinitionStore(reopened).Read().Scripts).Target));
        var codec = new AutomationBundleCodec(new());
        var bytes = codec.Encode(catalog, DateTimeOffset.Parse("2026-11-01T00:00:00Z"), catalog.Scripts[0].Id);
        var destination = Path.Combine(fixture.Root, "destination");
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(destination, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var roots = catalog.Roots.ToDictionary(value => value.Id, value =>
        {
            var selected = Path.Combine(destination, "sources", value.Id.ToString("N"));
            Directory.CreateDirectory(selected);
            foreach (var file in Directory.GetFiles(value.Path, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(selected, Path.GetRelativePath(value.Path, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            return selected;
        });
        using var lease = InstanceLease.Acquire(instance, new());
        var index = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(destination, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        var resources = new ResourceLeaseManager(new(index, TimeProvider.System));
        var importer = new AutomationImportService(instance, new(), database, lease, resources, index);
        var operation = Guid.Parse("65421823-9708-45a5-91c6-0028818760dc");
        var preview = importer.Preview(operation, bytes, roots, 0);
        Assert.True(preview.CanApply);
        var imported = Assert.Single(preview.Bundle.Definitions.Scripts);
        Assert.DoesNotContain(preview.Bundle.Definitions.Roots, root => roots.ContainsKey(root.Id));
        var wire = JsonNode.Parse(bytes)!["scripts"]![0]!["target"]!;
        Assert.Equal(target.Pc is not null ? "pc" : "android", wire["kind"]!.GetValue<string>());
        if (wire["android"] is JsonObject androidWire)
        {
            Assert.Equal(target.Android!.Driver == AutomationAndroidDriver.Mumu ? "mumu" : "genericAdb", androidWire["driver"]!.GetValue<string>());
            Assert.Equal(target.Android.ScreenshotTool is not null, androidWire.ContainsKey("screenshotTool"));
            Assert.Equal(target.Android.Emulator is not null, androidWire.ContainsKey("emulator"));
        }
        AssertTarget(preview.Bundle.Definitions, imported.Target!);
        var store = new AutomationDefinitionStore(database);
        var empty = new Dictionary<Guid, string>();
        var applied = importer.Apply(operation, "fixture", bytes, roots, 0, preview.PreviewDigest, empty);
        Assert.True(applied.IsNew);
        Assert.Equal(AutomationImportState.Published, applied.State);
        Assert.Equal(1L, applied.CatalogRevision);
        Assert.Equal(applied with { IsNew = false }, importer.Apply(operation, "fixture", bytes, roots, 0, preview.PreviewDigest, empty));
        AssertTarget(preview.Bundle.Definitions, Assert.Single(store.Read().Scripts).Target!);

        var nextOperation = Guid.Parse("e7140330-02dc-469b-81ae-45ce959a11d0");
        var next = importer.Preview(nextOperation, bytes, roots, 1);
        var nextScript = Assert.Single(next.Bundle.Definitions.Scripts);
        Assert.NotEqual(imported.Id, nextScript.Id);
        Assert.DoesNotContain(next.Bundle.Definitions.Roots, root => preview.Bundle.Definitions.Roots.Any(first => first.Id == root.Id));
        AssertTarget(next.Bundle.Definitions, nextScript.Target!);
        Assert.Equal(AutomationImportState.Published,
            importer.Apply(nextOperation, "fixture", bytes, roots, 1, next.PreviewDigest, empty).State);
        var saved = store.Read();
        Assert.Equal(2, saved.Scripts.Length);
        var actual = saved.Scripts.Single(script => script.Id == nextScript.Id).Target!;
        AssertTarget(next.Bundle.Definitions, actual);
        if (actual.Android is { } android)
        {
            var knownRoots = saved.Roots.ToDictionary(root => root.Id);
            var blobs = new ImmutableBlobStore(instance, new());
            var tool = Path.Combine(knownRoots[android.Tool.RootId].Path, android.Tool.RelativePath);
            using var discovery = android.Driver == AutomationAndroidDriver.Mumu
                ? PreparedMuMuDiscovery.Prepare(instance, new(), lease, resources, tool, android.Endpoint)
                : null;
            if (discovery is not null) await discovery.DiscoverAsync(TimeSpan.FromSeconds(15));

            NexusPipeline.Platform.Serialization.Protocol.EmulatorTargetFrozen? preparedEmulator = null;
            if (android.Emulator is { } selection)
            {
                var directory = knownRoots[selection.RootId].Path;
                var physical = PhysicalObjectIdentity.Read(directory);
                // The vendor owner is a fixture here. The runtime binding still comes from the saved script.
                var evidence = blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(new { fixture = "prepared-emulator" }));
                preparedEmulator = new()
                {
                    Selection = AutomationTargetCodec.ToWire(actual)!.Android!.Emulator!,
                    ExtensionId = "fixture-provider", ProviderId = "generic-adb",
                    InstalledRevision = 1, PackageSha256 = new('c', 64), ActivationId = Guid.NewGuid().ToString("D"),
                    InstallationId = "fixture-installation", InstanceDirectory = directory,
                    InstancePhysicalId = new() { VolumeSerial = physical.VolumeSerial, FileId = physical.FileId },
                    Control = ExecutionHookImages.Capture(tool, runtime: false),
                    Launch = new() { Arguments = [], Detached = true },
                    SourceEvidence = new() { RelativePath = evidence.RelativePath, SizeBytes = evidence.SizeBytes, Sha256 = evidence.Sha256 }
                };
            }
            var frozen = ExecutionTargetInputs.Capture(actual, knownRoots, blobs, instance.InstanceId, discovery, preparedEmulator)!.Android!;
            var expected = fixture.Target.Android!;
            Assert.Equal(Path.Combine(roots[expected.Tool.RootId], expected.Tool.RelativePath), frozen.Tool.Path);
            Assert.Equal(expected.ScreenshotTool is { } screenshot
                ? Path.Combine(roots[screenshot.RootId], screenshot.RelativePath) : null, frozen.ScreenshotTool?.Path);
            Assert.Equal(expected.Emulator?.TargetId, frozen.Emulator?.Selection.TargetId);
            Assert.Equal(expected.Emulator is { } emulator ? roots[emulator.RootId] : null, frozen.Emulator?.InstanceDirectory);
            if (discovery is not null) Assert.Equal(16384, frozen.Mapping!.EndpointPort);
        }

        void AssertTarget(AutomationChanges definition, AutomationTarget actual)
        {
            Guid MappedRoot(Guid original) => Assert.Single(definition.Roots, root => root.Path == roots[original]).Id;
            AutomationPath MappedPath(AutomationPath original) => new(MappedRoot(original.RootId), original.RelativePath);
            Assert.Equal(target.Kind, actual.Kind);
            Assert.Equal(target.LaunchByHost, actual.LaunchByHost);
            Assert.Equal(target.WaitSeconds, actual.WaitSeconds);
            Assert.Equal(target.ForceCloseAtEnd, actual.ForceCloseAtEnd);
            if (target.Pc is { } pc)
            {
                Assert.Null(actual.Android);
                var actualPc = Assert.IsType<AutomationPcTarget>(actual.Pc);
                Assert.Equal(MappedPath(pc.LaunchExecutable), actualPc.LaunchExecutable);
                Assert.Equal(MappedPath(pc.ReadyExecutable), actualPc.ReadyExecutable);
                Assert.Equal(pc.ReadinessMode, actualPc.ReadinessMode);
                Assert.Equal(pc.Arguments.ToArray(), actualPc.Arguments.ToArray());
            }
            else
            {
                Assert.Null(actual.Pc);
                var original = Assert.IsType<AutomationAndroidTarget>(target.Android);
                var android = Assert.IsType<AutomationAndroidTarget>(actual.Android);
                Assert.Equal(original.Driver, android.Driver);
                Assert.Equal(MappedPath(original.Tool), android.Tool);
                Assert.Equal(original.Endpoint, android.Endpoint);
                Assert.Equal(original.Package, android.Package);
                Assert.Equal(original.StartArguments.ToArray(), android.StartArguments.ToArray());
                Assert.Equal(original.ScreenshotTool is { } screenshot ? MappedPath(screenshot) : null, android.ScreenshotTool);
                Assert.Equal(original.Emulator is { } emulator ? emulator with { RootId = MappedRoot(emulator.RootId) } : null, android.Emulator);
                if (android.Emulator is { } selected) Assert.Equal(android.Tool.RootId, selected.RootId);
            }
        }
    }

    [Theory(DisplayName = "The Bundle Reader Rejects Undeclared Screenshot And Emulator Roots")]
    [InlineData("mumu-separate-root", "screenshotTool")]
    [InlineData("generic-emulator", "emulator")]
    public void RejectsUnlistedOptionalRoot(string kind, string field)
    {
        var catalog = Create(kind).Store.Read();
        var bytes = new AutomationBundleCodec(new()).Encode(catalog, DateTimeOffset.Parse("2026-11-01T00:00:00Z"), catalog.Scripts[0].Id);
        var reader = new AutomationBundleReader(new());
        Assert.Single(reader.Read(bytes).Scripts);
        var document = JsonNode.Parse(bytes)!;
        document["scripts"]![0]!["target"]!["android"]![field]!["rootId"] = "399adc4c-3f18-45bb-92f5-337c544f3964";
        var rejected = Assert.Throws<JsonException>(() => reader.Read(JsonSerializer.SerializeToUtf8Bytes(document)));
        Assert.Equal("unresolved_definition_reference", rejected.Message);
    }

    [Theory(DisplayName = "Unknown Persisted Target Facts Reject Copied Startup Without Rewriting Original Bytes")]
    [InlineData("unknown")]
    [InlineData("mixed")]
    [InlineData("missing")]
    public void PreservesUnknownTargets(string damage)
    {
        var fixture = Create("pc");
        var target = Assert.Single(fixture.Store.Read().Scripts).Target;
        var document = JsonSerializer.SerializeToNode(target)!.AsObject();
        if (damage == "unknown") document["unexpectedAuthority"] = true;
        if (damage == "mixed") document["Android"] = JsonSerializer.SerializeToNode(new AutomationAndroidTarget(AutomationAndroidDriver.GenericAdb,
            fixture.Store.Read().Scripts[0].Executable, "127.0.0.1:5555", "test.game", []));
        if (damage == "missing") document.Remove("WaitSeconds");
        using (var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open($"Data Source={fixture.Database.DatabasePath};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE automation.automations SET target_json=$target;";
            command.Parameters.AddWithValue("$target", document.ToJsonString()); command.ExecuteNonQuery();
        }
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        var error = Record.Exception(() => StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "invalid-inspection"),
            new JournalStartupAdmission(fixture.Instance, new())));
        Assert.True(error is IOException or AutomationDefinitionException, error?.ToString() ?? "Invalid target was accepted");
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    private static Fixture Create(string kind)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "automation-target-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var store = new AutomationDefinitionStore(database);
        var definitions = AutomationDefinitionSeed.Create(root);
        var path = Path.Combine(root, "game"); Directory.CreateDirectory(path);
        var game = new AutomationRoot(Guid.NewGuid(), 0, "Game", AutomationRootKind.External, path);
        foreach (var file in new[] { "launch.cmd", "game.exe", "adb.exe" }) File.WriteAllText(Path.Combine(path, file), "fixture bytes");
        var isMumu = kind.StartsWith("mumu", StringComparison.Ordinal);
        var toolName = "adb.exe";
        if (isMumu)
        {
            var original = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_image_missing");
            toolName = Path.GetFileName(original);
            File.Copy(original, Path.Combine(path, toolName));
            foreach (var asset in ExecutionHookImages.Capture(original, runtime: true).RuntimeFiles)
            {
                var copy = Path.Combine(path, asset.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(Path.Combine(Path.GetDirectoryName(original)!, asset.RelativePath), copy);
            }
            File.WriteAllBytes(Path.Combine(path, "mumu-discovery-fixture.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { mode = "valid", port = 16384 }));
        }
        AutomationPath? screenshotTool = null;
        if (kind is "mumu-same-root" or "mumu-separate-root")
        {
            var screenshotRoot = game;
            if (kind == "mumu-separate-root")
            {
                var helper = Directory.CreateDirectory(Path.Combine(root, "capture-tools"));
                screenshotRoot = new(Guid.NewGuid(), 0, "Screenshot tools", AutomationRootKind.External, helper.FullName);
                definitions = definitions with { Roots = definitions.Roots.Add(screenshotRoot) };
            }
            File.WriteAllText(Path.Combine(screenshotRoot.Path, "capture.exe"), "fixture screenshot tool");
            screenshotTool = new(screenshotRoot.Id, "capture.exe");
        }
        var target = kind == "pc" ? new AutomationTarget(AutomationTargetKind.Pc, true, 30, false,
            new(new(game.Id, "launch.cmd"), ["one argument"], new(game.Id, "game.exe"), AutomationPcReadiness.VisibleWindow), null) :
            new(AutomationTargetKind.Android, true, 30, true, null,
                new(isMumu ? AutomationAndroidDriver.Mumu : AutomationAndroidDriver.GenericAdb,
                    new(game.Id, toolName), isMumu ? "127.0.0.1:16384" : "127.0.0.1:5555", "test.game", ["-n", "test.game/.MainActivity"])
                {
                    ScreenshotTool = screenshotTool,
                    Emulator = kind == "generic-emulator" ? new("fixture-emulator", game.Id, new string('a', 64), new string('b', 64))
                        { ExtensionId = "fixture-provider", ProviderId = "generic-adb" } : null
                });
        definitions = definitions with { Roots = definitions.Roots.Add(game), Scripts = [definitions.Scripts[0] with { Target = target }] };
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", definitions), definitions, DateTimeOffset.UtcNow);
        return new(root, instance, database, store, target);
    }

    private static string[] Fingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private sealed record Fixture(string Root, RuntimeInstance Instance, StateDatabase Database, AutomationDefinitionStore Store, AutomationTarget Target);
}

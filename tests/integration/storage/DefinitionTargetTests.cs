using AutomationTarget = NexusPipeline.Core.Automation.AutomationTarget;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
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
    public void RemapsTargetRoots(string kind)
    {
        var fixture = Create(kind);
        var catalog = fixture.Store.Read();
        var target = Assert.Single(catalog.Scripts).Target!;
        var reopened = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "inspection"), new JournalStartupAdmission(fixture.Instance, new()));
        Assert.Equal(JsonSerializer.Serialize(target), JsonSerializer.Serialize(Assert.Single(new AutomationDefinitionStore(reopened).Read().Scripts).Target));
        var codec = new AutomationBundleCodec(new());
        var bytes = codec.Encode(catalog, DateTimeOffset.UtcNow, catalog.Scripts[0].Id);
        var destination = Path.Combine(fixture.Root, "destination");
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(destination, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var roots = catalog.Roots.ToDictionary(value => value.Id, value =>
        {
            var selected = Path.Combine(destination, "sources", value.Id.ToString("N"));
            Directory.CreateDirectory(selected);
            foreach (var file in Directory.GetFiles(value.Path)) File.Copy(file, Path.Combine(selected, Path.GetFileName(file)));
            return selected;
        });
        var preview = new AutomationImportPreviewer(instance, new(), database).Preview(Guid.NewGuid(), bytes, roots, 0);
        Assert.True(preview.CanApply);
        var imported = Assert.Single(preview.Bundle.Definitions.Scripts);
        var originalRoots = AutomationTargetPolicy.Paths(target).Select(value => value.RootId).ToHashSet();
        Assert.All(AutomationTargetPolicy.Paths(imported.Target), path =>
        {
            Assert.DoesNotContain(path.RootId, originalRoots);
            Assert.Contains(preview.Bundle.Definitions.Roots, root => root.Id == path.RootId);
        });
        Assert.Equal(kind, JsonNode.Parse(bytes)!["scripts"]![0]!["target"]!["kind"]!.GetValue<string>() == "pc" ? "pc" :
            JsonNode.Parse(bytes)!["scripts"]![0]!["target"]!["android"]!["driver"]!.GetValue<string>());
        var store = new AutomationDefinitionStore(database);
        store.Commit(AutomationDefinitionStore.Identity(preview.OperationId, "fixture", preview.Bundle.Definitions), preview.Bundle.Definitions, DateTimeOffset.UtcNow);
        Assert.Equal(JsonSerializer.Serialize(imported.Target), JsonSerializer.Serialize(Assert.Single(store.Read().Scripts).Target));
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
        var target = kind == "pc" ? new AutomationTarget(AutomationTargetKind.Pc, true, 30, false,
            new(new(game.Id, "launch.cmd"), ["one argument"], new(game.Id, "game.exe"), AutomationPcReadiness.VisibleWindow), null) :
            new(AutomationTargetKind.Android, true, 30, true, null,
                new(kind == "mumu" ? AutomationAndroidDriver.Mumu : AutomationAndroidDriver.GenericAdb,
                    new(game.Id, "adb.exe"), "127.0.0.1:5555", "test.game", ["-n", "test.game/.MainActivity"]));
        definitions = definitions with { Roots = definitions.Roots.Add(game), Scripts = [definitions.Scripts[0] with { Target = target }] };
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", definitions), definitions, DateTimeOffset.UtcNow);
        return new(root, instance, database, store);
    }

    private static string[] Fingerprints(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private sealed record Fixture(string Root, RuntimeInstance Instance, StateDatabase Database, AutomationDefinitionStore Store);
}

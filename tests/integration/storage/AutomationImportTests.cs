using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class AutomationImportTests
{
    [Fact(DisplayName = "Definitions Export Into Another New Instance Using Only Its Explicitly Selected Roots")]
    public void RemapsExplicitRoots()
    {
        var source = Create(populate: true);
        var target = Create(populate: false);
        var catalog = source.Store.Read();
        var bytes = new AutomationBundleCodec(new()).Encode(catalog, DateTimeOffset.UtcNow);
        var roots = SelectedRoots(catalog, target.Root, "selected");
        var before = Fingerprints(target.Instance.RuntimeRoot);
        var previewer = new AutomationImportPreviewer(target.Instance, new(), target.Database);
        var operation = Guid.NewGuid();
        var preview = previewer.Preview(operation, bytes, roots, 0);
        Assert.True(preview.CanApply);
        Assert.Equal(before, Fingerprints(target.Instance.RuntimeRoot));
        var same = previewer.Preview(operation, bytes, roots, 0);
        AutomationImportPreviewer.RequireSamePreview(same, preview.InputDigest, preview.PreviewDigest);
        Assert.Empty(same.Bundle.Attachments);
        var result = target.Store.Commit(AutomationDefinitionStore.Identity(operation, "fixture", same.Bundle.Definitions), same.Bundle.Definitions, DateTimeOffset.UtcNow);
        Assert.True(result.IsNew);
        Assert.NotEqual(before, Fingerprints(target.Instance.RuntimeRoot));
        var imported = target.Store.Read();
        Assert.Equal(catalog.Scripts.Single().Id, imported.Scripts.Single().Id);
        Assert.Equal(roots.Single().Value, imported.Roots.Single().Path);
        Assert.Equal(catalog.Queues.Single().ScriptIds.ToArray(), imported.Queues.Single().ScriptIds.ToArray());
        Assert.Equal(2, imported.Users.Length);
        Assert.All(imported.Bindings, value => Assert.False(value.IsEnabled));
    }

    [Fact(DisplayName = "A Generic Template Creates Independent Ids While Each Requests Preview And Replay Remain Stable")]
    public void PreservesTemplateReplay()
    {
        var source = Create(populate: true);
        var target = Create(populate: false);
        var catalog = source.Store.Read();
        var script = catalog.Scripts.Single();
        var bytes = new AutomationBundleCodec(new()).Encode(catalog, DateTimeOffset.UtcNow, script.Id);
        var previewer = new AutomationImportPreviewer(target.Instance, new(), target.Database);
        var firstOperation = Guid.NewGuid();
        var firstRoots = SelectedRoots(catalog, target.Root, "first");
        var first = previewer.Preview(firstOperation, bytes, firstRoots, 0);
        var repeated = previewer.Preview(firstOperation, bytes, firstRoots, 0);
        Assert.Equal(first.PreviewDigest, repeated.PreviewDigest);
        Assert.NotEqual(script.Id, first.Bundle.Definitions.Scripts.Single().Id);
        Assert.Empty(first.Bundle.Definitions.Users);
        var identity = AutomationDefinitionStore.Identity(firstOperation, "fixture", first.Bundle.Definitions);
        target.Store.Commit(identity, first.Bundle.Definitions, DateTimeOffset.UtcNow);
        var second = previewer.Preview(Guid.NewGuid(), bytes, SelectedRoots(catalog, target.Root, "second"), 1);
        target.Store.Commit(AutomationDefinitionStore.Identity(second.OperationId, "fixture", second.Bundle.Definitions), second.Bundle.Definitions, DateTimeOffset.UtcNow);
        Assert.NotEqual(first.Bundle.Definitions.Scripts.Single().Id, second.Bundle.Definitions.Scripts.Single().Id);
        var replay = target.Store.Commit(identity, first.Bundle.Definitions, DateTimeOffset.UtcNow);
        Assert.False(replay.IsNew);
        Assert.Equal(1, replay.CatalogRevision);
        Assert.Equal(2, target.Store.Read().Scripts.Length);
        Assert.Empty(target.Store.Read().Users);
        Assert.Empty(target.Store.Read().Bindings);
        var exported = new AutomationBundleCodec(new()).Encode(target.Store.Read(), DateTimeOffset.UtcNow);
        using var checkedIds = new ContractReader().Read(exported, "automation-bundle.schema.json");
    }

    [Fact(DisplayName = "Replacing A Selected Directory With Equal File Bytes Invalidates The Earlier Preview")]
    public void RejectsReplacedRoots()
    {
        var source = Create(populate: true);
        var target = Create(populate: false);
        var catalog = source.Store.Read();
        var bytes = new AutomationBundleCodec(new()).Encode(catalog, DateTimeOffset.UtcNow);
        var roots = SelectedRoots(catalog, target.Root, "selected");
        var previewer = new AutomationImportPreviewer(target.Instance, new(), target.Database);
        var operation = Guid.NewGuid();
        var preview = previewer.Preview(operation, bytes, roots, 0);
        var selected = roots.Single().Value;
        Directory.Move(selected, selected + ".original");
        Directory.CreateDirectory(selected);
        File.WriteAllBytes(Path.Combine(selected, "run.exe"), [1, 2, 3]);
        var changed = previewer.Preview(operation, bytes, roots, 0);
        Assert.NotEqual(preview.Roots[0].Identity, changed.Roots[0].Identity);
        Assert.Equal("automation_import_preview_changed", Assert.Throws<AutomationDefinitionException>(() =>
            AutomationImportPreviewer.RequireSamePreview(changed, preview.InputDigest, preview.PreviewDigest)).Message);
        Assert.Equal(0, target.Store.Read().Revision);
    }

    [Theory(DisplayName = "Invalid Exchange Bytes Are Rejected Before Changing The Target Instance")]
    [InlineData("version")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-id")]
    [InlineData("legacy")]
    public void PreservesInvalidImports(string fault)
    {
        var source = Create(populate: true);
        var target = Create(populate: false);
        var catalog = source.Store.Read();
        var bytes = new AutomationBundleCodec(new()).Encode(catalog, DateTimeOffset.UtcNow);
        var roots = SelectedRoots(catalog, target.Root, "selected");
        var previewer = new AutomationImportPreviewer(target.Instance, new(), target.Database);
        Assert.True(previewer.Preview(Guid.NewGuid(), bytes, roots, 0).CanApply);
        var document = JsonNode.Parse(bytes)!;
        if (fault == "version") document["version"] = 19;
        if (fault == "unknown-field") document["unexpected"] = true;
        if (fault == "duplicate-id") document["scripts"]!.AsArray().Add(document["scripts"]![0]!.DeepClone());
        bytes = fault == "legacy" ? Encoding.UTF8.GetBytes("{\"Name\":\"old\",\"FileName\":\"run.exe\"}") : JsonSerializer.SerializeToUtf8Bytes(document);
        var before = Fingerprints(target.Instance.RuntimeRoot);
        if (fault == "duplicate-id") Assert.Throws<AutomationDefinitionException>(() => previewer.Preview(Guid.NewGuid(), bytes, roots, 0));
        else Assert.Throws<JsonException>(() => previewer.Preview(Guid.NewGuid(), bytes, roots, 0));
        Assert.Equal(before, Fingerprints(target.Instance.RuntimeRoot));
    }

    [Theory(DisplayName = "Template Paths Cannot Escape The Selected Root Or Use Windows Device And Stream Aliases")]
    [InlineData("../outside.exe", true)]
    [InlineData("run.exe:stream", true)]
    [InlineData("CON", false)]
    [InlineData("run.exe.", false)]
    public void RejectsEscapingPaths(string path, bool schemaFailure)
    {
        var source = Create(populate: true);
        var target = Create(populate: false);
        var catalog = source.Store.Read();
        var document = JsonNode.Parse(new AutomationBundleCodec(new()).Encode(catalog, DateTimeOffset.UtcNow, catalog.Scripts.Single().Id))!;
        document["scripts"]![0]!["executable"]!["relative"] = path;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        var roots = SelectedRoots(catalog, target.Root, "selected");
        var previewer = new AutomationImportPreviewer(target.Instance, new(), target.Database);
        var before = Fingerprints(target.Instance.RuntimeRoot);
        if (schemaFailure) Assert.Throws<JsonException>(() => previewer.Preview(Guid.NewGuid(), bytes, roots, 0));
        else Assert.Throws<IOException>(() => previewer.Preview(Guid.NewGuid(), bytes, roots, 0));
        Assert.Equal(before, Fingerprints(target.Instance.RuntimeRoot));
    }

    [Fact(DisplayName = "An Unavailable Executable Produces A Blocking Preview Without Publishing Definitions")]
    public void BlocksUnavailableInputs()
    {
        var source = Create(populate: true);
        var target = Create(populate: false);
        var catalog = source.Store.Read();
        var bytes = new AutomationBundleCodec(new()).Encode(catalog, DateTimeOffset.UtcNow);
        var roots = SelectedRoots(catalog, target.Root, "selected");
        File.Delete(Path.Combine(roots.Single().Value, "run.exe"));
        var preview = new AutomationImportPreviewer(target.Instance, new(), target.Database).Preview(Guid.NewGuid(), bytes, roots, 0);
        Assert.False(preview.CanApply);
        Assert.True(Assert.Single(preview.Problems).BlocksApply);
        Assert.Equal("automation_import_source_unavailable", Assert.Throws<AutomationDefinitionException>(() =>
            AutomationImportPreviewer.RequireSamePreview(preview, preview.InputDigest, preview.PreviewDigest)).Message);
        Assert.Equal(0, target.Store.Read().Revision);
    }

    private static IReadOnlyDictionary<Guid, string> SelectedRoots(AutomationCatalog source, string root, string name) =>
        source.Roots.ToDictionary(value => value.Id, value =>
        {
            var selected = Path.Combine(root, name, value.Id.ToString("N"));
            Directory.CreateDirectory(selected);
            File.WriteAllBytes(Path.Combine(selected, "run.exe"), [1, 2, 3]);
            return selected;
        });

    private static Fixture Create(bool populate)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "automation-import-" + Guid.NewGuid().ToString("N"));
        var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var store = new AutomationDefinitionStore(database);
        if (populate)
        {
            var initial = AutomationDefinitionSeed.Create(root);
            store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", initial), initial, DateTimeOffset.UtcNow);
        }
        return new(root, instance, database, store);
    }

    private static string[] Fingerprints(string root) => RuntimeFingerprint.Capture(root);
    private sealed record Fixture(string Root, RuntimeInstance Instance, StateDatabase Database, AutomationDefinitionStore Store);
}

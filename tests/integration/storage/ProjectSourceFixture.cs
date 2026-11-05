using NexusPipeline.Core.Automation;
using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.StorageTests;

internal sealed class ProjectSourceFixture : IDisposable
{
    internal ExtensionCatalogFixture Catalog { get; }
    private readonly bool _ownsCatalog;
    internal string Path { get; }
    internal Guid Binding { get; }
    internal string? ConfigurationFile { get; }
    internal bool HasTaskProjection { get; }
    internal bool HasTaskRetry { get; }
    private readonly string _extensionId;
    internal ConfigurationRevisionStore Revisions => new(Catalog.Instance, Catalog.Database, new());
    internal ConfigurationAddress Address => new(Binding, "account");

    internal ProjectSourceFixture(bool configuration = false, bool file = false, bool enabled = false, bool taskProjection = false, bool taskRetry = false,
        ExtensionCatalogFixture? catalog = null, string configurationName = "local.json", string extensionId = "maas")
    {
        _extensionId = extensionId;
        Catalog = catalog ?? new(); _ownsCatalog = catalog is null;
        HasTaskProjection = taskProjection;
        HasTaskRetry = taskRetry;
        var changes = AutomationDefinitionSeed.Create(System.IO.Path.GetDirectoryName(Catalog.Instance.RuntimeRoot)!);
        changes = changes with { Scripts = [changes.Scripts[0] with { Kind = AutomationKind.Specialized, ExtensionId = extensionId }] };
        if (taskRetry) changes = changes with { Scripts = [changes.Scripts[0] with
            { Judgement = new(AutomationJudgementLanguage.Keywords, [], [], null) }] };
        if (enabled) changes = changes with { Bindings = changes.Bindings.Select((item, index) => item with { IsEnabled = index == 0 }).ToImmutableArray() };
        if (configuration)
        {
            var script = changes.Scripts[0]; var alias = file ? configurationName : "config";
            changes = changes with { Scripts = [script with { ConfigurationPaths = [new(script.RootId, alias)], AdapterResources =
                [new("account", 0, file ? "" : configurationName, 1, ConfigurationSyntax.Json,
                    new(taskProjection ? [new(new([]), true)] : [], [new(new([]), true)], []))] }] };
        }
        var store = new AutomationDefinitionStore(Catalog.Database);
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        Binding = changes.Bindings[0].Id; Path = changes.Roots[0].Path;
        if (configuration)
        {
            var directory = System.IO.Path.Combine(Path, "config"); Directory.CreateDirectory(directory);
            ConfigurationFile = System.IO.Path.Combine(file ? Path : directory, configurationName);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigurationFile)!);
            File.WriteAllText(ConfigurationFile, taskProjection
                ? "{\"account\":\"saved-account\",\"TaskItems\":[],\"taskA\":true,\"taskB\":true,\"unlisted\":true,\"counter\":17}"
                : "{\"account\":\"saved-account\",\"TaskItems\":[]}");
            var script = store.Read().Scripts[0];
            var tree = new FileTreeArchive(new FileTreeScanner(SnapshotBudget.Default), new(Catalog.Instance, new()))
                .Capture(file ? ConfigurationFile : directory, default);
            Save(new(Address, 0, 1, AdapterConfigurationPolicy.Contract(script, script.AdapterResources[0]), tree.Snapshot, ConfigurationUpdateKind.Manual));
        }
    }

    internal void Write(string path, byte[] bytes) => File.WriteAllBytes(System.IO.Path.Combine(Path, path), bytes);
    internal SelectedProjectSource Open(Action? check = null) => new(Catalog.Instance, Catalog.Database, Catalog.Lease, Catalog.Resources,
        Binding, 1, _extensionId, check ?? (() => { }));
    internal void Save(ConfigurationRevisionChange change)
    {
        var batch = new ConfigurationRevisionBatch([change]); var store = new ConfigurationBatchStore(Catalog.Instance, Catalog.Database, new());
        using var prepared = store.Prepare(batch);
        store.Commit(ConfigurationBatchStore.Identity(Guid.NewGuid(), "fixture", batch), prepared, DateTimeOffset.UtcNow);
    }
    public void Dispose() { if (_ownsCatalog) Catalog.Dispose(); }
}

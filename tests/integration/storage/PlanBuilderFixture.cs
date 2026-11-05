using ExecutionTarget = NexusPipeline.Core.Execution.ExecutionTarget;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

internal sealed class PlanBuilderFixture : IDisposable
{
    internal static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-10-21T00:00:00Z");
    internal Guid ScriptId { get; } = Guid.Parse("10000000-0000-0000-0000-000000000001");
    internal Guid OtherScriptId { get; } = Guid.Parse("10000000-0000-0000-0000-000000000002");
    internal Guid UserA { get; } = Guid.Parse("20000000-0000-0000-0000-000000000003");
    internal Guid UserB { get; } = Guid.Parse("20000000-0000-0000-0000-000000000001");
    internal Guid UserC { get; } = Guid.Parse("20000000-0000-0000-0000-000000000002");
    internal Guid BindingA { get; } = Guid.Parse("30000000-0000-0000-0000-000000000001");
    internal Guid BindingB { get; } = Guid.Parse("30000000-0000-0000-0000-000000000002");
    internal Guid BindingC { get; } = Guid.Parse("30000000-0000-0000-0000-000000000003");
    internal Guid OtherBinding { get; } = Guid.Parse("30000000-0000-0000-0000-000000000004");
    internal Guid QueueId { get; } = Guid.Parse("40000000-0000-0000-0000-000000000001");
    internal string Root { get; }
    internal string Image { get; }
    internal string OtherImage { get; }
    internal string Configuration { get; }
    internal string Log { get; }
    internal RuntimeInstance Instance { get; }
    internal StateDatabase Database { get; }
    internal InstanceLease Lease { get; }
    internal ExecutionPlanBuilder Builder { get; }
    internal AutomationDefinitionStore Definitions => new(Database);
    internal HostSettingsStore Settings => new(Database);

    internal PlanBuilderFixture(bool specialized = false, bool saveConfigurations = true, string? otherExtensionId = null)
    {
        Root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "execution-builder-" + Guid.NewGuid().ToString("N"));
        Instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(Root, "runtime"));
        Database = StateDatabase.CreateFresh(Instance);
        var main = Path.Combine(Root, "script"); var other = Path.Combine(Root, "other-script");
        var external = Path.Combine(Root, "external");
        Directory.CreateDirectory(main); Directory.CreateDirectory(other); Directory.CreateDirectory(external);
        Image = Path.Combine(main, "OwnedWriter.exe"); OtherImage = Path.Combine(other, "OwnedWriter.exe");
        var ownedWriter = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        File.Copy(ownedWriter, Image); File.Copy(ownedWriter, OtherImage);
        Configuration = Path.Combine(external, "settings.json"); Log = Path.Combine(external, "events.log");
        File.WriteAllText(Configuration, "{\"fixture\":true}"); File.WriteAllText(Log, "original log\n");
        Lease = InstanceLease.Acquire(Instance, new());
        try
        {
            var index = new StartupJournalRecovery(Instance, new(), Database,
                new(Path.Combine(Root, "host"), Path.Combine(Instance.RuntimeRoot, "extensions"))).Recover(Lease);
            var resources = new ResourceLeaseManager(new(index, TimeProvider.System));
            var service = new AutomationImportService(Instance, new(), Database, Lease, resources, index);
            var catalog = Seed(main, other, external, specialized, otherExtensionId);
            var roots = catalog.Roots.ToDictionary(item => item.Id, item => item.Path);
            var bundle = new AutomationBundleCodec(new()).Encode(catalog, CreatedAt);
            var operation = Guid.NewGuid();
            if (catalog.Scripts.Any(script => script.Kind == AutomationKind.Specialized))
            {
                // These declarations seed the fixture before its package installation; Host import authorization is tested at its own entry.
                var changes = new AutomationBundleCodec(new()).Decode(bundle, roots, 0).Definitions;
                Definitions.Commit(AutomationDefinitionStore.Identity(operation, "builder-fixture", changes), changes, CreatedAt);
            }
            else
            {
                var preview = service.Preview(operation, bundle, roots, 0);
                service.Apply(operation, "builder-fixture", bundle, roots, 0, preview.PreviewDigest, new Dictionary<Guid, string>());
            }
            if (saveConfigurations) SeedSavedConfigurations();
            Builder = new(Instance, Database, new(), Lease, new(5, 6, 7));
        }
        catch { Lease.Dispose(); throw; }
    }

    private void SeedSavedConfigurations()
    {
        var catalog = Definitions.Read();
        var store = new ConfigurationRevisionStore(Instance, Database, new());
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), new(Instance, new()));
        var scripts = catalog.Scripts.ToDictionary(script => script.Id);
        var roots = catalog.Roots.ToDictionary(root => root.Id);
        foreach (var binding in catalog.Bindings.Where(binding => scripts[binding.ScriptId].Kind == AutomationKind.Generic))
            foreach (var path in scripts[binding.ScriptId].ConfigurationPaths)
            {
                var tree = archive.Capture(Path.Combine(roots[path.RootId].Path, path.RelativePath), default).Snapshot;
                var change = new ConfigurationRevisionChange(new(binding.Id, GenericConfigurationPolicy.ResourceId(path)), 0, binding.Revision,
                    GenericConfigurationPolicy.Contract(path), tree, ConfigurationUpdateKind.Manual);
                store.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "builder-fixture", change), change, CreatedAt);
            }
    }

    private AutomationCatalog Seed(string main, string other, string external, bool specialized, string? otherExtensionId)
    {
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Main script", AutomationRootKind.Script, main);
        var second = new AutomationRoot(Guid.NewGuid(), 0, "Other script", AutomationRootKind.Script, other);
        var config = new AutomationRoot(Guid.NewGuid(), 0, "Configuration and log", AutomationRootKind.External, external);
        var script = new AutomationScript(ScriptId, 0, "Main", specialized ? AutomationKind.Specialized : AutomationKind.Generic,
            root.Id, specialized ? "fixture-extension" : null, new(root.Id, "OwnedWriter.exe"),
            ["--fixture-input-only"], [new(config.Id, "settings.json")], [new(config.Id, "events.log")], new(3, 30, 120),
            new(AutomationJudgementLanguage.Keywords, ["done"], ["failed"], null));
        var otherScript = script with { Id = OtherScriptId, Name = "Other", RootId = second.Id,
            Kind = otherExtensionId is null ? AutomationKind.Generic : AutomationKind.Specialized, ExtensionId = otherExtensionId,
            Executable = new(second.Id, "OwnedWriter.exe"), ConfigurationPaths = [], LogPaths = [] };
        return AutomationEdits.Apply(AutomationCatalog.Empty, new(0, [root, second, config], [script, otherScript],
            [new(UserA, 0, "Same name", 2), new(UserB, 0, "Same name", 1), new(UserC, 0, "Third", 2)],
            [new(BindingA, 0, UserA, ScriptId, true, 0), new(BindingB, 0, UserB, ScriptId, true, 9),
                new(BindingC, 0, UserC, ScriptId, true, 1), new(OtherBinding, 0, UserB, OtherScriptId, true, 0)],
            [new(QueueId, 0, "Explicit order", [ScriptId, OtherScriptId], AutomationCompletionAction.Exit)], [], []));
    }

    internal ExecutionSelectionRequest Request(ExecutionTarget target) => new(target, Definitions.Read().Revision, Settings.Read().Revision);
    internal ExecutionPlanDraft Prepare(ExecutionTarget target) => Builder.Prepare(Request(target), CreatedAt);

    internal void Disable(params Guid[] bindings)
    {
        var catalog = Definitions.Read(); var ids = bindings.ToHashSet();
        Commit(new(catalog.Revision, [], [], [], catalog.Bindings.Where(item => ids.Contains(item.Id))
            .Select(item => item with { IsEnabled = false }).ToImmutableArray(), [], [], []));
    }

    internal void EmptyQueue()
    {
        var catalog = Definitions.Read();
        Commit(new(catalog.Revision, [], [], [], [], [catalog.Queues.Single() with { ScriptIds = [] }], [], []));
    }

    internal void SetExtensionPreference(bool? enabled)
    {
        if (enabled is null) return;
        var settings = Settings.Read();
        var change = new HostSettingsChange(settings.Revision, settings.Values with { Extensions = [new("fixture-extension", enabled.Value)] });
        Settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "builder-fixture", change), change, CreatedAt);
    }

    private void Commit(AutomationChanges changes) => Definitions.Commit(
        AutomationDefinitionStore.Identity(Guid.NewGuid(), "builder-fixture", changes), changes, CreatedAt);

    internal (string[] Rows, string[] Files) Snapshot()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Database.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name;";
            using var reader = command.ExecuteReader(); while (reader.Read()) tables.Add(reader.GetString(0));
        }
        var rows = new List<string>();
        foreach (var table in tables)
        {
            rows.Add("table:" + table);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\";";
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(table + ":" + JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray()));
        }
        // SQLite read connections may checkpoint or create sidecars; compare every logical row and the remaining physical scene separately.
        var files = Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "instance.lock" && path != Database.DatabasePath &&
                path != Database.DatabasePath + "-wal" && path != Database.DatabasePath + "-shm")
            .Select(path => "file:" + Path.GetRelativePath(Root, path) + ":" + PhysicalObjectIdentity.Read(path) + ":" +
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))
            .Concat(Directory.GetDirectories(Root, "*", SearchOption.AllDirectories)
                .Select(path => "directory:" + Path.GetRelativePath(Root, path) + ":" + PhysicalObjectIdentity.Read(path)))
            .Order(StringComparer.Ordinal).ToArray();
        return (rows.Order(StringComparer.Ordinal).ToArray(), files);
    }

    public void Dispose() => Lease.Dispose();
}

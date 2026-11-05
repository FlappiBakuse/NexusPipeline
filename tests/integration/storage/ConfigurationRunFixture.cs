using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class ConfigurationRunFixture : IDisposable
{
    internal const string Subject = "fixture-run";
    internal ExecutionPlanFixture Source { get; }
    internal PreparedExecutionPlan Execution { get; }
    internal InstanceLease Lease { get; }
    internal HostLifecycle Lifecycle { get; }
    internal RecoveryIndex Index { get; }
    internal ResourceLeaseManager Resources { get; }
    internal ConfigurationRunPreparation Preparation { get; }
    internal ConfigurationRunRequest Request { get; }
    internal ConfigurationRevision Saved { get; }
    internal Guid OtherBinding { get; } = Guid.NewGuid();
    internal Guid Owner { get; } = Guid.NewGuid();
    internal string NativeReport => Path.Combine(Source.Root, "run-writer-ready.json");
    internal string StepId => Source.Plan.Steps[0].StepId;
    private readonly Dictionary<string, (FileTreeSnapshot Tree, IReadOnlyList<PhysicalTreeObject> Objects)> _originals = [];
    private readonly List<Guid> _installed = [];

    internal ConfigurationRunFixture(bool save = true, bool overlap = false, bool additional = false, bool nativeWriter = false, bool childWriter = false, bool updateAtFinish = true)
    {
        var source = ExecutionPlanFixture.CreateManagedPaths();
        var bundle = source.Bundle.DeepClone().AsObject();
        bundle["scripts"]![0]!["updateConfigurationAtFinish"] = updateAtFinish;
        var otherUser = Guid.NewGuid();
        bundle["users"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { id = otherUser, name = "Other account", order = 1 }));
        bundle["bindings"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { id = OtherBinding, userId = otherUser,
            scriptId = source.Plan.Steps[0].ScriptId, enabled = true, priority = 2,
            notifyEnabled = false, notificationTaskIds = Array.Empty<string>() }));
        if (overlap || additional)
        {
            var rootId = source.Plan.RootBindings[0].RootId;
            bundle["scripts"]![0]!["configurationPaths"] = JsonSerializer.SerializeToNode(new[] {
                new { rootId, relative = "configuration" }, new { rootId, relative = overlap ? "configuration/empty" : "configuration-extra" } });
            if (additional)
            {
                var extra = Path.Combine(source.Plan.RootBindings[0].Path, "configuration-extra");
                Directory.CreateDirectory(extra); File.WriteAllText(Path.Combine(extra, "original.txt"), "extra original");
            }
        }
        var plan = source.WithBundle(bundle);
        plan = plan with { Steps = [plan.Steps[0] with { UpdateConfigurationAtFinish = updateAtFinish }] };
        if (overlap || additional) plan = plan with { Steps = [plan.Steps[0] with { ConfigurationTargets =
            [plan.Steps[0].ConfigurationTargets[0], overlap ? Path.Combine(plan.Steps[0].ConfigurationTargets[0], "empty")
                : Path.Combine(plan.RootBindings[0].Path, "configuration-extra")] }] };
        if (nativeWriter)
        {
            var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
            var directory = Path.GetDirectoryName(executable)!; var nativeRoot = Guid.NewGuid().ToString("D");
            var input = Path.Combine(source.Root, "run-writer-input.json");
            var arguments = new[] { "configuration-editor-writer", input, Path.Combine(source.Root, "run-writer-ready.json") };
            File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { child = childWriter,
                operations = plan.Steps[0].ConfigurationTargets.Select(target => new { target = Path.Combine(target, "tasks.json"), kind = "file",
                    value = "{\"tasks\":{\"one\":true,\"new\":false},\"count\":41}" }).ToArray() }));
            bundle["roots"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { id = nativeRoot, name = "Owned writer binaries", kind = "script" }));
            bundle["scripts"]![0]!["rootId"] = nativeRoot;
            bundle["scripts"]![0]!["executable"] = JsonSerializer.SerializeToNode(new { rootId = nativeRoot, relative = Path.GetFileName(executable) });
            bundle["scripts"]![0]!["arguments"] = JsonSerializer.SerializeToNode(arguments);
            var updated = source.WithBundle(bundle);
            plan = plan with { DefinitionBundle = updated.DefinitionBundle,
                RootBindings = plan.RootBindings.Append(new() { RootId = nativeRoot, Path = directory, Identity = Wire(PhysicalObjectIdentity.Read(directory)) }).ToArray(),
                SourceRevisions = plan.SourceRevisions.Append(new() { EntityKind = "root", EntityId = nativeRoot, Revision = 1 }).ToArray(),
                Steps = [plan.Steps[0] with { ExecutablePath = executable, ImageSizeBytes = new FileInfo(executable).Length,
                    ImageSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))), ImageIdentity = Wire(PhysicalObjectIdentity.Read(executable)),
                    ParentIdentity = Wire(PhysicalObjectIdentity.Read(directory)), Arguments = arguments, WorkingDirectory = directory,
                    WorkingDirectoryIdentity = Wire(PhysicalObjectIdentity.Read(directory)) }] };
        }
        var stepId = "binding-" + plan.Steps[0].BindingId + "-0000";
        plan = plan with { Selection = [plan.Selection[0] with { StepId = stepId }], Steps = [plan.Steps[0] with { StepId = stepId }] };
        plan = plan with { ConfigurationSlots = plan.Steps[0].ConfigurationTargets.Select((path, index) => new NexusPipeline.Platform.Serialization.Protocol.ExecutionPlanConfigurationSlotsItem
            { StepId = stepId, BindingId = plan.Steps[0].BindingId!, ResourceId = index == 0 ? "main" : "extra", TargetPath = path, ExpectedRevision = 1 }).ToArray() };
        plan = plan with { PhysicalClaims = ExecutionPlanResources.Capture(plan) };
        plan = plan with { ResourceKeys = ExecutionPlanResources.Derive(plan) };
        Source = source with { Plan = plan, Bundle = bundle };
        var roots = plan.RootBindings.ToDictionary(item => Guid.Parse(item.RootId), item => item.Path);
        var changes = new AutomationBundleCodec(new()).Decode(JsonSerializer.SerializeToUtf8Bytes(bundle), roots, 0).Definitions;
        new AutomationDefinitionStore(Source.Database).Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        Execution = Source.Codec.Freeze(plan);
        foreach (var path in plan.Steps[0].ConfigurationTargets)
        {
            var before = new FileTreeScanner(SnapshotBudget.Default).Capture(path, default);
            _originals.Add(path, (before, PhysicalTreeIdentity.Capture(path, before)));
        }
        var content = Source.Blobs.PersistContent(Encoding.UTF8.GetBytes("{\"tasks\":{\"one\":false},\"count\":17}"), default);
        var tree = new FileTreeSnapshot(FileObjectKind.Directory,
            [new("", FileObjectKind.Directory, 0, null), new("tasks.json", FileObjectKind.File, content.SizeBytes, content.Sha256)]);
        var contract = new ConfigurationContractStamp(1, new('a', 64), null, false);
        var selector = new ConfigurationSelector([new ConfigurationProperty("tasks")]);
        var rule = new RunTaskRule("tasks.json", ConfigurationSyntax.Json,
            new([new(selector, true)], [new(selector, true)], []), new(new ConfigurationMapTasks(selector), ["one"]), ["one"]);
        var resources = plan.Steps[0].ConfigurationTargets.Select((path, index) =>
        {
            var address = new ConfigurationAddress(Guid.Parse(plan.Steps[0].BindingId!), index == 0 ? "main" : "extra");
            if (save)
            {
                var change = new ConfigurationRevisionChange(address, 0, 1, contract, tree, ConfigurationUpdateKind.Manual);
                new ConfigurationRevisionStore(Source.Instance, Source.Database, new()).Commit(
                    ConfigurationRevisionStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
            }
            return new ConfigurationRunResource(Guid.NewGuid(), Guid.NewGuid(), new(address, 1, contract, path)
                { WritePolicy = new([""], [FileObjectKind.Directory]) { Documents = [new("tasks.json", ConfigurationSyntax.Json, true)] } }, [rule]);
        }).ToImmutableArray();
        Request = new(1, true, resources) { SyncAtFinish = updateAtFinish };
        Saved = new(resources[0].Target.Address, 1, Guid.Parse(plan.Steps[0].UserId!), Guid.Parse(plan.Steps[0].ScriptId), contract, tree);
        Lease = InstanceLease.Acquire(Source.Instance, new());
        Index = new StartupJournalRecovery(Source.Instance, new(), Source.Database,
            new(Source.Root, Path.Combine(Source.Instance.RuntimeRoot, "extensions"))).Recover(Lease);
        Lifecycle = new(Index, TimeProvider.System); Resources = new(Lifecycle);
        Preparation = new(Source.Instance, Source.Database, new(), Lease, Resources);
    }

    internal IReadOnlyList<ResourceClaim> Claims(ConfigurationRunRequest request) => Preparation.Claims(Execution.Plan, StepId, request);
    internal ResourceOperationLease Acquire(ConfigurationRunRequest request) =>
        Assert.IsType<ResourceOperationLease>(Resources.TryAcquire(Owner, Claims(request)).Lease);
    internal string Read(FileTreeSnapshot tree)
    {
        var file = tree.Entries.Single(entry => entry.Kind == FileObjectKind.File);
        using var input = Source.Blobs.OpenVerified(new(Content(tree), file.SizeBytes, file.Sha256!));
        using var reader = new StreamReader(input); return reader.ReadToEnd();
    }
    internal void ChangeDefinition(string fault)
    {
        var store = new AutomationDefinitionStore(Source.Database); var catalog = store.Read();
        var step = Source.Plan.Steps[0];
        var changes = new AutomationChanges(catalog.Revision, [], [],
            fault == "user-definition" ? [catalog.Users.Single(item => item.Id.ToString("D") == step.UserId) with { Name = "Changed account" }] : [],
            fault == "binding-revision" ? [catalog.Bindings.Single(item => item.Id.ToString("D") == step.BindingId) with { Priority = 9 }] : [], [], [], []);
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
    }
    internal void VerifyOriginal()
    {
        foreach (var pair in _originals)
        {
            var current = new FileTreeScanner(SnapshotBudget.Default).Capture(pair.Key, default);
            Assert.Equal(pair.Value.Tree.Shape, current.Shape); Assert.Equal(pair.Value.Tree.Entries, current.Entries);
            Assert.Equal(pair.Value.Objects, PhysicalTreeIdentity.Capture(pair.Key, current));
        }
    }

    internal NexusPipeline.Platform.Serialization.Protocol.RunAttemptPlan Parent(PreparedConfigurationRun prepared) => prepared.Use(resources =>
    {
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), Source.Blobs);
        var parent = AttemptPlanSeed.Create(Source, Execution) with { OperationId = Owner.ToString("D") };
        return parent with
        {
            ConfigurationPlans = resources.Select(item => new NexusPipeline.Platform.Serialization.Protocol.RunAttemptPlanConfigurationPlansItem
                { StepId = StepId, Plan = new() { RelativePath = item.Reference.RelativePath, SizeBytes = item.Reference.SizeBytes, Sha256 = item.Reference.Sha256 } }).ToArray(),
            InitialLogs = Source.Plan.Steps[0].LogTargets.Select(log => log.ScopePath).Select(path =>
            {
                var before = archive.Capture(path, default);
                return new NexusPipeline.Platform.Serialization.Protocol.RunAttemptPlanInitialLogsItem
                {
                    Path = path, ParentIdentity = Wire(PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!)), Before = new()
                    {
                        Shape = before.Snapshot.Shape.ToString().ToLowerInvariant(), Entries = before.Snapshot.Entries.Select(entry =>
                            new NexusPipeline.Platform.Serialization.Protocol.FileTreeEntriesItem { RelativePath = entry.RelativePath,
                                Kind = entry.Kind.ToString().ToLowerInvariant(), SizeBytes = entry.SizeBytes, Sha256 = entry.Sha256 }).ToArray()
                    }, OriginalObjects = before.OriginalObjects.Select(item => new NexusPipeline.Platform.Serialization.Protocol.FileTreePhysicalObject
                        { RelativePath = item.RelativePath, Identity = Wire(item.Identity) }).ToArray()
                };
            }).ToArray()
        };
    });

    internal AttemptAcceptanceResult Accept(NexusPipeline.Platform.Serialization.Protocol.RunAttemptPlan parent, string subject = Subject) =>
        new RunAttemptAcceptance(Source.Instance, new(), Source.Database).Accept(new(Owner, subject, new('b', 64), "fixture.run"), parent, DateTimeOffset.UtcNow);
    internal void Install(Guid journalId)
    {
        new ConfigurationEditExchange(Source.Instance, Source.Database, new()).Install(journalId);
        _installed.Add(journalId);
    }
    internal void Restore()
    {
        foreach (var id in _installed)
            Assert.Equal(ConfigurationRecoveryResult.Restored, new ConfigurationEditExchange(Source.Instance, Source.Database, new()).Restore(id));
        _installed.Clear(); VerifyOriginal();
    }
    internal void SaveAgain()
    {
        var revisions = new ConfigurationRevisionStore(Source.Instance, Source.Database, new());
        var change = new ConfigurationRevisionChange(Saved.Address, 1, 1, Saved.Contract, Saved.Tree, ConfigurationUpdateKind.Manual);
        revisions.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), Subject, change), change, DateTimeOffset.UtcNow);
    }
    internal long Count(string table)
    {
        using var connection = Connect(); using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table};"; return (long)command.ExecuteScalar()!;
    }
    internal void Execute(string sql)
    {
        using var connection = Connect(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Source.Database.DatabasePath, Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    private static NexusPipeline.Extensibility.Protocol.FileTreePhysicalIdentity Wire(PhysicalObjectIdentity identity) =>
        new() { VolumeSerial = identity.VolumeSerial, FileId = identity.FileId };
    private static string Content(FileTreeSnapshot tree) => $"blobs/content/{tree.Entries.Single(entry => entry.Kind == FileObjectKind.File).Sha256}.bin";
    public void Dispose()
    {
        try { Restore(); }
        finally { Execution.Dispose(); Lease.Dispose(); }
    }
}

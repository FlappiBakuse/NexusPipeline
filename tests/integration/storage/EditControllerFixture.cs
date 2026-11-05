using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class EditControllerFixture : IAsyncDisposable
{
    internal ConfigurationBatchFixture Base { get; } = new();
    internal InstanceLease Lease { get; }
    internal RecoveryIndex Index { get; }
    internal HostLifecycle Lifecycle { get; }
    internal ResourceLeaseManager Resources { get; }
    internal ConfigurationEditController Controller { get; }
    internal ConfigurationEditRequest Request { get; }
    internal Guid SessionId { get; } = Guid.NewGuid();
    internal Dictionary<string, (FileTreeSnapshot Tree, IReadOnlyList<PhysicalTreeObject> Objects)> Originals { get; } = [];
    private readonly List<Guid> _sessions = [];
    private readonly Dictionary<Guid, string> _reports = [];
    private Guid? _independentBinding;

    internal EditControllerFixture(string originalShape = "file", string outputShape = "file", string value = "{\"edited\":true}",
        ConfigurationSyntax? syntax = null, bool directorySnapshot = false, IConfigurationEditObserver? observer = null, string? targetRoot = null)
    {
        var changes = new[] { "main", "extra" }.Select(id =>
        {
            if (!directorySnapshot) return Base.Capture(id, "{\"saved\":true}");
            var root = Path.Combine(Base.Root, "snapshot-" + id); Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "config.json"), "{\"saved\":true}"); return Base.CaptureTree(id, root);
        }).ToImmutableArray();
        Base.Save(new(changes));
        var policy = new EditWritePolicy([""], [FileObjectKind.Missing, FileObjectKind.File, FileObjectKind.Directory])
            { Documents = syntax is null ? [] : [new("", syntax.Value, true)] };
        var targets = changes.Select(change =>
        {
            var path = Path.Combine(targetRoot ?? Base.Root, "target-" + change.Address.ResourceId);
            if (originalShape == "file") File.WriteAllText(path, "original-" + change.Address.ResourceId);
            if (originalShape == "directory") { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "original.txt"), "original"); }
            var tree = new FileTreeScanner(SnapshotBudget.Default).Capture(path, default);
            Originals.Add(path, (tree, PhysicalTreeIdentity.Capture(path, tree)));
            return new ConfigurationTarget(change.Address, 1, change.Contract, path) { WritePolicy = policy };
        }).ToImmutableArray();
        Request = WithCommand(SessionId, new(1, targets), outputShape, value);
        Lease = InstanceLease.Acquire(Base.Instance, new());
        Index = new StartupJournalRecovery(Base.Instance, new(), Base.Database, new(Base.Root, Path.Combine(Base.Instance.RuntimeRoot, "extensions"))).Recover(Lease);
        Lifecycle = new(Index, TimeProvider.System); Resources = new(Lifecycle);
        Controller = new(Base.Instance, Base.Database, new(), Lease, Resources, Index, observer);
    }

    internal ConfigurationEditRequest WithCommand(Guid sessionId, ConfigurationEditRequest request, string shape, string value)
    {
        _sessions.Add(sessionId);
        var input = Path.Combine(Base.Root, sessionId.ToString("N") + "-input.json"); var report = input + ".ready";
        _reports[sessionId] = report;
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { operations = request.Targets.Select(target => new
            { target = target.TargetPath, kind = shape, value }).ToArray() }));
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        return request with { Editor = new(executable, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))),
            ["configuration-editor-writer", input, report], Base.Root) };
    }

    internal async Task<int> BeginAsync(Guid? sessionId = null, ConfigurationEditRequest? request = null)
    {
        var id = sessionId ?? SessionId;
        Assert.Equal(ConfigurationEditPhase.Editing, (await Controller.BeginAsync(id, "fixture", request ?? Request, DateTimeOffset.UtcNow)).Phase);
        return (await OwnedCrashProcess.ReadReportAsync(_reports[id])).GetProperty("processId").GetInt32();
    }

    internal ConfigurationEditRequest IndependentRequest(Guid sessionId, string path)
    {
        var definitions = new AutomationDefinitionStore(Base.Database); var catalog = definitions.Read();
        if (!_independentBinding.HasValue)
        {
            var other = catalog.Bindings.Single(binding => binding.Id == Base.OtherBinding);
            var script = catalog.Scripts[0] with { Id = Guid.NewGuid(), Revision = 0, Name = "Other script" };
            var bindingId = Guid.NewGuid();
            var changes = new AutomationChanges(catalog.Revision, [], [script], [],
                [other with { Id = bindingId, Revision = 0, ScriptId = script.Id, Priority = catalog.Bindings.Max(binding => binding.Priority) + 1 }], [], [], []);
            definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
            _independentBinding = bindingId;
            var initial = Base.Capture("main", "{\"saved\":true}") with { Address = new(bindingId, "main"), ExpectedBindingRevision = 1 };
            Base.Save(new([initial]));
        }
        var baseline = Base.Revisions.Read(new(_independentBinding.Value, "main"))!;
        return WithCommand(sessionId, new(1, [new(new(_independentBinding.Value, "main"), 1, baseline.Contract, path) { WritePolicy = Request.Targets[0].WritePolicy }]),
            "file", "{\"other\":true}");
    }

    internal void VerifyOriginals()
    {
        foreach (var pair in Originals)
        {
            var tree = new FileTreeScanner(SnapshotBudget.Default).Capture(pair.Key, default);
            Assert.Equal(pair.Value.Tree.Shape, tree.Shape); Assert.Equal(pair.Value.Tree.Entries, tree.Entries);
            Assert.Equal(pair.Value.Objects, PhysicalTreeIdentity.Capture(pair.Key, tree));
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Distinct())
        {
            var session = Controller.Read(id, "fixture");
            if (session is not null && session.Phase is ConfigurationEditPhase.Prepared or ConfigurationEditPhase.Editing)
                await Controller.CancelAsync(Guid.NewGuid(), "fixture", id, session.Revision, DateTimeOffset.UtcNow);
        }
        Controller.Dispose(); Lease.Dispose();
    }
}

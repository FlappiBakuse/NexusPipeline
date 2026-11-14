using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ControllerCrashTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases() => Enum.GetValues<ConfigurationEditBoundary>()
        .SelectMany(point => new[] { new object[] { point, true }, [point, false] });

    [Theory(DisplayName = "Actual Controller Owner Loss At Every Lifecycle Boundary Preserves Known Output Originals And Atomic Completion")]
    [MemberData(nameof(Cases))]
    public async Task RecoversControllerLoss(ConfigurationEditBoundary point, bool save)
    {
        var fixture = new ConfigurationBatchFixture(); var sessionId = Guid.NewGuid(); var completionId = Guid.NewGuid();
        var initial = new ConfigurationRevisionBatch([fixture.Capture("main", "{\"saved\":true}"), fixture.Capture("extra", "{\"saved\":true}")]);
        fixture.Save(initial);
        var originals = new Dictionary<string, (FileTreeSnapshot Tree, IReadOnlyList<PhysicalTreeObject> Objects)>();
        var targets = initial.Changes.Select(change =>
        {
            var target = Path.Combine(fixture.Root, change.Address.ResourceId + ".json"); File.WriteAllText(target, "original-" + change.Address.ResourceId);
            var tree = new FileTreeScanner(SnapshotBudget.Default).Capture(target, default); originals[target] = (tree, PhysicalTreeIdentity.Capture(target, tree));
            return new ConfigurationTarget(change.Address, 1, change.Contract, target)
                { WritePolicy = new([""], [FileObjectKind.File]) { Documents = [new("", ConfigurationSyntax.Json, true)] } };
        }).ToImmutableArray();
        var editorInput = Path.Combine(fixture.Root, "writer-input.json"); var editorReport = editorInput + ".ready";
        File.WriteAllBytes(editorInput, JsonSerializer.SerializeToUtf8Bytes(new { operations = targets.Select(target => new
            { target = target.TargetPath, kind = "file", value = EditedText(target.Address.ResourceId) }) }));
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var request = new ConfigurationEditRequest(1, targets) { Editor = new(executable,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))), ["configuration-editor-writer", editorInput, editorReport], fixture.Root) };
        var inputPath = Path.Combine(fixture.Root, "controller-input.json"); var report = inputPath + ".fault";
        File.WriteAllBytes(inputPath, JsonSerializer.SerializeToUtf8Bytes(new
            { runtimeRoot = fixture.Instance.RuntimeRoot, request, sessionId, completionId, point = point.ToString(), save, editorReport }));
        using var owner = new OwnedCrashProcess();
        var identity = owner.Start(["configuration-edit-controller", inputPath, report], fixture.Root, fixture.Instance.InstanceId,
            sessionId, Path.Combine(fixture.Root, "controller-process.json"));
        var fault = await OwnedCrashProcess.ReadReportAsync(report); Assert.Equal("faultReady", fault.GetProperty("status").GetString());
        Assert.Equal(identity.ProcessId, fault.GetProperty("processId").GetInt32()); Assert.Equal(point.ToString(), fault.GetProperty("point").GetString());
        Assert.Equal(sessionId, fault.GetProperty("sessionId").GetGuid());
        var stopped = await owner.StopAsync(); Assert.True(stopped.CompleteJobStopped); Assert.Equal(0u, stopped.Accounting.ActiveProcesses);
        File.WriteAllBytes(Path.Combine(fixture.Root, "controller-family-stopped.json"), JsonSerializer.SerializeToUtf8Bytes(stopped));
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        if (point is ConfigurationEditBoundary.Installed or ConfigurationEditBoundary.Editing or ConfigurationEditBoundary.WriterStarted)
        {
            VerifyWriterRefusal(fixture, sessionId, point, request, initial, originals, stopped);
            return;
        }
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "restart-diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        var recovery = new StartupJournalRecovery(fixture.Instance, new(), database, new(fixture.Root, Path.Combine(fixture.Instance.RuntimeRoot, "extensions")));
        var index = recovery.Recover(lease);
        var recovered = index.Current;
        var store = new ConfigurationEditStore(fixture.Instance, database, new());
        var session = store.LookupBegin(ConfigurationEditStore.BeginIdentity(sessionId, "fixture", request), request);
        Assert.NotNull(session);
        Assert.Equal(sessionId, session.SessionId);
        var isolated = point == ConfigurationEditBoundary.WriterStopped;
        var complete = point == ConfigurationEditBoundary.Completed;
        var open = point != ConfigurationEditBoundary.Accepted && !complete;
        var journals = new RecoveryJournalRegistry(database);
        var revisions = new ConfigurationRevisionStore(fixture.Instance, database, new());
        var blobs = new ImmutableBlobStore(fixture.Instance, new());
        Assert.Equal(targets.Length, session.Resources.Length);
        foreach (var target in targets)
        {
            var resource = Assert.Single(session.Resources, item => item.Address == target.Address);
            var journal = journals.ReadJournal(resource.JournalId);
            Assert.Equal(sessionId, journal.OperationId);
            Assert.Equal(isolated ? JournalPhase.Quarantined : JournalPhase.Committed, journal.Phase);
            var at = isolated ? Path.Combine(Path.GetDirectoryName(target.TargetPath)!, ".nexus-" + resource.JournalId.ToString("D"), "before") : target.TargetPath;
            var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(at, default);
            Assert.Equal(originals[target.TargetPath].Tree.Entries, actual.Entries);
            Assert.Equal(originals[target.TargetPath].Objects, PhysicalTreeIdentity.Capture(at, actual));
            var key = ConfigurationRevisionService.ResourceKey(target.Address);
            if (isolated)
            {
                Assert.Contains(recovered.Isolations, item => item.JournalId == resource.JournalId && item.ResourceKey == key);
                Assert.Equal(EditedText(target.Address.ResourceId), File.ReadAllText(target.TargetPath));
            }
            if (open)
                Assert.Contains(recovered.ResourceHolds, hold => hold.OperationId == sessionId && hold.ResourceKey == key &&
                    hold.Owner == RecoveryHoldOwner.ConfigurationOperations && hold.Mode == ResourceClaimMode.Mutation);
            AssertRevision(revisions.Read(target.Address), initial.Changes.Single(change => change.Address == target.Address), complete && save, blobs);
        }
        var expectedPhase = open ? ConfigurationEditPhase.Editing : complete && save ? ConfigurationEditPhase.Saved : ConfigurationEditPhase.Cancelled;
        Assert.Equal(expectedPhase, session.Phase);
        if (complete) Assert.Equal(completionId, session.CompletionOperationId);
        if (open) Assert.Null(session.CompletionOperationId);
        else Assert.Empty(recovered.ResourceHolds);
        var isolationIds = isolated ? session.Resources.Select(resource => resource.JournalId).Order() : Enumerable.Empty<Guid>();
        Assert.Equal(isolationIds, recovered.Isolations.Select(item => item.JournalId).Distinct().Order());
        var repeated = recovery.Recover(lease);
        Assert.Equal(recovered.Isolations, repeated.Current.Isolations);
        Assert.Equal(recovered.ResourceHolds, repeated.Current.ResourceHolds);
        if (point is ConfigurationEditBoundary.OutputSealed or ConfigurationEditBoundary.Restored or ConfigurationEditBoundary.Completed)
        {
            var lifecycle = new HostLifecycle(repeated, TimeProvider.System);
            using var controller = new ConfigurationEditController(fixture.Instance, database, new(), lease, new(lifecycle), repeated);
            var replay = await controller.BeginAsync(sessionId, "fixture", request, DateTimeOffset.UtcNow);
            Assert.False(replay.IsNew); Assert.Equal(sessionId, replay.SessionId);
            Assert.Equal(session.Resources.AsEnumerable(), replay.Resources.AsEnumerable());
            if (save)
            {
                var result = await controller.SaveAsync(completionId, "fixture", sessionId, 2, DateTimeOffset.UtcNow);
                Assert.Equal(!complete, result.IsNew); Assert.Equal(completionId, result.OperationId);
                Assert.Equal(initial.Changes.Length, result.Items.Length);
                foreach (var change in initial.Changes)
                {
                    var item = Assert.Single(result.Items, item => item.Saved.Address == change.Address);
                    Assert.Equal(completionId, item.OperationId); Assert.Equal(2, item.Saved.Revision);
                    var current = revisions.Read(change.Address);
                    AssertRevision(current, change, edited: true, blobs);
                    Assert.Equal(current!.Tree.Entries, item.Saved.Tree.Entries);
                }
            }
            else
            {
                var result = await controller.CancelAsync(completionId, "fixture", sessionId, 2, DateTimeOffset.UtcNow);
                Assert.Equal(!complete, result.IsNew); Assert.Equal(completionId, result.CompletionOperationId);
                Assert.Equal(sessionId, result.SessionId); Assert.Equal(ConfigurationEditPhase.Cancelled, result.Phase);
                foreach (var change in initial.Changes) AssertRevision(revisions.Read(change.Address), change, edited: false, blobs);
            }
            Assert.Empty(repeated.Current.ResourceHolds); Assert.NotNull(lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
        }
        ConfigurationEditInventory.VerifyReadOnly(database);
        output.WriteLine(JsonSerializer.Serialize(new { point, save, stopped, sessionPhaseAfterRecovery = session.Phase,
            isolation = recovered.Isolations, holds = recovered.ResourceHolds,
            scope = "Actual composed editor process-family loss, original native objects, durable output, startup recovery and explicit save/cancel replay" }));
    }

    private void VerifyWriterRefusal(ConfigurationBatchFixture fixture, Guid sessionId, ConfigurationEditBoundary point,
        ConfigurationEditRequest request, ConfigurationRevisionBatch initial,
        IReadOnlyDictionary<string, (FileTreeSnapshot Tree, IReadOnlyList<PhysicalTreeObject> Objects)> originals, OwnedCrashStop stopped)
    {
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        for (var repetition = 0; repetition < 2; repetition++)
            Assert.Equal("startup_journal_writer_stop_unconfirmed", Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Instance,
                Path.Combine(fixture.Root, "refused-" + repetition), new JournalStartupAdmission(fixture.Instance, new()))).Message);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        var inspection = DatabaseInspector.Inspect(fixture.Database.DatabasePath, fixture.Instance.InstanceId, Path.Combine(fixture.Root, "readonly-copy"));
        var snapshot = RecoveryJournalRegistry.ReadInspectedSnapshot(inspection);
        using var connection = DatabaseConnections.Open(Path.Combine(inspection.DiagnosticCopyRoot, "runtime.db"), readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        var session = ConfigurationEditRows.Read(connection, transaction, sessionId, "fixture");
        Assert.NotNull(session);
        Assert.Equal(point == ConfigurationEditBoundary.Installed ? ConfigurationEditPhase.Prepared : ConfigurationEditPhase.Editing, session.Phase);
        Assert.Null(session.CompletionOperationId);
        Assert.Equal(initial.Changes.Length, session.Resources.Length);
        var blobs = new ImmutableBlobStore(fixture.Instance, new());
        var codec = new ConfigurationSnapshotCodec(blobs);
        foreach (var change in initial.Changes)
        {
            var resource = Assert.Single(session.Resources, item => item.Address == change.Address);
            var journal = Assert.Single(snapshot.Journals, item => item.JournalId == resource.JournalId);
            Assert.Equal(sessionId, journal.OperationId); Assert.Equal(JournalPhase.Applied, journal.Phase);
            var target = request.Targets.Single(item => item.Address == resource.Address).TargetPath;
            var backup = Path.Combine(Path.GetDirectoryName(target)!, ".nexus-" + resource.JournalId.ToString("D"), "before");
            var tree = new FileTreeScanner(SnapshotBudget.Default).Capture(backup, default);
            Assert.Equal(originals[target].Tree.Entries, tree.Entries); Assert.Equal(originals[target].Objects, PhysicalTreeIdentity.Capture(backup, tree));
            Assert.Equal(point == ConfigurationEditBoundary.WriterStarted ? EditedText(resource.Address.ResourceId) : "{\"saved\":true}", File.ReadAllText(target));
            AssertRevision(ConfigurationRevisionRows.Current(connection, transaction, change.Address, codec)?.Revision, change, edited: false, blobs);
        }
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        output.WriteLine(JsonSerializer.Serialize(new { point, stopped, admission = "REFUSED_WRITER_STOP_UNCONFIRMED", sourcePreserved = before,
            scope = "A stopped outer supervision family does not authorize adoption of a disappeared inner Job without its durable closure; diagnose only its isolated copy" }));
    }

    private static void AssertRevision(ConfigurationRevision? revision, ConfigurationRevisionChange initial, bool edited, ImmutableBlobStore blobs)
    {
        Assert.NotNull(revision);
        Assert.Equal(initial.Address, revision.Address); Assert.Equal(initial.Contract, revision.Contract);
        Assert.Equal(edited ? 2 : 1, revision.Revision); Assert.Equal(initial.Tree.Shape, revision.Tree.Shape);
        if (!edited)
        {
            Assert.Equal(initial.Tree.Entries, revision.Tree.Entries);
            return;
        }
        var entry = Assert.Single(revision.Tree.Entries);
        Assert.Equal(FileObjectKind.File, entry.Kind);
        using var stream = blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
        using var reader = new StreamReader(stream);
        Assert.Equal(EditedText(initial.Address.ResourceId), reader.ReadToEnd());
    }

    private static string EditedText(string resource) => JsonSerializer.Serialize(new { edited = resource });

    private static IReadOnlyList<string> Fingerprints(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "instance.lock").Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}

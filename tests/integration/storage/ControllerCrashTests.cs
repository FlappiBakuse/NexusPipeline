using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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
            { target = target.TargetPath, kind = "file", value = JsonSerializer.Serialize(new { edited = target.Address.ResourceId }) }) }));
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
        var stopped = await owner.StopAsync(); Assert.True(stopped.CompleteJobStopped); Assert.Equal(0u, stopped.Accounting.ActiveProcesses);
        File.WriteAllBytes(Path.Combine(fixture.Root, "controller-family-stopped.json"), JsonSerializer.SerializeToUtf8Bytes(stopped));
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        if (point is ConfigurationEditBoundary.Installed or ConfigurationEditBoundary.Editing or ConfigurationEditBoundary.WriterStarted)
        {
            VerifyWriterRefusal(fixture, sessionId, point, request, originals, stopped);
            return;
        }
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "restart-diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        var recovery = new StartupJournalRecovery(fixture.Instance, new(), database, new(fixture.Root, Path.Combine(fixture.Instance.RuntimeRoot, "extensions")));
        var index = recovery.Recover(lease); var store = new ConfigurationEditStore(fixture.Instance, database, new()); var session = store.Read(sessionId, "fixture")!;
        var isolated = point is ConfigurationEditBoundary.Installed or ConfigurationEditBoundary.Editing or ConfigurationEditBoundary.WriterStarted or ConfigurationEditBoundary.WriterStopped;
        var journals = new RecoveryJournalRegistry(database).ReadSnapshot().Journals;
        foreach (var target in targets)
        {
            var resource = session.Resources.Single(item => item.Address == target.Address);
            Assert.Equal(isolated ? JournalPhase.Quarantined : JournalPhase.Committed, journals.Single(item => item.JournalId == resource.JournalId).Phase);
            var at = isolated ? Path.Combine(Path.GetDirectoryName(target.TargetPath)!, ".nexus-" + resource.JournalId.ToString("D"), "before") : target.TargetPath;
            var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(at, default);
            Assert.Equal(originals[target.TargetPath].Tree.Entries, actual.Entries);
            Assert.Equal(originals[target.TargetPath].Objects, PhysicalTreeIdentity.Capture(at, actual));
            Assert.Equal(point == ConfigurationEditBoundary.Completed && save ? 2 : 1, fixture.Revisions.Read(target.Address)!.Revision);
        }
        var expectedPhase = point == ConfigurationEditBoundary.Accepted ? ConfigurationEditPhase.Cancelled
            : point == ConfigurationEditBoundary.Installed ? ConfigurationEditPhase.Prepared
            : point == ConfigurationEditBoundary.Completed ? save ? ConfigurationEditPhase.Saved : ConfigurationEditPhase.Cancelled : ConfigurationEditPhase.Editing;
        Assert.Equal(expectedPhase, session.Phase);
        Assert.Equal(isolated, index.Current.Isolations.Count != 0);
        Assert.Equal(point is not (ConfigurationEditBoundary.Accepted or ConfigurationEditBoundary.Completed), index.Current.ResourceHolds.Count != 0);
        var repeated = recovery.Recover(lease); Assert.Equal(index.Current.Isolations, repeated.Current.Isolations);
        if (point is ConfigurationEditBoundary.OutputSealed or ConfigurationEditBoundary.Restored or ConfigurationEditBoundary.Completed)
        {
            var lifecycle = new HostLifecycle(repeated, TimeProvider.System);
            using var controller = new ConfigurationEditController(fixture.Instance, database, new(), lease, new(lifecycle), repeated);
            var replay = await controller.BeginAsync(sessionId, "fixture", request, DateTimeOffset.UtcNow); Assert.False(replay.IsNew);
            if (save)
            {
                var result = await controller.SaveAsync(completionId, "fixture", sessionId, 2, DateTimeOffset.UtcNow);
                Assert.Equal(point != ConfigurationEditBoundary.Completed, result.IsNew);
                Assert.All(result.Items, item => Assert.Equal(2, item.Saved.Revision));
            }
            else
            {
                var result = await controller.CancelAsync(completionId, "fixture", sessionId, 2, DateTimeOffset.UtcNow);
                Assert.Equal(point != ConfigurationEditBoundary.Completed, result.IsNew);
            }
            Assert.Empty(repeated.Current.ResourceHolds); Assert.NotNull(lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
        }
        ConfigurationEditInventory.VerifyReadOnly(database);
        output.WriteLine(JsonSerializer.Serialize(new { point, save, stopped, sessionPhaseAfterRecovery = session.Phase,
            isolation = index.Current.Isolations, holds = index.Current.ResourceHolds,
            scope = "Actual composed editor process-family loss, original native objects, durable output, startup recovery and explicit save/cancel replay" }));
    }

    private void VerifyWriterRefusal(ConfigurationBatchFixture fixture, Guid sessionId, ConfigurationEditBoundary point,
        ConfigurationEditRequest request, IReadOnlyDictionary<string, (FileTreeSnapshot Tree, IReadOnlyList<PhysicalTreeObject> Objects)> originals, OwnedCrashStop stopped)
    {
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        for (var repetition = 0; repetition < 2; repetition++)
            Assert.Equal("startup_journal_writer_stop_unconfirmed", Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Instance,
                Path.Combine(fixture.Root, "refused-" + repetition), new JournalStartupAdmission(fixture.Instance, new()))).Message);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        var inspection = DatabaseInspector.Inspect(fixture.Database.DatabasePath, fixture.Instance.InstanceId, Path.Combine(fixture.Root, "readonly-copy"));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(inspection.DiagnosticCopyRoot, "nexus.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT r.resource_id,j.journal_id,j.phase FROM configuration_edit_resources r JOIN operation_journals j ON j.journal_id=r.journal_id WHERE r.session_id=$session;";
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        var resources = new List<(string Id, Guid Journal)>();
        using (var row = command.ExecuteReader())
            while (row.Read()) { Assert.Equal("applied", row.GetString(2)); resources.Add((row.GetString(0), Guid.Parse(row.GetString(1)))); }
        Assert.Equal(2, resources.Count);
        foreach (var resource in resources)
        {
            var target = request.Targets.Single(item => item.Address.ResourceId == resource.Id).TargetPath;
            var backup = Path.Combine(Path.GetDirectoryName(target)!, ".nexus-" + resource.Journal.ToString("D"), "before");
            var tree = new FileTreeScanner(SnapshotBudget.Default).Capture(backup, default);
            Assert.Equal(originals[target].Tree.Entries, tree.Entries); Assert.Equal(originals[target].Objects, PhysicalTreeIdentity.Capture(backup, tree));
            Assert.Equal(point == ConfigurationEditBoundary.WriterStarted ? JsonSerializer.Serialize(new { edited = resource.Id }) : "{\"saved\":true}", File.ReadAllText(target));
        }
        command.CommandText = "SELECT phase FROM configuration_edit_sessions WHERE session_id=$session;";
        Assert.Equal(point == ConfigurationEditBoundary.Installed ? 0L : 1L, (long)command.ExecuteScalar()!);
        command.CommandText = "SELECT count(*) FROM configuration_current WHERE revision=1;";
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
        output.WriteLine(JsonSerializer.Serialize(new { point, stopped, admission = "REFUSED_WRITER_STOP_UNCONFIRMED", sourcePreserved = before,
            scope = "A stopped outer supervision family does not authorize adoption of a disappeared inner Job without its durable closure; diagnose only its isolated copy" }));
    }

    private static IReadOnlyList<string> Fingerprints(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "instance.lock").Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}

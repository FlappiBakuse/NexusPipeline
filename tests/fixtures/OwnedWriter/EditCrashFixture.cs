using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class EditCrashFixture
{
    internal static int Run(string[] arguments)
    {
        if (arguments is not [var inputPath, var report]) return 2;
        OwnedFixtureEvidence.RequirePath(inputPath); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            using var input = StrictJson.Parse(File.ReadAllBytes(inputPath));
            var root = input.RootElement;
            var runtime = root.GetProperty("runtimeRoot").GetString()!; OwnedFixtureEvidence.RequirePath(runtime);
            var request = root.GetProperty("request").Deserialize<ConfigurationEditRequest>()!;
            foreach (var target in request.Targets) OwnedFixtureEvidence.RequirePath(target.TargetPath);
            var operation = root.GetProperty("operation").Deserialize<OperationIdentity>()!;
            var point = root.GetProperty("point").GetString()!;
            var ordinal = root.GetProperty("ordinal").GetInt32();
            var instance = new InstanceRoot(new()).OpenExisting(runtime);
            using var lease = InstanceLease.Acquire(instance, new());
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(inputPath)!, "writer-diagnostics"), new JournalStartupAdmission(instance, new()));
            var store = new ConfigurationEditStore(instance, database, new());
            using var prepared = store.Prepare(operation.OperationId, request);
            var session = store.Begin(operation, prepared, DateTimeOffset.UtcNow);
            Pause(point, "accepted", report, session.SessionId);
            var exchange = new ConfigurationEditExchange(instance, database, new(), new PauseAtFile(point, session.Resources[ordinal].JournalId, report));
            foreach (var resource in session.Resources) exchange.Install(resource.JournalId);
            session = store.MarkEditing(session.SessionId, operation.SubjectId, 1);
            Pause(point, "editing", report, session.SessionId);
            return 3;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", code = error.Message });
            return 1;
        }
    }

    private static void Pause(string expected, string point, string report, Guid identity)
    {
        if (expected != point) return;
        OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point, identity, processId = Environment.ProcessId });
        Thread.Sleep(Timeout.Infinite);
    }

    private sealed class PauseAtFile(string expected, Guid journalId, string report) : IEditFileObserver
    {
        public void Reached(EditFileBoundary boundary, Guid actual) { if (actual == journalId) Pause(expected, boundary.ToString(), report, actual); }
    }
}

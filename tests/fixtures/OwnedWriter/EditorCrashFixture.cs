using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class EditorCrashFixture
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments is not [var inputPath, var report]) return 2;
        OwnedFixtureEvidence.RequirePath(inputPath); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            using var input = StrictJson.Parse(File.ReadAllBytes(inputPath)); var root = input.RootElement;
            var runtime = root.GetProperty("runtimeRoot").GetString()!; OwnedFixtureEvidence.RequirePath(runtime);
            var request = root.GetProperty("request").Deserialize<ConfigurationEditRequest>()!;
            foreach (var target in request.Targets) OwnedFixtureEvidence.RequirePath(target.TargetPath);
            var operation = root.GetProperty("operation").Deserialize<OperationIdentity>()!;
            var instance = new InstanceRoot(new()).OpenExisting(runtime); using var lease = InstanceLease.Acquire(instance, new());
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(inputPath)!, "writer-diagnostics"), new JournalStartupAdmission(instance, new()));
            var store = new ConfigurationEditStore(instance, database, new()); using var prepared = store.Prepare(operation.OperationId, request);
            var session = store.Begin(operation, prepared, DateTimeOffset.UtcNow);
            using var editor = new ConfigurationEditorProcess(instance, database, new(), lease, session.SessionId, operation.SubjectId);
            var exchange = new ConfigurationEditExchange(instance, database, new()); foreach (var resource in session.Resources) exchange.Install(resource.JournalId);
            session = store.MarkEditing(session.SessionId, operation.SubjectId, 1);
            var editorInput = inputPath + ".editor"; var editorReport = report + ".editor";
            File.WriteAllBytes(editorInput, JsonSerializer.SerializeToUtf8Bytes(new { operations = request.Targets.Select(target => new
                { target = target.TargetPath, kind = "file", atomic = true, value = "editor-output-" + target.Address.ResourceId }) }));
            var image = Environment.ProcessPath!;
            editor.Start(image, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(image))), ["configuration-editor-writer", editorInput, editorReport], Path.GetDirectoryName(inputPath)!);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(editorReport) && elapsed.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            if (!File.Exists(editorReport)) throw new IOException("fixture_editor_not_ready");
            var stopped = await editor.StopAsync(TimeSpan.FromSeconds(5)); editor.Dispose();
            var point = root.GetProperty("point").GetString()!; var ordinal = root.GetProperty("ordinal").GetInt32();
            Pause(point, "stopped", report, session.SessionId);
            var recorder = new EditOutputRecorder(instance, database, new(), lease, new Observer(point, session.Resources[ordinal].JournalId, report));
            foreach (var resource in session.Resources) recorder.Seal(resource.JournalId, stopped);
            return 3;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", code = error.Message });
            return 1;
        }
    }

    private static void Pause(string expected, string actual, string report, Guid id)
    {
        if (expected != actual) return;
        OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point = actual, id, processId = Environment.ProcessId });
        Thread.Sleep(Timeout.Infinite);
    }
    private sealed class Observer(string point, Guid journalId, string report) : IConfigurationOutputObserver
    {
        public void Reached(ConfigurationOutputBoundary boundary, Guid actual) { if (actual == journalId) Pause(point, boundary.ToString(), report, actual); }
    }
}

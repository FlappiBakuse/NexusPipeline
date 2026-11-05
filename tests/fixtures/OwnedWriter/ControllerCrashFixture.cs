using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class ControllerCrashFixture
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments is not [var inputPath, var report]) return 2;
        OwnedFixtureEvidence.RequirePath(inputPath); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            using var document = StrictJson.Parse(File.ReadAllBytes(inputPath)); var input = document.RootElement;
            var runtime = input.GetProperty("runtimeRoot").GetString()!; OwnedFixtureEvidence.RequirePath(runtime);
            var editorReport = input.GetProperty("editorReport").GetString()!; OwnedFixtureEvidence.RequirePath(editorReport);
            var request = input.GetProperty("request").Deserialize<ConfigurationEditRequest>()!;
            foreach (var target in request.Targets) OwnedFixtureEvidence.RequirePath(target.TargetPath);
            var id = input.GetProperty("sessionId").GetGuid(); var completion = input.GetProperty("completionId").GetGuid();
            var instance = new InstanceRoot(new()).OpenExisting(runtime); using var lease = InstanceLease.Acquire(instance, new());
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(inputPath)!, "controller-diagnostics"), new JournalStartupAdmission(instance, new()));
            var index = new StartupJournalRecovery(instance, new(), database,
                new(Path.GetDirectoryName(inputPath)!, Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
            var lifecycle = new HostLifecycle(index, TimeProvider.System); var resources = new ResourceLeaseManager(lifecycle);
            using var controller = new ConfigurationEditController(instance, database, new(), lease, resources, index,
                new Observer(input.GetProperty("point").GetString()!, editorReport, report));
            await controller.BeginAsync(id, "fixture", request, DateTimeOffset.UtcNow);
            WaitEditor(editorReport);
            if (input.GetProperty("save").GetBoolean()) await controller.SaveAsync(completion, "fixture", id, 2, DateTimeOffset.UtcNow);
            else await controller.CancelAsync(completion, "fixture", id, 2, DateTimeOffset.UtcNow);
            return 3;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", code = error.Message });
            return 1;
        }
    }

    private static void WaitEditor(string report)
    {
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(report) && elapsed.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(20);
        if (!File.Exists(report)) throw new IOException("fixture_controller_writer_not_ready");
    }

    private sealed class Observer(string point, string editorReport, string report) : IConfigurationEditObserver
    {
        public void Reached(ConfigurationEditBoundary boundary, Guid sessionId)
        {
            if (point != boundary.ToString()) return;
            if (boundary == ConfigurationEditBoundary.WriterStarted) WaitEditor(editorReport);
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", point, sessionId, processId = Environment.ProcessId });
            Thread.Sleep(Timeout.Infinite);
        }
    }
}

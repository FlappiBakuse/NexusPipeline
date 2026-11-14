using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

internal static class InitialExtensionCrash
{
    internal sealed record Input(RuntimeInstance Instance, string Source, string HostRoot, string Report, string Point);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var path]) return 2;
        OwnedFixtureEvidence.RequirePath(path);
        try
        {
            var input = JsonSerializer.Deserialize<Input>(File.ReadAllBytes(path)) ?? throw new IOException("fixture_input_invalid");
            foreach (var item in new[] { input.Source, input.HostRoot, input.Report }) OwnedFixtureEvidence.RequirePath(item);
            if (input.Point is not ("applied" or "committed")) return 2;
            var instance = new InstanceRoot(new()).OpenExisting(input.Instance.RuntimeRoot);
            if (instance != input.Instance) throw new IOException("fixture_instance_changed");
            using var lease = InstanceLease.Acquire(instance, new());
            var roots = new PayloadApplyRoots(input.HostRoot, Path.Combine(instance.RuntimeRoot, "extensions"));
            Directory.CreateDirectory(roots.ExtensionPayloadRoot);
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(path)!, "child-inspection"),
                new JournalStartupAdmission(instance, new(), roots), lease);
            var recovery = new StartupJournalRecovery(instance, new(), database, roots).Recover(lease);
            var resources = new ResourceLeaseManager(new HostLifecycle(recovery, TimeProvider.System));
            await using var runtime = new ExtensionRuntimeLifecycle(instance, database, lease, new(), roots, resources,
                recovery, new(), ExtensionReleaseTrust.Configured());
            await new InitialExtensionInstaller(instance, database, lease, new(), roots, resources, recovery, runtime, new Barrier(input))
                .InitializeAsync(input.Source, default);
            return 1;
        }
        catch (Exception error)
        {
            OwnedFixtureEvidence.WriteReport(path + ".failure.json", new { status = "FAIL", type = error.GetType().FullName, code = error.Message });
            Console.Error.WriteLine(error.GetType().FullName); return 1;
        }
    }

    private sealed class Barrier(Input input) : IPayloadPersistenceObserver
    {
        public void Reached(PayloadPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            var match = input.Point == "applied" && point == PayloadPersistencePoint.SqlProjectionCommitted && phase == JournalPhase.Applied ||
                input.Point == "committed" && point == PayloadPersistencePoint.JournalFlushed && phase == JournalPhase.Committed;
            if (!match) return;
            OwnedFixtureEvidence.WriteReport(input.Report, new { input.Instance, journalId, revision, phase, processId = Environment.ProcessId });
            Thread.Sleep(Timeout.Infinite);
        }
    }
}

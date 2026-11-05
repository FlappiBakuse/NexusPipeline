using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Platform.Processes;

internal static class OwnedFixtureProcess
{
    internal static async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        string report, string receipt, TimeSpan timeout, Guid? instanceId = null, Guid? attemptId = null,
        string? reportReadyReleaseFile = null)
    {
        using var job = new OwnedWorkerJob();
        var sync = new object();
        var identities = new Dictionary<(int, DateTime), ProcessIdentity>();
        using var starts = job.ObserveProcessStarts(pid =>
        {
            var identity = ProcessIdentityReader.Read(pid);
            lock (sync) identities[(pid, identity.CreatedAtUtc)] = identity;
        });
        var elapsed = Stopwatch.StartNew();
        var launched = job.Start(executable, arguments, workingDirectory, instanceId ?? Guid.NewGuid(), attemptId ?? Guid.NewGuid(), identity =>
        {
            using var output = new FileStream(receipt + ".launch.json", FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            output.Write(JsonSerializer.SerializeToUtf8Bytes(identity, JsonSerializerOptions.Web));
            output.Flush(flushToDisk: true);
        });
        using var process = Process.GetProcessById(launched.ProcessId);
        _ = process.Handle;
        if (process.StartTime.ToUniversalTime() != launched.CreatedAtUtc) throw new IOException("Fixture process identity changed");
        using var deadline = new CancellationTokenSource(timeout);
        if (reportReadyReleaseFile is not null)
        {
            while (!File.Exists(report) && !process.HasExited) await Task.Delay(20, deadline.Token);
            var observed = starts.Drain(TimeSpan.FromSeconds(5));
            var readyAccounting = job.GetAccounting();
            int observedCount;
            lock (sync) observedCount = identities.Count;
            var readyBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "nexus.test.family-ready", version = 1,
                attributionComplete = observed && starts.Failures.Length == 0 && observedCount == readyAccounting.TotalProcesses
            });
            using (var release = new FileStream(reportReadyReleaseFile + ".prepared", FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                release.Write(readyBytes);
                release.Flush(flushToDisk: true);
            }
            File.Move(reportReadyReleaseFile + ".prepared", reportReadyReleaseFile);
        }
        await process.WaitForExitAsync(deadline.Token);
        while (job.GetAccounting().ActiveProcesses != 0 && elapsed.Elapsed < timeout) await Task.Delay(20, deadline.Token);
        var drained = starts.Drain(TimeSpan.FromSeconds(5));
        var accounting = job.GetAccounting();
        ProcessIdentity[] members;
        lock (sync) members = identities.Values.ToArray();
        var complete = drained && starts.Failures.Length == 0 && members.Length == accounting.TotalProcesses;
        var stopped = accounting.ActiveProcesses == 0 && job.ActiveProcessIds().Count == 0;
        var passed = complete && stopped && process.ExitCode == 0 && File.Exists(report);
        File.WriteAllText(receipt, JsonSerializer.Serialize(new
        {
            status = passed ? "PASS" : "FAIL", scope = "Actual owned fixture process only", launched,
            members, accounting, attributionComplete = complete, allProcessesStopped = stopped,
            nativeExitCode = process.ExitCode, elapsedSeconds = elapsed.Elapsed.TotalSeconds, notificationFailures = starts.Failures
        }, JsonSerializerOptions.Web));
        return passed ? 0 : 1;
    }
}

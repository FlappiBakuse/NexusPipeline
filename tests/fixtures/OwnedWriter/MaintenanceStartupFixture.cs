using System.Text.Json;
using NexusPipeline.Platform.Processes;

internal static class MaintenanceStartupFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var report, var marker, var mode, var ancestorName, var parentName] || mode is not ("start" or "fail-registration")) return 2;
        MaintenanceStartupOrigin? origin = null;
        MaintenanceStartupJob? job = null;
        var registered = false;
        OwnedWorkerIdentity? root = null;
        string? failure = null;
        Dictionary<string, bool>? suspendedMembership = null;
        try
        {
            origin = MaintenanceStartupOrigin.CaptureUnjobbedProcess();
            job = new MaintenanceStartupJob(origin, _ => { });
            root = job.Start(Environment.ProcessPath!, [marker, "maintenance-startup"],
                Path.GetDirectoryName(report)!, Guid.NewGuid(), Guid.NewGuid(), identity =>
                {
                    if (File.Exists(marker)) throw new IOException("unregistered_process_executed");
                    root = identity;
                    using var process = System.Diagnostics.Process.GetProcessById(identity.ProcessId);
                    suspendedMembership = new();
                    foreach (var name in new[] { ancestorName, parentName })
                    {
                        using var ancestor = JobNative.OpenJobObjectW(4, false, name);
                        if (ancestor.IsInvalid || !JobNative.IsProcessInJob(process.SafeHandle, ancestor, out var member))
                            throw new IOException("fixture_ancestor_membership_unreadable");
                        suspendedMembership.Add(name, member);
                    }
                    registered = true;
                    if (mode == "fail-registration") throw new IOException("controlled_registration_failure");
                });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(marker)) await Task.Delay(10, deadline.Token);
        }
        catch (Exception error) { failure = error.GetType().Name + ":" + error.Message; }
        var started = File.Exists(marker);
        var createdStartupJob = job is not null;
        var flags = job is null ? 0 : ReadFlags(job);
        var stopped = job is null || await job.StopAsync(TimeSpan.FromSeconds(10));
        using var stream = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(new
        {
            started, registered, createdStartupJob, root, failure, flags, stopped, suspendedMembership,
            accounting = job?.GetAccounting() ?? new WorkerJobAccounting(0, 0, 0),
            active = job?.ActiveProcessIds() ?? []
        }, JsonSerializerOptions.Web));
        stream.Flush(flushToDisk: true);
        job?.Dispose(); origin?.Dispose();
        return 0;
    }

    private static uint ReadFlags(MaintenanceStartupJob job)
    {
        using var native = JobNative.OpenJobObjectW(4, false, job.Name);
        if (native.IsInvalid) throw new IOException("fixture_job_read_failed");
        return JobStateReader.Limits(native).Basic.Flags;
    }
}

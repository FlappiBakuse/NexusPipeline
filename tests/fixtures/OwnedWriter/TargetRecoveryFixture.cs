using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using NexusPipeline.Platform.Processes;

internal static class TargetRecoveryFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var mode, var report, var marker] || mode is not ("owned" or "retained" or "empty")) return 2;
        using var job = new OwnedWorkerJob();
        var family = new ConcurrentDictionary<(int, DateTime), ProcessIdentity>();
        var observer = job.ObserveProcessIdentities(identity => family[(identity.ProcessId, identity.CreatedAtUtc)] = identity);
        ProcessIdentity? root = null;
        if (mode != "empty")
        {
            var target = Environment.GetEnvironmentVariable("NEXUS_TARGET_GAME_FIXTURE_EXE") ?? throw new IOException("target_native_fixture_game_missing");
            var started = job.Start(target, [marker, "target-native-recovery"], Path.GetDirectoryName(report)!,
                Guid.NewGuid(), Guid.NewGuid(), identity =>
                    root = new(identity.ProcessId, identity.CreatedAtUtc, identity.ImagePath, identity.ImageSha256, identity.UserSid, identity.IsElevated));
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(marker))
            { if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("target_native_fixture_root_not_ready"); await Task.Delay(10); }
            var accounting = job.GetAccounting();
            if (started.ProcessId != root!.ProcessId || accounting.ActiveProcesses != 1)
            {
                observer.Drain(TimeSpan.FromSeconds(5));
                throw new IOException("target_native_fixture_root_unbound:" + JsonSerializer.Serialize(new
                    { started, root, accounting, family = family.Values, observer.Failures, active = job.ActiveProcessIds(), exit = job.ReadExitCode(started) }, JsonSerializerOptions.Web));
            }
        }
        using var native = JobNative.OpenJobObjectW(6, false, job.Name);
        if (native.IsInvalid) throw new IOException("target_native_fixture_job_unbound");
        if (mode == "retained")
        {
            var limits = JobStateReader.Limits(native);
            limits.Basic.Flags &= ~0x00002000U;
            if (!JobNative.SetInformationJobObject(native, 9, ref limits, (uint)Marshal.SizeOf<JobNative.ExtendedLimits>()))
                throw new IOException("target_native_fixture_flags_failed");
        }
        using var process = Process.GetCurrentProcess();
        using (var output = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            output.Write(JsonSerializer.SerializeToUtf8Bytes(new
            {
                creator = ProcessIdentityReader.Read(Environment.ProcessId), root, jobName = job.Name, sessionId = process.SessionId,
                flags = JobStateReader.Limits(native).Basic.Flags, accounting = job.GetAccounting()
            }, JsonSerializerOptions.Web));
            output.Flush(flushToDisk: true);
        }
        File.WriteAllText(report + ".ready", "ready");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }
}

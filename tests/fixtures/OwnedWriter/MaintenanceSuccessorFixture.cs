using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;

internal static class MaintenanceSuccessorFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var pipeName, var parentJson, var oldJobName, var report, var marker]) return 2;
        var parentIdentity = JsonSerializer.Deserialize<ProcessIdentity>(parentJson) ?? throw new IOException("fixture_parent_missing");
        using var parent = new NativeClientBinding(parentIdentity);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var accepted = false; var excluded = false;
        string? failure = null;
        try
        {
            await pipe.ConnectAsync(deadline.Token);
            var bytes = new byte[8]; await pipe.ReadExactlyAsync(bytes, deadline.Token);
            using var origin = await MaintenanceStartupOrigin.ReceiveSuccessorAsync(pipe, parent, parentIdentity,
                BinaryPrimitives.ReadInt64LittleEndian(bytes), deadline.Token);
            accepted = true;
            await pipe.WriteAsync(new byte[] { 1 }, deadline.Token); await pipe.FlushAsync(deadline.Token);
            var release = new byte[1]; await pipe.ReadExactlyAsync(release, deadline.Token);
            if (release[0] != 1) throw new IOException("fixture_successor_release_missing");
            origin.RequireReleasedSuccessor();
            using var childJob = new MaintenanceStartupJob(origin, _ => { });
            try
            {
                var child = childJob.Start(ProcessIdentityReader.Read(Environment.ProcessId).ImagePath, [marker, "maintenance-startup"],
                    Path.GetDirectoryName(report)!, Guid.NewGuid(), Guid.NewGuid(), identity =>
                    {
                        using var process = JobNative.OpenProcess(0x101000, false, identity.ProcessId);
                        using var oldJob = JobNative.OpenJobObjectW(4, false, oldJobName);
                        if (oldJob.IsInvalid || process.IsInvalid || !JobNative.IsProcessInJob(process, oldJob, out var member) || member)
                            throw new IOException("fixture_successor_kept_old_job");
                        excluded = true;
                    });
                while (!File.Exists(marker)) await Task.Delay(10, deadline.Token);
                if (!childJob.CapturePreparedFamily().LivingMembers.Any(item => item.ProcessId == child.ProcessId))
                    throw new IOException("fixture_successor_family_missing");
            }
            finally
            {
                if (!await childJob.StopAsync(TimeSpan.FromSeconds(10))) throw new IOException("fixture_successor_stop_pending");
            }
        }
        catch (IOException error) { failure = error.Message; }
        await File.WriteAllBytesAsync(report, JsonSerializer.SerializeToUtf8Bytes(new
            { accepted, excluded, started = File.Exists(marker), failure }), deadline.Token);
        return 0;
    }
}

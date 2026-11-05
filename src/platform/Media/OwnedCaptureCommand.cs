using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Media;

internal sealed record CaptureCommandResult(int? ExitCode, bool Natural, byte[] StandardOutput,
    byte[] StandardError, OwnedTargetInventory Stopped);

[SupportedOSPlatform("windows")]
internal static class OwnedCaptureCommand
{
    internal static async Task<CaptureCommandResult> RunAsync(ExecutionPlanInterpreterImage image,
        string[] arguments, Guid instanceId, Guid attemptId, int stdoutMaximum, long totalMaximum,
        int stopTimeoutMilliseconds, Action<OwnedWorkerJob, OwnedWorkerIdentity> admit, CancellationToken cancellation)
    {
        using var job = new OwnedWorkerJob();
        using var output = new OwnedProcessOutput();
        job.ObserveProcessIdentities(_ => { });
        Task<byte[]>? stdout = null, stderr = null;
        var quota = new ReadQuota(totalMaximum);
        OwnedWorkerIdentity? root = null; int? exitCode = null; var natural = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            root = job.Start(image.Path, arguments, Path.GetDirectoryName(image.Path)!,
                instanceId, attemptId, identity =>
                {
                    admit(job, identity);
                }, output);
            stdout = ReadAsync(output.StandardOutput, stdoutMaximum, quota);
            stderr = ReadAsync(output.StandardError, MuMuCaptureSequence.MaximumErrorBytes, quota);
            while (exitCode is null)
            {
                cancellation.ThrowIfCancellationRequested();
                if (stdout.IsFaulted || stderr.IsFaulted) throw new IOException("execution_capture_output_rejected");
                exitCode = await job.AwaitRootAsync(root, TimeSpan.FromMilliseconds(25), cancellation).ConfigureAwait(false);
            }
            await Task.WhenAll(stdout, stderr).WaitAsync(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested(); natural = true;
        }
        finally
        {
            try
            {
                var stopped = await PreparedStoppedTarget.StopAsync(job,
                    TimeSpan.FromMilliseconds(stopTimeoutMilliseconds)).ConfigureAwait(false);
                output.CloseUnstartedEnds();
                if (stdout is not null && stderr is not null)
                {
                    try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromMilliseconds(stopTimeoutMilliseconds)).ConfigureAwait(false); }
                    catch
                    {
                        output.Dispose();
                        await ObserveAsync(stdout, stopTimeoutMilliseconds).ConfigureAwait(false); await ObserveAsync(stderr, stopTimeoutMilliseconds).ConfigureAwait(false);
                        throw;
                    }
                }
                // The caller receives no result unless this actual command Job has a complete native stop proof.
                _ = stopped.Use(job, inventory => inventory);
            }
            catch
            {
                output.Dispose();
                if (stdout is not null) await ObserveAsync(stdout, stopTimeoutMilliseconds).ConfigureAwait(false);
                if (stderr is not null) await ObserveAsync(stderr, stopTimeoutMilliseconds).ConfigureAwait(false);
                throw;
            }
        }
        if (root is null || stdout is null || stderr is null) throw new IOException("capture_command_unbound");
        var inventory = job.CaptureTargetInventory();
        var outBytes = await stdout.ConfigureAwait(false); var errorBytes = await stderr.ConfigureAwait(false);
        return new(exitCode, natural, outBytes, errorBytes, inventory);
    }

    private static async Task<byte[]> ReadAsync(Stream input, int maximum, ReadQuota quota)
    {
        using var result = new MemoryStream(); var buffer = new byte[16384];
        while (true)
        {
            var count = await input.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) return result.ToArray();
            quota.Add(count);
            if (result.Length + count > maximum) throw new IOException("execution_capture_output_rejected");
            result.Write(buffer, 0, count);
        }
    }

    private static async Task ObserveAsync(Task task, int timeout)
    {
        try { await task.WaitAsync(TimeSpan.FromMilliseconds(timeout)).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private sealed class ReadQuota(long maximum)
    {
        private long _used;
        internal void Add(int count)
        {
            if (Interlocked.Add(ref _used, count) > maximum) throw new IOException("execution_capture_output_rejected");
        }
    }
}

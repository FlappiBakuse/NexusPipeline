using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
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
        var job = new OwnedWorkerJob();
        OwnedProcessOutput? output = null;
        Task<byte[]>? stdout = null, stderr = null;
        Task<byte[][]>? drain = null;
        var quota = new ReadQuota(totalMaximum);
        var stopTimeout = TimeSpan.FromMilliseconds(stopTimeoutMilliseconds);
        int? exitCode = null;
        OwnedTargetInventory? inventory = null;
        Exception? primary = null;
        try
        {
            job.ObserveProcessIdentities(_ => { });
            cancellation.ThrowIfCancellationRequested();
            output = new OwnedProcessOutput();
            var root = job.Start(image.Path, arguments, Path.GetDirectoryName(image.Path)!,
                instanceId, attemptId, identity => admit(job, identity), output);
            stdout = ReadAsync(output.StandardOutput, stdoutMaximum, quota);
            stderr = ReadAsync(output.StandardError, MuMuCaptureSequence.MaximumErrorBytes, quota);
            drain = Task.WhenAll(stdout, stderr);
            while (exitCode is null)
            {
                cancellation.ThrowIfCancellationRequested();
                if (stdout.IsFaulted || stderr.IsFaulted) throw new IOException("execution_capture_output_rejected");
                exitCode = await job.AwaitRootAsync(root, TimeSpan.FromMilliseconds(25), cancellation).ConfigureAwait(false);
            }
            await drain.WaitAsync(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            var cleanup = new CleanupFailures();
            try
            {
                var stopped = await PreparedStoppedTarget.StopAsync(job, stopTimeout).ConfigureAwait(false);
                inventory = stopped.Use(job, actual => actual);
            }
            catch (Exception error) { cleanup.Add(error); }
            if (output is not null) cleanup.Attempt(output.Dispose);
            if (primary is not null && drain is not null)
                await ObserveDrainAsync(drain, stopTimeout, cleanup).ConfigureAwait(false);
            cleanup.Attempt(job.Dispose);
            cleanup.Preserve(primary);
        }
        if (exitCode is null || drain is null || inventory is null) throw new IOException("capture_command_unbound");
        var bytes = await drain.ConfigureAwait(false);
        return new(exitCode, true, bytes[0], bytes[1], inventory);
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

    private static async Task ObserveDrainAsync(Task drain, TimeSpan timeout, CleanupFailures cleanup)
    {
        try { await drain.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException error)
        {
            cleanup.Add(error);
            _ = drain.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception error)
        {
            if (drain.Exception is { } faults)
                foreach (var fault in faults.InnerExceptions) cleanup.Add(fault);
            else cleanup.Add(error);
        }
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

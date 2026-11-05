using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record AndroidDeviceOutput(int? ExitCode, byte[] StandardOutput, byte[] StandardError);

[SupportedOSPlatform("windows")]
internal static class AndroidDeviceCommand
{
    internal static async Task<AndroidDeviceOutput> RunAsync(PreparedAndroidDevice device, ExecutionPlanInterpreterImage image,
        string[] arguments, Action<Action<OwnedWorkerJob, OwnedWorkerIdentity>> useParent, TimeSpan timeout,
        TimeSpan stopTimeout, Func<bool> expired, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        using var job = new OwnedWorkerJob();
        using var output = new OwnedProcessOutput();
        job.ObserveProcessIdentities(_ => { });
        Task<byte[]>? stdout = null, stderr = null;
        OwnedWorkerIdentity? root = null;
        int? exitCode = null;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (expired()) throw new IOException("execution_device_budget_expired");
            useParent((parent, primary) => device.Use(_ => parent.UseObservedMember(
                new(primary.ProcessId, primary.CreatedAtUtc, primary.ImagePath, primary.ImageSha256, primary.UserSid, primary.IsElevated), () =>
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (expired()) throw new IOException("execution_device_budget_expired");
                    root = job.StartNested(parent, image.Path, arguments, Path.GetDirectoryName(image.Path)!,
                        device.Primary.InstanceId, device.Primary.AttemptId, actual =>
                        {
                            if (actual.ImageSha256 != image.Sha256 || !string.Equals(actual.ImagePath, image.Path, StringComparison.OrdinalIgnoreCase))
                                throw new IOException("execution_device_tool_changed");
                        }, output);
                    return true;
                })));
            stdout = ReadAsync(output.StandardOutput);
            stderr = ReadAsync(output.StandardError);
            while (exitCode is null)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (expired()) throw new IOException("execution_device_budget_expired");
                if (stdout.IsFaulted || stderr.IsFaulted) throw new IOException("execution_device_output_rejected");
                exitCode = await job.AwaitRootAsync(root!, TimeSpan.FromMilliseconds(20), deadline.Token).ConfigureAwait(false);
            }
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                var stopped = await PreparedStoppedTarget.StopAsync(job, stopTimeout).ConfigureAwait(false);
                _ = stopped.Use(job, inventory => inventory);
                output.CloseUnstartedEnds();
                if (stdout is not null && stderr is not null)
                    await Task.WhenAll(stdout, stderr).WaitAsync(stopTimeout).ConfigureAwait(false);
            }
            catch
            {
                output.Dispose();
                if (stdout is not null) await ObserveAsync(stdout, stopTimeout).ConfigureAwait(false);
                if (stderr is not null) await ObserveAsync(stderr, stopTimeout).ConfigureAwait(false);
                throw;
            }
        }
        if (root is null || stdout is null || stderr is null) throw new IOException("execution_device_command_unbound");
        return new(exitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadAsync(Stream source)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) return result.ToArray();
            if (result.Length + count > 65536) throw new IOException("execution_device_output_limit");
            result.Write(buffer, 0, count);
        }
    }

    private static async Task ObserveAsync(Task pending, TimeSpan timeout)
    {
        try { await pending.WaitAsync(timeout).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or TimeoutException) { }
    }
}

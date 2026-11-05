using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Execution.Judgement;

[SupportedOSPlatform("windows")]
internal static class JudgementPythonInvocation
{
    internal static async Task<BusinessJudgementResult> ExecuteAsync(JudgementWorkerInitialization initialization,
        JudgementWorkerInvocation invocation, JudgementConsole console, JudgementProbeBridge probes, Stream brokerInput, Stream brokerOutput, CancellationToken cancellationToken)
    {
        var interpreter = initialization.Python ?? throw new IOException("judgement_python_missing");
        using var job = new OwnedWorkerJob();
        using var output = new OwnedProcessOutput(redirectInput: true);
        Task<byte[]?>? response = null;
        Task? errors = null;
        try
        {
            var root = job.Start(interpreter.Path, ["-I", "-B", "-u", "-c", JudgementPythonBootstrap.Source], initialization.WorkingDirectory,
                invocation.Identity.Primary.InstanceId, invocation.Identity.Primary.AttemptId, identity =>
                AdmitChildAsync(identity, invocation, brokerInput, brokerOutput, cancellationToken).GetAwaiter().GetResult(), output);
            response = ReadReplyAsync(output, probes, cancellationToken);
            errors = DrainErrorsAsync(output.StandardError, cancellationToken);
            await JudgementWorkerFraming.WriteAsync(output.StandardInput,
                JudgementWorkerProtocol.Encode(new JudgementPythonRequest(initialization.Script, invocation.Input, invocation.Identity, invocation.Generation, invocation.InputSha256, invocation.ProbeEvidenceAllowance)), cancellationToken,
                JudgementWorkerProtocol.MaximumInvocationBytes).ConfigureAwait(false);
            var bytes = await response.ConfigureAwait(false) ?? throw new IOException("judgement_python_result_missing");
            output.CompleteInput();
            var exit = await job.AwaitRootAsync(root, TimeSpan.FromSeconds(initialization.InvocationTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            if (exit is null) throw new OperationCanceledException("judgement_python_timeout", cancellationToken);
            if (!await job.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)) throw new IOException("judgement_python_family_not_stopped");
            if (exit != 0) throw new IOException("judgement_python_exit_failed");
            if (await JudgementWorkerFraming.ReadAsync(output.StandardOutput, cancellationToken).ConfigureAwait(false) is not null)
                throw new IOException("judgement_python_extra_result");
            await errors.ConfigureAwait(false);
            var decoded = JudgementWorkerProtocol.ReadPythonResponse(bytes); console.Import(decoded.Console);
            if (decoded.FaultCode is not null) throw new IOException(decoded.FaultCode);
            return decoded.Result!;
        }
        finally
        {
            try
            {
                if (!await job.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
                    throw new IOException("judgement_python_family_not_stopped");
            }
            finally
            {
                output.Dispose();
                if (response is not null) await ObserveAsync(response).ConfigureAwait(false);
                if (errors is not null) await ObserveAsync(errors).ConfigureAwait(false);
            }
        }
    }

    private static async Task<byte[]?> ReadReplyAsync(OwnedProcessOutput output, JudgementProbeBridge probes, CancellationToken cancellation)
    {
        while (true)
        {
            var bytes = await JudgementWorkerFraming.ReadAsync(output.StandardOutput, cancellation).ConfigureAwait(false);
            if (bytes is null) return null;
            using var document = Serialization.StrictJson.Parse(bytes);
            if (!document.RootElement.TryGetProperty("format", out var format) || format.GetString() != JudgementProbeProtocol.RequestFormat)
                return bytes;
            var request = JudgementWorkerProtocol.Decode<JudgementProbeRequest>(bytes);
            var reply = await probes.ForwardAsync(request).ConfigureAwait(false);
            await JudgementWorkerHost.WriteAsync(output.StandardInput, reply, cancellation).ConfigureAwait(false);
        }
    }

    private static async Task AdmitChildAsync(OwnedWorkerIdentity child, JudgementWorkerInvocation invocation,
        Stream input, Stream output, CancellationToken cancellationToken)
    {
        var identity = new ProcessIdentity(child.ProcessId, child.CreatedAtUtc, child.ImagePath, child.ImageSha256, child.UserSid, child.IsElevated);
        await JudgementWorkerHost.WriteAsync(output, new WorkerChildReady(JudgementWorkerProtocol.ChildReadyFormat,
            1, invocation.Identity, invocation.Generation, identity), cancellationToken).ConfigureAwait(false);
        var admission = JudgementWorkerProtocol.Decode<WorkerChildAdmission>(
            await JudgementWorkerFraming.ReadAsync(input, cancellationToken).ConfigureAwait(false) ?? throw new IOException("judgement_python_admission_missing"));
        if (admission.Format != JudgementWorkerProtocol.ChildAdmissionFormat || admission.Version != 1 ||
            admission.Identity != invocation.Identity || admission.Generation != invocation.Generation || admission.Process != identity)
            throw new IOException("judgement_python_admission_mismatch");
    }

    private static async Task DrainErrorsAsync(Stream input, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            total = checked(total + count);
            if (total > 20 * 1024 * 1024) throw new IOException("judgement_python_error_output_exceeded");
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

}

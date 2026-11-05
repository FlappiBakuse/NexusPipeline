using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Execution.Judgement;

[SupportedOSPlatform("windows")]
public static class JudgementWorkerHost
{
    public static async Task<int> RunAsync()
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            return await RunAsync(input, output, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or
            InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("judgement_worker_input_rejected");
            return 1;
        }
    }

    internal static async Task<int> RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var initialization = JudgementWorkerProtocol.Decode<JudgementWorkerInitialization>(
            await JudgementWorkerFraming.ReadAsync(input, cancellationToken).ConfigureAwait(false) ?? throw new IOException("judgement_worker_initialization_missing"));
        using var lease = new JudgementInterpreterLease(initialization);
        var actual = RequireOwnedBroker(initialization);
        await WriteAsync(output, new JudgementWorkerReady(JudgementWorkerProtocol.ReadyFormat, 1, initialization.Identity, actual), cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < initialization.MaximumInvocations; index++)
        {
            var bytes = await JudgementWorkerFraming.ReadAsync(input, cancellationToken, JudgementWorkerProtocol.MaximumInvocationBytes).ConfigureAwait(false);
            if (bytes is null) return 0;
            var invocation = JudgementWorkerProtocol.Decode<JudgementWorkerInvocation>(bytes);
            JudgementWorkerProtocol.Require(invocation, initialization.Identity, index + 1L);
            var response = await ExecuteAsync(initialization, invocation, input, output, cancellationToken).ConfigureAwait(false);
            await WriteAsync(output, response, cancellationToken).ConfigureAwait(false);
            if (invocation.IsFinal) return 0;
        }
        if (await JudgementWorkerFraming.ReadAsync(input, cancellationToken).ConfigureAwait(false) is not null)
            throw new IOException("judgement_worker_invocation_capacity_exceeded");
        return 0;
    }

    private static async Task<JudgementWorkerResponse> ExecuteAsync(JudgementWorkerInitialization initialization,
        JudgementWorkerInvocation invocation, Stream input, Stream output, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(initialization.InvocationTimeoutSeconds));
        var console = new JudgementConsole();
        var probes = new JudgementProbeBridge(invocation, input, output, deadline.Token);
        JudgementWorkerResponse response;
        try
        {
            var result = initialization.Language == "javascript" ? BoundedJudgementScript.Execute(initialization, invocation, console, probes, deadline.Token) :
                await JudgementPythonInvocation.ExecuteAsync(initialization, invocation, console, probes, input, output, deadline.Token).ConfigureAwait(false);
            response = new(JudgementWorkerProtocol.ResponseFormat, 1, invocation.Identity, invocation.Generation,
                invocation.IsFinal, invocation.InputSha256, result, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response = Fault(invocation, "judgement_worker_timeout");
        }
        catch (TimeoutException)
        {
            response = Fault(invocation, "judgement_worker_timeout");
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception || initialization.Language == "javascript" &&
            error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            response = Fault(invocation, "judgement_worker_execution_failed");
        }
        return response with { Console = console.Entries, ProbeCount = probes.Exchanges.Count,
            ProbeTranscriptSha256 = JudgementProbeProtocol.Hash(probes.Exchanges) };
    }

    private static ProcessIdentity RequireOwnedBroker(JudgementWorkerInitialization initialization)
    {
        var actual = ProcessIdentityReader.Read(Environment.ProcessId);
        if (!string.Equals(actual.ImagePath, initialization.Broker.Path, StringComparison.OrdinalIgnoreCase) ||
            actual.ImageSha256 != initialization.Broker.Sha256) throw new IOException("judgement_worker_broker_mismatch");
        using var job = JobNative.OpenJobObjectW(4, false, initialization.Identity.WorkerJobName);
        using var process = JobNative.OpenProcess(0x1000, false, Environment.ProcessId);
        if (job.IsInvalid || process.IsInvalid || !JobNative.IsProcessInJob(process, job, out var member) || !member)
            throw new IOException("judgement_worker_job_mismatch");
        return actual;
    }

    internal static Task WriteAsync<T>(Stream output, T value, CancellationToken cancellationToken) =>
        JudgementWorkerFraming.WriteAsync(output, JudgementWorkerProtocol.Encode(value), cancellationToken,
            JudgementWorkerProtocol.FrameLimit(typeof(T)));

    private static JudgementWorkerResponse Fault(JudgementWorkerInvocation invocation, string code) =>
        new(JudgementWorkerProtocol.ResponseFormat, 1, invocation.Identity, invocation.Generation, invocation.IsFinal, invocation.InputSha256, null, code);
}

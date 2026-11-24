using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace NexusPipeline.Platform.Execution.Judgement;

internal static class JudgementPythonBootstrap
{
    internal const string Source = """
import contextlib
import io
import json
import struct
import sys

def exact(stream, count):
    result = bytearray()
    while len(result) < count:
        part = stream.read(count - len(result))
        if not part:
            raise ValueError('judgement_python_input_incomplete')
        result.extend(part)
    return bytes(result)

class ProbeApi:
    def __init__(self, context, source, target):
        object.__setattr__(self, 'context', context)
        object.__setattr__(self, 'source', source)
        object.__setattr__(self, 'target', target)
        object.__setattr__(self, 'ordinal', 0)

    def __setattr__(self, name, value):
        raise AttributeError('judgement_probe_api_frozen')

    def processes(self, options=None):
        return self.call('processes', {} if options is None else options)

    def windows(self, options=None):
        return self.call('windows', {} if options is None else options)

    def httpGet(self, options=None):
        return self.call('httpGet', {} if options is None else options)

    def call(self, method, options):
        object.__setattr__(self, 'ordinal', self.ordinal + 1)
        if self.ordinal > 16 or not isinstance(options, dict):
            raise ValueError('judgement_probe_options_invalid')
        request = {'format': 'nexus.execution.judgement-probe-request', 'version': 1,
            'identity': self.context['identity'], 'generation': self.context['generation'],
            'inputSha256': self.context['inputSha256'], 'ordinal': self.ordinal,
            'method': method, 'options': options}
        payload = json.dumps(request, ensure_ascii=False, allow_nan=False, separators=(',', ':')).encode('utf-8', errors='strict')
        maximum = 16384 if method == 'httpGet' else 8192
        if not 1 <= len(payload) <= maximum:
            raise ValueError('judgement_probe_options_invalid')
        self.target.write(struct.pack('<i', len(payload)))
        self.target.write(payload)
        self.target.flush()
        size = struct.unpack('<i', exact(self.source, 4))[0]
        if not 1 <= size <= 16777216:
            raise ValueError('judgement_probe_reply_invalid')
        reply = json.loads(exact(self.source, size).decode('utf-8', errors='strict'))
        if set(reply) != {'format', 'version', 'identity', 'generation', 'inputSha256', 'ordinal', 'result', 'faultCode'}:
            raise ValueError('judgement_probe_reply_invalid')
        if reply['format'] != 'nexus.execution.judgement-probe-reply' or reply['version'] != 1 or any(
            reply[key] != request[key] for key in ('identity', 'generation', 'inputSha256', 'ordinal')):
            raise ValueError('judgement_probe_reply_invalid')
        if reply['faultCode'] is not None:
            raise ValueError('judgement_probe_failed')
        if not isinstance(reply['result'], dict):
            raise ValueError('judgement_probe_reply_invalid')
        return reply['result']

class NexusApi:
    __slots__ = ('probes',)
    def __init__(self, probes):
        object.__setattr__(self, 'probes', probes)
    def __setattr__(self, name, value):
        raise AttributeError('judgement_api_frozen')

class ConsoleState:
    def __init__(self):
        self.count = 0
        self.entries = []

class LimitedText(io.TextIOBase):
    def __init__(self, state, level):
        self.state = state
        self.level = level
    def writable(self):
        return True
    def write(self, value):
        size = len(value.encode('utf-8', errors='strict'))
        if self.state.count + size > 16384 or len(self.state.entries) >= 128:
            raise ValueError('judgement_python_text_output_exceeded')
        if value:
            self.state.entries.append({'level': self.level, 'text': value})
            self.state.count += size
        return len(value)

wire_input = sys.stdin.buffer
wire_output = sys.stdout.buffer
size = struct.unpack('<i', exact(wire_input, 4))[0]
if not 1 <= size <= 33554432:
    raise ValueError('judgement_python_input_size_invalid')
request = json.loads(exact(wire_input, size).decode('utf-8', errors='strict'))
if set(request) != {'script', 'input', 'identity', 'generation', 'inputSha256', 'probeEvidenceAllowance'} or not isinstance(request['script'], str) or not isinstance(request['input'], dict):
    raise ValueError('judgement_python_input_unrecognized')
namespace = {'__name__': '__nexus_judgement__', 'nexus': NexusApi(ProbeApi(request, wire_input, wire_output))}
console = ConsoleState()
result = None
fault = None
failure_type = None
try:
    with contextlib.redirect_stdout(LimitedText(console, 'log')), contextlib.redirect_stderr(LimitedText(console, 'error')):
        exec(compile(request['script'], '<frozen-judgement>', 'exec'), namespace)
        result = namespace['nexus_judge'](request['input'])
except Exception as error:
    fault = 'judgement_worker_execution_failed'
    name = type(error).__name__
    failure_type = name if name in ('SyntaxError', 'ValueError', 'TypeError', 'KeyError', 'NameError', 'RuntimeError', 'OSError', 'AssertionError') else 'Exception'
payload = json.dumps({'result': result, 'faultCode': fault, 'failureType': failure_type, 'console': console.entries},
    ensure_ascii=False, allow_nan=False, separators=(',', ':')).encode('utf-8', errors='strict')
if not 1 <= len(payload) <= 131072:
    raise ValueError('judgement_python_output_size_invalid')
wire_output.write(struct.pack('<i', len(payload)))
wire_output.write(payload)
wire_output.flush()
""";
}

[SupportedOSPlatform("windows")]
internal static class JudgementPythonInvocation
{
    internal static async Task<BusinessJudgementResult> ExecuteAsync(JudgementWorkerInitialization initialization,
        JudgementWorkerInvocation invocation, JudgementConsole console, JudgementDiagnostics diagnostics,
        JudgementProbeBridge probes, Stream brokerInput, Stream brokerOutput, CancellationToken cancellationToken)
    {
        var interpreter = initialization.Python ?? throw new IOException("judgement_python_missing");
        var job = new OwnedWorkerJob();
        OwnedProcessOutput? output = null;
        Task<JudgementWorkerProtocol.DispatchFrame?>? response = null;
        Task? errors = null;
        Exception? primary = null;
        var stage = "python-start";
        string? scriptFailure = null;
        long stderrBytes = 0;
        var familyStopped = false;
        try
        {
            output = new OwnedProcessOutput(redirectInput: true);
            var root = job.Start(interpreter.Path, ["-I", "-B", "-u", "-c", JudgementPythonBootstrap.Source], initialization.WorkingDirectory,
                invocation.Identity.Primary.InstanceId, invocation.Identity.Primary.AttemptId, identity =>
                AdmitChildAsync(identity, invocation, brokerInput, brokerOutput, cancellationToken).GetAwaiter().GetResult(), output);
            response = ReadReplyAsync(output, probes, cancellationToken);
            errors = DrainErrorsAsync(output.StandardError, count => Interlocked.Add(ref stderrBytes, count), cancellationToken);
            stage = "python-send";
            await JudgementWorkerFraming.WriteAsync(output.StandardInput,
                JudgementWorkerProtocol.Encode(new JudgementPythonRequest(initialization.Script, invocation.Input, invocation.Identity, invocation.Generation, invocation.InputSha256, invocation.ProbeEvidenceAllowance)), cancellationToken,
                JudgementWorkerProtocol.MaximumInvocationBytes).ConfigureAwait(false);
            stage = "python-reply";
            var reply = await response.ConfigureAwait(false) ?? throw new IOException("judgement_python_result_missing");
            stage = "python-exit";
            output.CompleteInput();
            var exit = await job.AwaitRootAsync(root, TimeSpan.FromSeconds(initialization.InvocationTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            if (exit is null) throw new OperationCanceledException("judgement_python_timeout", cancellationToken);
            stage = "python-stop";
            familyStopped = await job.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (!familyStopped) throw new IOException("judgement_python_family_not_stopped");
            stage = "python-exit";
            if (exit != 0) throw new IOException("judgement_python_exit_failed");
            if (await JudgementWorkerFraming.ReadAsync(output.StandardOutput, cancellationToken).ConfigureAwait(false) is not null)
                throw new IOException("judgement_python_extra_result");
            stage = "python-drain"; await errors.ConfigureAwait(false);
            stage = "python-decode";
            var decoded = JudgementWorkerProtocol.ReadPythonResponse(reply); console.Import(decoded.Console);
            if (decoded.FaultCode is not null) { scriptFailure = decoded.FailureType; throw new IOException(decoded.FaultCode); }
            return decoded.Result!;
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            var cleanup = new List<(string Stage, Exception Error)>();
            try
            {
                if (!familyStopped && !await job.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
                    throw new IOException("judgement_python_family_not_stopped");
            }
            catch (Exception error) { cleanupFailure = error; cleanup.Add(("python-stop", error)); }
            try { output?.Dispose(); }
            catch (Exception error) { cleanupFailure ??= error; cleanup.Add(("python-release", error)); }
            await ObserveAsync(response).ConfigureAwait(false);
            // The read can finish while the invocation is stopping, before it reaches typed decoding.
            if (response is { IsCompletedSuccessfully: true }) response.Result?.Dispose();
            await ObserveAsync(errors).ConfigureAwait(false);
            try { job.Dispose(); }
            catch (Exception error) { cleanupFailure ??= error; cleanup.Add(("python-release", error)); }

            var bytes = Interlocked.Read(ref stderrBytes);
            if (scriptFailure is not null) diagnostics.AddPythonFault(scriptFailure, bytes);
            else if (primary is not null && (primary is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
                diagnostics.Add(stage, "failed", primary, bytes);
            foreach (var (cleanupStage, error) in cleanup) diagnostics.Add(cleanupStage, "cleanup", error, bytes);
            if (primary is null && cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();

            async Task ObserveAsync(Task? task)
            {
                if (task is null) return;
                try { await task.ConfigureAwait(false); }
                catch (Exception error) when (ReferenceEquals(error, primary) || error is OperationCanceledException && cancellationToken.IsCancellationRequested) { }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
                { cleanup.Add(("python-drain", error)); }
                catch (Exception error) { cleanupFailure ??= error; cleanup.Add(("python-drain", error)); }
            }
        }
    }

    private static async Task<JudgementWorkerProtocol.DispatchFrame?> ReadReplyAsync(OwnedProcessOutput output, JudgementProbeBridge probes, CancellationToken cancellation)
    {
        while (true)
        {
            var bytes = await JudgementWorkerFraming.ReadAsync(output.StandardOutput, cancellation).ConfigureAwait(false);
            if (bytes is null) return null;
            JudgementWorkerProtocol.DispatchFrame? frame = new(bytes);
            try
            {
                if (frame.Format != JudgementProbeProtocol.RequestFormat)
                {
                    var response = frame;
                    frame = null;
                    return response;
                }
                var request = frame.Decode<JudgementProbeRequest>();
                var reply = await probes.ForwardAsync(request).ConfigureAwait(false);
                await JudgementWorkerHost.WriteAsync(output.StandardInput, reply, cancellation).ConfigureAwait(false);
            }
            finally { frame?.Dispose(); }
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

    private static async Task DrainErrorsAsync(Stream input, Action<int> onBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        long total = 0;
        int count;
        try
        {
            while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                onBytes(count); total += count;
                if (total > 20 * 1024 * 1024) throw new IOException("judgement_python_error_output_exceeded");
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

}

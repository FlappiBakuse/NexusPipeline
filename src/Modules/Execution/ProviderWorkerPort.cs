using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using RandomNumberGenerator = System.Security.Cryptography.RandomNumberGenerator;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Execution;

/// <summary>One attempt's controlled worker. Host independently confirms owned writers stopped.</summary>
internal sealed class ProviderWorkerPort : IPluginProviderWorkerPort
{
    private readonly string _pluginRoot;
    private readonly string _projectRoot;
    private readonly string _executionId;
    private readonly string _recordId;
    private readonly int _attempt;
    private readonly Action<string, LogLevel>? _log;
    private readonly TimeSpan _startupTimeout;
    private bool _used;
    internal bool CleanupConfirmed { get; private set; } = true;
    internal bool TerminalReceived { get; private set; }
    internal string TerminalStatus { get; private set; } = "not_started";

    internal ProviderWorkerPort(string pluginRoot, string projectRoot, string executionId,
        string recordId, int attempt, Action<string, LogLevel>? log, TimeSpan? startupTimeout = null)
    {
        _pluginRoot = Path.GetFullPath(pluginRoot); _projectRoot = Path.GetFullPath(projectRoot);
        _executionId = executionId; _recordId = recordId; _attempt = attempt; _log = log;
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(30);
        if (_startupTimeout <= TimeSpan.Zero || _startupTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
    }

    internal static string ScopedPath(string root, string relative, bool executable = false)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("provider.path_scope");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || executable && !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("provider.path_scope");
        for (string? part = full; part is not null; part = Path.GetDirectoryName(part))
        {
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("provider.path_link");
            if (string.Equals(part.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) break;
        }
        return full;
    }

    public async Task<PluginProviderWorkerResult> RunAsync(PluginProviderWorkerRequest request,
        Func<PluginProviderEvent, ValueTask> onEvent, CancellationToken cancellationToken)
    {
        if (_used) throw new InvalidOperationException("provider.worker_already_used");
        _used = true;
        string executable = ScopedPath(_pluginRoot, request.RelativeExecutable, true);
        string working = Path.GetFullPath(request.WorkingDirectory);
        if (!string.Equals(working, _projectRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("provider.working_directory_scope");
        string session = Guid.NewGuid().ToString("N");
        var bootstrap = new PluginWorkerBootstrap("nxp-e-" + Guid.NewGuid().ToString("N"),
            "nxp-c-" + Guid.NewGuid().ToString("N"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            _executionId, _recordId, _attempt, session, request.Input.DeepClone().AsObject());
        string bootstrapJson = JsonSerializer.Serialize(bootstrap, PluginWorkerProtocol.Json);
        if (System.Text.Encoding.UTF8.GetByteCount(bootstrapJson) > PluginWorkerProtocol.MaximumFrameBytes)
            throw new InvalidDataException("provider.bootstrap_size");
        using var events = new NamedPipeServerStream(bootstrap.EventPipe, PipeDirection.In,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var control = new NamedPipeServerStream(bootstrap.ControlPipe, PipeDirection.Out,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using ProcessOwnership? ownership = ProcessOwnership.TryCreate("provider worker");
        if (ownership is null) throw new InvalidOperationException("provider.worker_ownership_unavailable");
        var psi = SystemActions.BuildScriptStartInfo(executable, working, request.Arguments, true, true);
        psi.RedirectStandardInput = true;
        using Process process = SystemActions.StartOwnedProcess(psi, ownership)
            ?? throw new InvalidOperationException("provider.worker_start_failed");
        // Project code can echo bootstrap secrets. Drain console streams but publish
        // only the whitelisted authenticated structured protocol to user history.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var identity = ProcessIdentity.Capture(process);
        bool cancelled = false;
        string kind = "fault";
        using var receiveStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? receive = null;
        Task? readinessDeadline = null;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        long startupStamp = Stopwatch.GetTimestamp();
        try
        {
            if (identity is null) throw new InvalidOperationException("provider.worker_identity_unavailable");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            TimeSpan startupRemaining = _startupTimeout - Stopwatch.GetElapsedTime(startupStamp);
            if (startupRemaining <= TimeSpan.Zero) throw new TimeoutException();
            startup.CancelAfter(startupRemaining);
            await process.StandardInput.WriteLineAsync(bootstrapJson.AsMemory(), startup.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            Task connected = Task.WhenAll(events.WaitForConnectionAsync(startup.Token), control.WaitForConnectionAsync(startup.Token));
            Task startupExit = process.WaitForExitAsync();
            if (await Task.WhenAny(connected, startupExit).ConfigureAwait(false) == startupExit)
                throw new InvalidDataException("provider.worker_exited_before_handshake:" + process.ExitCode);
            await connected.ConfigureAwait(false);
            VerifyClient(events, process.Id); VerifyClient(control, process.Id);
            var hello = await PluginWorkerProtocol.ReadAsync(events, startup.Token).ConfigureAwait(false);
            PluginWorkerProtocol.Validate(hello, bootstrap, "worker_to_host", 1);
            if (hello.Kind != "handshake" || hello.Payload["nonce"]?.GetValue<string>() != bootstrap.Nonce)
                throw new InvalidDataException("worker.handshake");
            await PluginWorkerProtocol.WriteAsync(control, Frame(bootstrap, 1, "start", new()), startup.Token).ConfigureAwait(false);
            receive = ReceiveAsync(events, bootstrap, onEvent, ready, receiveStop.Token);
            TimeSpan remaining = _startupTimeout - Stopwatch.GetElapsedTime(startupStamp);
            readinessDeadline = EnforceReadinessAsync(ready.Task, remaining, receiveStop.Token);
            Task exit = process.WaitForExitAsync();
            Task cancel = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Task first = await Task.WhenAny(receive, exit, cancel, readinessDeadline).ConfigureAwait(false);
            if (first == readinessDeadline && !cancellationToken.IsCancellationRequested)
                await readinessDeadline.ConfigureAwait(false);
            if (first == cancel)
            {
                cancelled = true;
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await PluginWorkerProtocol.WriteAsync(control, Frame(bootstrap, 2, "cancel", new()), stop.Token).ConfigureAwait(false);
                    await exit.WaitAsync(stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            }
            else if (first == receive)
            {
                await receive.ConfigureAwait(false);
                try { await exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { TerminalStatus = "fault"; }
            }
            else
            {
                try { await receive.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or TimeoutException) { }
            }
            kind = cancelled ? "cancelled" : TerminalReceived && TerminalStatus != "fault" ? "completed" : "fault";
        }
        catch (OperationCanceledException) { cancelled = cancellationToken.IsCancellationRequested; kind = cancelled ? "cancelled" : "fault"; }
        catch (TimeoutException) { kind = "fault"; _log?.Invoke("provider.worker_ready_timeout", LogLevel.Warn); }
        finally
        {
            // Plugin flags are not proof. Reuse the existing exact-identity and owned Job cleanup.
            CleanupConfirmed = SystemActions.KillEditProcess(ownership, identity, process.Id, executable,
                "provider worker", rounds: 2, intervalMs: 100, stableSeconds: 1);
            receiveStop.Cancel();
            if (readinessDeadline is not null)
            {
                try { await readinessDeadline.ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            }
            if (receive is not null)
            {
                try { await receive.ConfigureAwait(false); }
                catch (Exception) when (receive.IsCanceled || receive.IsFaulted) { /* observed; the owning attempt keeps its terminal/cancel decision */ }
            }
        }
        return new(kind, process.HasExited ? process.ExitCode : null, CleanupConfirmed);
    }

    private async Task ReceiveAsync(Stream events, PluginWorkerBootstrap bootstrap,
        Func<PluginProviderEvent, ValueTask> onEvent, TaskCompletionSource<bool> ready, CancellationToken token)
    {
        for (long sequence = 2; ; sequence++)
        {
            var frame = await PluginWorkerProtocol.ReadAsync(events, token).ConfigureAwait(false);
            PluginWorkerProtocol.Validate(frame, bootstrap, "worker_to_host", sequence);
            if (frame.Kind is not ("ready" or "task_event" or "progress" or "cancel_ack" or "completed" or "fault"))
                throw new InvalidDataException("worker.message_kind");
            if (frame.Kind == "ready" && !ready.TrySetResult(true))
                throw new InvalidDataException("worker.duplicate_ready");
            if (frame.Kind is "task_event" or "completed" && !ready.Task.IsCompletedSuccessfully)
                throw new InvalidDataException("worker.not_ready");
            var evidence = frame.Payload.DeepClone().AsObject();
            evidence["sessionId"] = bootstrap.SessionId;
            evidence["recordId"] = bootstrap.RecordId;
            evidence["attemptNumber"] = bootstrap.AttemptNumber;
            token.ThrowIfCancellationRequested();
            await onEvent(new(frame.Kind, frame.SourceSequence, frame.Payload["taskId"]?.GetValue<string>(),
                frame.Payload["status"]?.GetValue<string>(), evidence)).ConfigureAwait(false);
            if (frame.Kind is "completed" or "fault")
            {
                TerminalStatus = frame.Kind == "fault" ? "fault" : frame.Payload["status"]?.GetValue<string>() ?? "unknown";
                TerminalReceived = true; return;
            }
        }
    }

    private static async Task EnforceReadinessAsync(Task ready, TimeSpan remaining, CancellationToken token)
    {
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("provider.worker_ready_timeout");
        await ready.WaitAsync(remaining, token).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
    }

    private static PluginWorkerEnvelope Frame(PluginWorkerBootstrap bootstrap, long sequence, string kind, JsonObject payload) =>
        new(1, bootstrap.ExecutionId, bootstrap.RecordId, bootstrap.AttemptNumber, bootstrap.SessionId,
            "host_to_worker", sequence, kind, payload);

    private static void VerifyClient(NamedPipeServerStream pipe, int pid)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint actual) || actual != pid)
            throw new InvalidDataException("worker.client_identity");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
}

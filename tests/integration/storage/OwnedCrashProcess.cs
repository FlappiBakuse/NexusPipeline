using System.Diagnostics;
using System.Text.Json;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.StorageTests;

internal sealed record OwnedCrashStop(bool CompleteJobStopped, OwnedWorkerIdentity Identity, IReadOnlyList<ProcessIdentity> Family,
    WorkerJobAccounting Accounting);

[SupportedOSPlatform("windows")]
internal sealed class OwnedCrashProcess : IDisposable
{
    private readonly OwnedWorkerJob _job = new();
    private readonly ProcessStartObserver _observer;
    private readonly Dictionary<(int, DateTime), ProcessIdentity> _members = [];
    private readonly object _gate = new();
    private Process? _process;
    internal OwnedWorkerIdentity? Identity { get; private set; }

    internal OwnedCrashProcess()
    {
        _observer = _job.ObserveProcessStarts(pid =>
        {
            var member = ProcessIdentityReader.Read(pid);
            lock (_gate) _members[(pid, member.CreatedAtUtc)] = member;
        });
    }

    internal OwnedWorkerIdentity Start(IReadOnlyList<string> arguments, string root, Guid instanceId, Guid journalId, string identityPath)
    {
        if (Identity is not null) throw new InvalidOperationException("A crash fixture owns one root process");
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new InvalidOperationException();
        Identity = _job.Start(executable, arguments, root, instanceId, journalId, identity =>
        {
            _process = Process.GetProcessById(identity.ProcessId);
            _ = _process.Handle;
            if (_process.StartTime.ToUniversalTime() != identity.CreatedAtUtc) throw new IOException("Crash fixture root identity changed");
            using var output = new FileStream(identityPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            output.Write(JsonSerializer.SerializeToUtf8Bytes(identity));
            output.Flush(flushToDisk: true);
        });
        return Identity;
    }

    internal async Task<OwnedCrashStop> StopAsync()
    {
        if (Identity is null) throw new InvalidOperationException("No crash fixture has started");
        if (!_observer.Drain(TimeSpan.FromSeconds(5))) throw new IOException("Crash fixture process attribution did not drain");
        if (!await _job.StopAsync(TimeSpan.FromSeconds(5))) throw new IOException("Crash fixture family did not stop");
        if (!_observer.Drain(TimeSpan.FromSeconds(5))) throw new IOException("Crash fixture exit attribution did not drain");
        var accounting = _job.GetAccounting();
        ProcessIdentity[] family;
        lock (_gate) family = _members.Values.ToArray();
        if (_observer.Failures.Length != 0 || family.Length != accounting.TotalProcesses || accounting.ActiveProcesses != 0 || _job.ActiveProcessIds().Count != 0)
            throw new IOException("Crash fixture complete family attribution failed");
        return new(true, Identity, Array.AsReadOnly(family), accounting);
    }

    internal async Task<int> AwaitExitAsync()
    {
        if (_process is null) throw new InvalidOperationException("No retained crash fixture process");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _process.WaitForExitAsync(deadline.Token);
        return _process.ExitCode;
    }

    internal Task<JsonElement> ReadReportAsync(string path, TimeSpan? timeout = null)
        => ReadReportAsync(path, timeout, () => _process is not null && _process.HasExited);

    internal static async Task<JsonElement> ReadReportAsync(string path, TimeSpan? timeout = null, Func<bool>? producerExited = null)
    {
        var maximum = timeout ?? TimeSpan.FromSeconds(15);
        if (maximum <= TimeSpan.Zero || maximum > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < maximum)
        {
            if (File.Exists(path))
            {
                try { using var document = JsonDocument.Parse(File.ReadAllBytes(path)); return document.RootElement.Clone(); }
                catch (Exception error) when (error is JsonException or IOException) { }
            }
            if (producerExited?.Invoke() == true) throw new IOException("Owned crash fixture exited before its report: " + path);
            await Task.Delay(20);
        }
        throw new TimeoutException("Owned crash fixture did not produce its bounded report");
    }

    public void Dispose()
    {
        _observer.Dispose();
        _job.Dispose();
        _process?.Dispose();
    }
}

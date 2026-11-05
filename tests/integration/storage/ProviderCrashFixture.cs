using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ProviderCrashFixture : IAsyncDisposable
{
    internal ProviderRuntimeFixture Fixture { get; }
    internal PlanBuilderFixture Source => Fixture.Source;
    internal byte[] OriginalBytes { get; }
    internal PhysicalObjectIdentity OriginalIdentity { get; }
    internal Guid Parent { get; private set; }
    internal Guid Attempt { get; private set; }
    internal StepEvidenceBinding Binding { get; private set; } = null!;
    internal ProcessIdentity Root { get; private set; } = null!;
    internal ProviderLauncherState? Launcher { get; private set; }
    internal ProcessIdentity? LauncherRoot { get; private set; }
    private Process? _owner;
    private Task<string>? _stdout, _stderr;
    private SafeKernelHandle? _launcherJob;
    private ProcessIdentity? _ownerIdentity;

    private ProviderCrashFixture(ProviderRuntimeFixture fixture)
    {
        Fixture = fixture; OriginalBytes = File.ReadAllBytes(Source.Configuration);
        OriginalIdentity = PhysicalObjectIdentity.Read(Source.Configuration);
    }

    internal static async Task<ProviderCrashFixture> StartAsync(bool collected, bool launcher = false, bool committed = false,
        string? boundary = null, bool holdLauncherJob = false)
    {
        var result = new ProviderCrashFixture(await ProviderRuntimeFixture.PrepareAsync(
            launcher ? "configuration-launcher" : collected ? "configuration" : "configuration-wait", configuration: true, launcher: launcher));
        try { await result.StartOwnerAsync(boundary ?? (committed ? "committed" : collected ? "collected" : "running"), holdLauncherJob); return result; }
        catch { await result.DisposeAsync(); throw; }
    }

    private async Task StartOwnerAsync(string point, bool holdLauncherJob)
    {
        var request = Source.Request(new BindingTarget(Source.OtherBinding));
        var operation = Guid.NewGuid(); var input = Path.Combine(Source.Root, "provider-crash-input.json");
        var report = Path.Combine(Source.Root, "provider-crash-report.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new
        {
            runtimeRoot = Source.Instance.RuntimeRoot, point, bindingId = Source.OtherBinding,
            catalogRevision = request.ExpectedCatalogRevision, settingsRevision = request.ExpectedSettingsRevision,
            operationId = operation, archive = Fixture.Package.Archive, deployed = Fixture.Package.Deployed, marker = Fixture.Marker
        }));
        await Fixture.Scope.DisposeAsync(); Source.Lease.Dispose();
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_image_missing");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Source.Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "execution-provider-recovery", input, report }) start.ArgumentList.Add(argument);
        _owner = Process.Start(start) ?? throw new IOException("fixture_owner_not_started");
        var owner = ProcessIdentityReader.Read(_owner.Id);
        _ownerIdentity = owner;
        _stdout = _owner.StandardOutput.ReadToEndAsync(); _stderr = _owner.StandardError.ReadToEndAsync();
        var evidence = await OwnedCrashProcess.ReadReportAsync(report, TimeSpan.FromMinutes(4));
        Assert.True(evidence.GetProperty("status").GetString() == "faultReady", evidence.GetRawText());
        Assert.Equal(owner.ProcessId, evidence.GetProperty("ownerProcessId").GetInt32());
        Parent = evidence.GetProperty("journalId").GetGuid(); Attempt = evidence.GetProperty("attemptId").GetGuid();
        Binding = evidence.GetProperty("binding").Deserialize<StepEvidenceBinding>()!;
        Root = evidence.GetProperty("root").Deserialize<ProcessIdentity>()!;
        Assert.Equal(Source.Instance.InstanceId, Binding.InstanceId); Assert.Equal(operation, Binding.ParentOperationId);
        Assert.Equal(Parent, Binding.ParentJournalId); Assert.Equal(Attempt, Binding.AttemptId);
        Assert.Equal(Root, new StepEvidenceReader(Source.Instance, Source.Database, new()).Context(Binding).Root);
        Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(owner));
        if (point is "committed" or "retirementAccepted" or "retirementMaterialized") Assert.Equal(OriginalBytes, File.ReadAllBytes(Source.Configuration));
        else Assert.NotEqual(OriginalBytes, File.ReadAllBytes(Source.Configuration));
        if (Fixture.Package.Launcher is not null)
        {
            var state = AttemptWriterState.Read(Source.Instance, Source.Database, new(), Parent);
            Launcher = Assert.Single(ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Equal(point is "RetentionPrepared" or "HandoffApplied" ? "retentionPrepared" : "retained", Launcher.Stage);
            LauncherRoot = ProviderLauncherRows.Root(Source.Database, Launcher.Binding);
            Assert.NotNull(LauncherRoot); Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(LauncherRoot));
            if (holdLauncherJob)
            {
                _launcherJob = JobNative.OpenJobObjectW(12, false, Launcher.Binding.JobName);
                Assert.False(_launcherJob.IsInvalid);
                Assert.Contains(LauncherRoot.ProcessId, JobStateReader.ActiveProcessIds(_launcherJob));
                Assert.Equal(point == "RetentionPrepared" ? 0x2000U : 0U, JobStateReader.Limits(_launcherJob).Basic.Flags);
            }
        }
    }

    internal async Task KillOwnerAsync()
    {
        Assert.NotNull(_owner); Assert.False(_owner.HasExited);
        _owner.Kill(); await _owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var timer = Stopwatch.StartNew();
        while (WriterStopEvidence.ObserveProcess(_ownerIdentity!) == WriterState.Running && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(_ownerIdentity!));
        while (WriterStopEvidence.ObserveProcess(Root) == WriterState.Running && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(Root));
        await RequireReleasedAsync();
    }

    private async Task RequireReleasedAsync()
    {
        var path = Path.Combine(Source.Instance.RuntimeRoot, "instance.lock");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return; }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && timer.Elapsed < TimeSpan.FromSeconds(10))
            { await Task.Delay(20); }
        }
    }

    internal async Task StopLauncherAsync()
    {
        Assert.NotNull(_launcherJob); Assert.NotNull(LauncherRoot);
        Assert.Equal(0x2000U, JobStateReader.Limits(_launcherJob).Basic.Flags);
        Assert.Contains(LauncherRoot.ProcessId, JobStateReader.ActiveProcessIds(_launcherJob));
        Assert.True(JobNative.TerminateJobObject(_launcherJob, 1));
        var timer = Stopwatch.StartNew();
        while ((JobStateReader.Accounting(_launcherJob).ActiveProcesses != 0 ||
            WriterStopEvidence.ObserveProcess(LauncherRoot) != WriterState.Stopped) && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
        Assert.Equal(0U, JobStateReader.Accounting(_launcherJob).ActiveProcesses);
        Assert.Empty(JobStateReader.ActiveProcessIds(_launcherJob));
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(LauncherRoot));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_owner is not null)
            {
                if (!_owner.HasExited) { _owner.Kill(); await _owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                _ = _stdout is null ? "" : await _stdout; _ = _stderr is null ? "" : await _stderr; _owner.Dispose();
            }
        }
        finally
        {
            if (LauncherRoot is not null)
            {
                File.WriteAllText(Fixture.LauncherRelease, "release");
                var timer = Stopwatch.StartNew();
                while (WriterStopEvidence.ObserveProcess(LauncherRoot) == WriterState.Running && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
                Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(LauncherRoot));
            }
            _launcherJob?.Dispose();
            await Fixture.DisposeAsync();
        }
    }
}

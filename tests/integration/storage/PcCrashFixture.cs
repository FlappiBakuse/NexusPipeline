using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class PcCrashFixture : IDisposable
{
    internal PcStageFixture Fixture { get; }
    internal PlanBuilderFixture Source => Fixture.Source;
    internal byte[] OriginalBytes { get; }
    internal NexusPipeline.Platform.Storage.PhysicalObjectIdentity OriginalIdentity { get; }
    internal Guid Parent { get; private set; }
    internal Guid Attempt { get; private set; }
    internal ExecutionTargetBinding Binding { get; private set; } = null!;
    internal ProcessIdentity Game { get; private set; } = null!;
    internal JsonElement Report { get; private set; }
    private Process? _owner;
    private SafeKernelHandle? _game;
    private SafeKernelHandle? _heldJob;
    private readonly List<SafeKernelHandle> _heldNecessary = [];
    private Task<string>? _stdout, _stderr;

    internal PcCrashFixture(bool nativeWindow = false, bool retirementCapture = false, bool livePreview = false)
    {
        Fixture = new(configuration: true, forceClose: retirementCapture, hooks: false, distinctConfiguration: true, accept: false, nativeWindow: nativeWindow, mainWait: livePreview);
        OriginalBytes = File.ReadAllBytes(Source.Configuration);
        OriginalIdentity = NexusPipeline.Platform.Storage.PhysicalObjectIdentity.Read(Source.Configuration);
    }

    internal async Task StartAsync(string point, bool holdOriginalJob = false, bool holdEmptyNecessaryJobs = false)
    {
        var request = Source.Request(new BindingTarget(Source.BindingA)); var operation = Guid.NewGuid();
        var input = Path.Combine(Source.Root, "target-crash-input.json"); var report = Path.Combine(Source.Root, "target-crash-report.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { runtimeRoot = Source.Instance.RuntimeRoot, point,
            bindingId = Source.BindingA, catalogRevision = request.ExpectedCatalogRevision, settingsRevision = request.ExpectedSettingsRevision,
            operationId = operation, subject = "pc-target-stage-fixture" }));
        Source.Lease.Dispose();
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_image_missing");
        var start = new ProcessStartInfo(executable) { WorkingDirectory = Source.Root, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "execution-target-recovery", input, report }) start.ArgumentList.Add(argument);
        _owner = Process.Start(start) ?? throw new IOException("fixture_owner_not_started");
        _ = _owner.SafeHandle;
        var actualOwner = ProcessIdentityReader.Read(_owner.Id);
        _stdout = _owner.StandardOutput.ReadToEndAsync(); _stderr = _owner.StandardError.ReadToEndAsync();
        Report = await OwnedCrashProcess.ReadReportAsync(report, TimeSpan.FromMinutes(3));
        Assert.True(Report.GetProperty("status").GetString() == "faultReady", Report.GetRawText());
        Assert.Equal(actualOwner.ProcessId, Report.GetProperty("ownerProcessId").GetInt32());
        Parent = Report.GetProperty("journalId").GetGuid(); Attempt = Report.GetProperty("attemptId").GetGuid();
        Binding = Report.GetProperty("binding").Deserialize<ExecutionTargetBinding>()!;
        Game = Report.GetProperty("root").Deserialize<ProcessIdentity>()!;
        Assert.Equal(Source.Instance.InstanceId, Binding.InstanceId); Assert.Equal(Parent, Binding.ParentJournalId);
        Assert.Equal(operation, Binding.ParentOperationId); Assert.Equal(Attempt, Binding.AttemptId);
        Assert.Equal(actualOwner, TargetRootFacts.Creator(Source.Database, Binding));
        Assert.Equal(Game, TargetRootFacts.Read(Source.Database, Binding));
        if (point.StartsWith("ImageRetirement", StringComparison.Ordinal))
        {
            Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(Game));
            return;
        }
        _game = JobNative.OpenProcess(0x101001, false, Game.ProcessId);
        Assert.False(_game.IsInvalid); Assert.Equal(Game, ProcessIdentityReader.Read(_game, Game.ProcessId));
        Assert.Equal(258U, JobNative.WaitForSingleObject(_game, 0));
        if (holdEmptyNecessaryJobs)
        {
            var state = AttemptWriterState.Read(Source.Instance, Source.Database, new(), Parent);
            foreach (var job in state.Jobs)
            {
                var held = JobNative.OpenJobObjectW(4, false, job.JobName);
                _heldNecessary.Add(held);
                Assert.False(held.IsInvalid);
                Assert.Equal(0U, JobStateReader.Accounting(held).TotalProcesses);
                Assert.Empty(JobStateReader.ActiveProcessIds(held));
            }
            Assert.NotEmpty(_heldNecessary);
        }
        if (holdOriginalJob)
        {
            _heldJob = JobNative.OpenJobObjectW(4, false, Binding.JobName);
            Assert.False(_heldJob.IsInvalid);
            Assert.Equal(0U, JobStateReader.Limits(_heldJob).Basic.Flags & 0x2000U);
            Assert.Contains(Game.ProcessId, JobStateReader.ActiveProcessIds(_heldJob));
        }
    }

    internal async Task KillOwnerAsync()
    {
        Assert.NotNull(_owner);
        Assert.False(_owner.HasExited); _owner.Kill();
        await _owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var creator = TargetRootFacts.Creator(Source.Database, Binding);
        var timer = Stopwatch.StartNew();
        while (WriterStopEvidence.ObserveProcess(creator) == WriterState.Running && timer.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(creator));
    }

    internal bool GameAlive => _game is not null && JobNative.WaitForSingleObject(_game, 0) == 258;

    internal async Task RequireStoppedAsync()
    {
        var timer = Stopwatch.StartNew();
        while (GameAlive && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Assert.False(GameAlive); Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(Game));
    }

    public void Dispose()
    {
        try
        {
            if (_owner is not null && !_owner.HasExited) { _owner.Kill(); _owner.WaitForExit(10000); }
            if (_game is not null && !_game.IsInvalid && JobNative.WaitForSingleObject(_game, 0) == 258 &&
                (!JobNative.TerminateProcess(_game, 1) || JobNative.WaitForSingleObject(_game, 10000) != 0))
                throw new IOException("fixture_target_crash_cleanup_pending");
            if (_stdout is not null && _stderr is not null && _owner is { HasExited: true })
            {
                File.WriteAllText(Path.Combine(Source.Root, "target-owner-stdout.log"), _stdout.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
                File.WriteAllText(Path.Combine(Source.Root, "target-owner-stderr.log"), _stderr.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
            }
        }
        finally
        {
            foreach (var job in _heldNecessary) job.Dispose();
            _heldJob?.Dispose(); _game?.Dispose(); _owner?.Dispose(); Fixture.Dispose();
        }
    }
}

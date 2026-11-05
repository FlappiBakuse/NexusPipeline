using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class AndroidCrashFixture : IDisposable
{
    internal AndroidRuntimeFixture Fixture { get; }
    internal PlanBuilderFixture Source => Fixture.Source;
    internal byte[] OriginalBytes { get; }
    internal PhysicalObjectIdentity OriginalIdentity { get; }
    internal Guid Parent { get; private set; }
    internal Guid Attempt { get; private set; }
    internal ExecutionTargetBinding Binding { get; private set; } = null!;
    internal ProcessIdentity CommandRoot { get; private set; } = null!;
    internal JsonElement Report { get; private set; }
    private readonly bool _capture, _mumu;
    private Process? _owner;
    private Task<string>? _stdout, _stderr;
    private readonly List<SafeKernelHandle> _emptyNecessaryJobs = [];

    internal AndroidCrashFixture(bool capture = false, bool mumu = false)
    {
        _capture = capture; _mumu = mumu;
        Fixture = new(configuration: true, forceClose: false, accept: false, capture: capture,
            captureMode: capture ? "rgba" : null, mumu: mumu, pairedCapture: mumu && capture);
        File.WriteAllText(Source.Configuration, "{\"fixture\":false,\"site\":\"original\"}");
        OriginalBytes = File.ReadAllBytes(Source.Configuration);
        OriginalIdentity = PhysicalObjectIdentity.Read(Source.Configuration);
    }

    internal async Task StartAsync(string point)
    {
        var request = Source.Request(new BindingTarget(Source.BindingA));
        var operation = Guid.NewGuid();
        var input = Path.Combine(Source.Root, "android-crash-input.json");
        var report = Path.Combine(Source.Root, "android-crash-report.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new
        {
            runtimeRoot = Source.Instance.RuntimeRoot, point, capture = _capture, bindingId = Source.BindingA,
            mumuToolPath = _mumu ? Fixture.ToolImage : null, mumuScriptId = Source.ScriptId,
            mumuEndpoint = AndroidRuntimeFixture.Endpoint,
            catalogRevision = request.ExpectedCatalogRevision, settingsRevision = request.ExpectedSettingsRevision, operationId = operation, subject = AndroidRuntimeFixture.Subject
        }));
        Source.Lease.Dispose();
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_image_missing");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Source.Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "execution-target-recovery", input, report }) start.ArgumentList.Add(argument);
        _owner = Process.Start(start) ?? throw new IOException("fixture_owner_not_started");
        var actualOwner = ProcessIdentityReader.Read(_owner.Id);
        _stdout = _owner.StandardOutput.ReadToEndAsync(); _stderr = _owner.StandardError.ReadToEndAsync();
        Report = await OwnedCrashProcess.ReadReportAsync(report, TimeSpan.FromMinutes(3));
        Assert.True(Report.GetProperty("status").GetString() == "faultReady", Report.GetRawText());
        Assert.Equal(actualOwner.ProcessId, Report.GetProperty("ownerProcessId").GetInt32());
        Parent = Report.GetProperty("journalId").GetGuid(); Attempt = Report.GetProperty("attemptId").GetGuid();
        Binding = Report.GetProperty("binding").Deserialize<ExecutionTargetBinding>()!;
        CommandRoot = Report.GetProperty("root").Deserialize<ProcessIdentity>()!;
        Assert.Equal(Source.Instance.InstanceId, Binding.InstanceId); Assert.Equal(operation, Binding.ParentOperationId);
        Assert.Equal(Parent, Binding.ParentJournalId); Assert.Equal(Attempt, Binding.AttemptId);
        Assert.Equal(actualOwner, TargetRootFacts.Creator(Source.Database, Binding));
        var state = AttemptWriterState.Read(Source.Instance, Source.Database, new(), Parent);
        if (point == "RootAdmitted")
            foreach (var job in state.Jobs.DistinctBy(item => item.JobName))
            {
                var handle = JobNative.OpenJobObjectW(4, false, job.JobName);
                _emptyNecessaryJobs.Add(handle);
                Assert.False(handle.IsInvalid);
                Assert.Equal(0U, JobStateReader.Accounting(handle).TotalProcesses);
                Assert.Empty(JobStateReader.ActiveProcessIds(handle));
            }
        var command = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .Single(item => item.Binding == Binding);
        Assert.Equal(CommandRoot, TargetRootFacts.ReadCommand(Source.Database, command));
    }

    internal async Task KillOwnerAsync()
    {
        Assert.NotNull(_owner); Assert.False(_owner.HasExited);
        _owner.Kill(); await _owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var creator = TargetRootFacts.Creator(Source.Database, Binding);
        var elapsed = Stopwatch.StartNew();
        while (WriterStopEvidence.ObserveProcess(creator) == WriterState.Running && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(creator));
        elapsed.Restart();
        while (WriterStopEvidence.ObserveProcess(CommandRoot) == WriterState.Running && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(CommandRoot));
    }

    internal string[] ObservedCommands() => new[] { Fixture.Directory, Fixture.ScreenshotDirectory }
        .Where(directory => directory is not null && System.IO.Directory.Exists(Path.Combine(directory, "observed")))
        .SelectMany(directory => System.IO.Directory.GetFiles(Path.Combine(directory!, "observed")))
        .Order(StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        try
        {
            if (_owner is not null)
            {
                if (!_owner.HasExited) { _owner.Kill(); _owner.WaitForExit(10000); }
                if (!_owner.HasExited) throw new IOException("fixture_owner_stop_pending");
                _ = _stdout?.GetAwaiter().GetResult(); _ = _stderr?.GetAwaiter().GetResult();
                _owner.Dispose();
            }
        }
        finally
        {
            foreach (var handle in _emptyNecessaryJobs) handle.Dispose();
            Fixture.Dispose();
        }
    }
}

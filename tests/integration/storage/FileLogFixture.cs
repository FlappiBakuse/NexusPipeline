using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class FileLogFixture : IAsyncDisposable
{
    internal const string Subject = "file-log-fixture";
    internal ExecutionAdmissionFixture Base { get; }
    internal PlanBuilderFixture Source => Base.Source;
    internal string DirectoryPath { get; }
    internal string LogPath => Path.Combine(DirectoryPath, "events.log");
    internal string Marker { get; }
    internal AcceptedExecutionAdmission Accepted { get; }
    internal StepWriterScope Scope { get; }
    internal ConfigurationWriterProcess? Writer { get; }
    internal ConfigurationRunController? Controller { get; }
    internal StepEvidenceCollector Collector { get; private set; } = null!;
    internal StepEvidenceBinding Binding { get; private set; } = null!;
    internal StepJudgementWorker? Worker { get; private set; }
    private readonly bool _longOutput;
    internal AttemptWriterState State => Scope.VerifyReadOnly();
    internal ExecutionPlan Total => Accepted.Prepared.Total.Plan.ReadWire();
    private bool _collected, _completed, _entered, _collectionAttempted;

    internal FileLogFixture(string mode, string declaration = "logs/events.log", bool configuration = false,
        bool script = false, AutomationLimits? limits = null, string? judgementScript = null)
    {
        _longOutput = mode.StartsWith("long-", StringComparison.Ordinal);
        Base = new(judgementRuntime: script ? new(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!,
            Environment.GetEnvironmentVariable("NEXUS_TEST_PYTHON_EXE")) : null);
        try
        {
            Marker = Path.Combine(Source.Root, "file-log-ready");
            var scope = Path.Combine(Source.Root, "file-log-scope"); DirectoryPath = Path.Combine(scope, "logs"); Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(LogPath, "old-file-success\n");
            File.WriteAllText(Path.Combine(DirectoryPath, "older.log"), "old-file-success\n");
            File.SetLastWriteTimeUtc(Path.Combine(DirectoryPath, "older.log"), DateTime.UtcNow.AddDays(-1));
            var catalog = Source.Definitions.Read(); var scriptId = configuration ? Source.ScriptId : Source.OtherScriptId;
            var head = catalog.Scripts.Single(item => item.Id == scriptId);
            var native = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
            var root = new AutomationRoot(Guid.NewGuid(), 0, "File log native", AutomationRootKind.Script, Path.GetDirectoryName(native)!);
            var logs = new AutomationRoot(Guid.NewGuid(), 0, "File log scope", AutomationRootKind.External, scope);
            var judgement = script ? new AutomationJudgement(AutomationJudgementLanguage.JavaScript, [], [],
                judgementScript ?? "function nexusJudge(input) { return { outcome: input.log.includes('file-failure') ? 'failed' : 'unverified', reason: 'saved file log' }; }") :
                new(AutomationJudgementLanguage.Keywords, ["old-file-success", "pipe-success"], ["file-failure", "file-中文🙂-failure"], null);
            var changed = head with { RootId = root.Id, Executable = new(root.Id, Path.GetFileName(native)),
                Arguments = ["file-log-output", mode, Marker, DirectoryPath], LogPaths = [new(logs.Id, declaration)],
                Limits = limits ?? new(1, 30, 120), Judgement = judgement };
            var change = new AutomationChanges(catalog.Revision, [root, logs], [changed], [], [], [], [], []);
            Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), Subject, change), change, DateTimeOffset.UtcNow);
            var binding = configuration ? Source.BindingA : Source.OtherBinding;
            var receipt = Base.Service.Start(Guid.NewGuid(), Subject, Source.Request(new BindingTarget(binding)), DateTimeOffset.UtcNow);
            Accepted = Base.Take(receipt.Acceptance.OperationId);
            var step = Assert.Single(Total.Steps);
            Guid? sessionId = null;
            if (configuration)
            {
                var session = Guid.NewGuid(); var prepared = Accepted.Prepared.Configurations[step.StepId];
                new ConfigurationRunStore(Source.Instance, Source.Database, new()).Begin(
                    ConfigurationRunStore.BeginIdentity(session, Subject, receipt.JournalId, step.StepId, prepared), receipt.JournalId,
                    step.StepId, prepared, DateTimeOffset.UtcNow); sessionId = session;
            }
            Accepted.Prepared.ReleaseLogGuards();
            Scope = new(Source.Instance, Source.Database, new(), Source.Lease, receipt.JournalId, step.StepId);
            if (sessionId is { } id) Controller = new(Source.Instance, Source.Database, new(), Source.Lease, Base.Index, id, Subject,
                Accepted.Prepared.Configurations[step.StepId], Scope, deferRegistration: true);
            else Writer = new(Source.Instance, Source.Lease, Scope, deferRegistration: true);
        }
        catch { Base.Dispose(); throw; }
    }

    internal void Prepare(Action? afterRegister = null, long? rawBudget = null)
    {
        if (Controller is not null) Controller.RegisterPreparedJob(); else Writer!.RegisterPreparedJob();
        var state = State; Binding = StepResultCodec.Binding(state.Latest.Root, Assert.Single(state.Steps));
        afterRegister?.Invoke();
        var judgement = new AttemptBusinessJudgement(Binding.AttemptId, new StepEvidenceReader(Source.Instance, Source.Database, new()).Judgement(Total, Binding.StepId));
        if (Assert.Single(Total.Steps).JudgementWorker is not null)
        {
            Worker = new(Source.Instance, Source.Database, new(), Source.Lease, Total, Binding, judgement,
                () => StepResultAdmission.SuppressesLocalStop(new StepEvidenceReader(Source.Instance, Source.Database, new()).AcceptedClose(Binding)));
            Worker.Prepare();
        }
        var allowance = rawBudget is null ? null : new BindingRawAllowance(rawBudget.Value, 0);
        Collector = Writer is not null ? new(Source.Instance, Source.Database, new(), Source.Lease, Binding, Writer, judgement, Worker, allowance) :
            new(Source.Instance, Source.Database, new(), Source.Lease, Binding, Controller!, judgement, Worker, allowance);
    }

    internal async Task Start()
    {
        Scope.EnterApplying(); _entered = true; Collector.Start(); await Ready(".ready"); File.WriteAllText(Marker + ".go", "go");
    }
    internal async Task Ready(string suffix)
    {
        var timer = Stopwatch.StartNew(); while (!File.Exists(Marker + suffix) && timer.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);
        Assert.True(File.Exists(Marker + suffix), Marker + suffix);
    }
    internal async Task WaitExit()
    {
        var timeout = TimeSpan.FromSeconds(_longOutput ? 120 : 20);
        Assert.Equal(37, await (Writer is not null ? Writer.AwaitRootAsync(timeout) : Controller!.AwaitRootAsync(timeout)));
        await Collector.ObserveExitAsync();
    }
    internal async Task<StepEvidenceDocument> Collect()
    {
        _collectionAttempted = true;
        try { return await CollectCore(); }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(Source.Root, "file-log-collection-failure.txt"), error.ToString());
            throw;
        }
    }

    private async Task<StepEvidenceDocument> CollectCore()
    {
        var stopTimeout = TimeSpan.FromSeconds(_longOutput ? 30 : 10);
        var drainTimeout = TimeSpan.FromSeconds(_longOutput ? 60 : 10);
        var stopped = Writer is not null ? await Writer.StopAsync(stopTimeout) : await Controller!.StopWriterAsync(stopTimeout);
        var proof = await Collector.CollectAsync(stopped, drainTimeout);
        new AttemptTerminalStore(Source.Instance, Source.Database, new(), Source.Lease).RecordCollected(Binding.ParentJournalId, proof);
        _collected = true;
        var document = StepEvidenceCodec.Read(new(Source.Instance, new()), Binding, proof.Use(Binding, evidence => evidence).Artifact);
        Collector.Dispose(); return document;
    }
    internal void Cancel()
    {
        var state = State;
        new ExecutionCancellationStore(Source.Instance, Source.Database, new(), Source.Lease).Cancel(
            ExecutionCancellationStore.Identity(Guid.NewGuid(), Subject, Binding.ParentJournalId, Binding.ParentOperationId, Binding.RunId, Binding.AttemptId),
            Binding.ParentJournalId, DateTimeOffset.UtcNow);
    }
    internal async Task Complete(bool cancelled = false)
    {
        if (Controller is not null) await Controller.FinishAsync(Guid.NewGuid(), interrupted: cancelled);
        var terminal = new AttemptTerminalStore(Source.Instance, Source.Database, new(), Source.Lease);
        if (!cancelled) terminal.FreezeDispatch(Binding.ParentJournalId, interrupted: false);
        terminal.CloseUndispatched(Binding.ParentJournalId); terminal.MarkApplied(Binding.ParentJournalId);
        terminal.Commit(Binding.ParentJournalId, cancelled ? LifecycleOutcome.Cancelled : LifecycleOutcome.Completed,
            cancelled ? DispatchIntentCodec.Reason : "execution_completed"); _completed = true;
    }
    internal async Task<(RawOutputManifest Raw, string[] Lines)> Snapshot()
    {
        var blobs = new ImmutableBlobStore(Source.Instance, new());
        var raw = RawOutputCodec.Read(blobs, Binding, await Collector.SnapshotOutputAsync());
        var lines = new List<string>(); RawOutputReplay.Read(blobs, raw, lines.Add); return (raw, lines.ToArray());
    }
    internal async Task WaitLine(string expected)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(20)) { if ((await Snapshot()).Lines.Contains(expected)) return; await Task.Delay(50); }
        Assert.Contains(expected, (await Snapshot()).Lines);
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_collectionAttempted && Collector is not null && _entered) _ = await Collect();
            else if (!_collected)
            {
                var stopped = Writer is not null ? await Writer.StopAsync(TimeSpan.FromSeconds(10)) : await Controller!.StopWriterAsync(TimeSpan.FromSeconds(10));
                if (!_collectionAttempted && Collector is not null)
                { _ = await Collector.CollectAsync(stopped, TimeSpan.FromSeconds(10)); Collector.Dispose(); }
            }
            if (_collected) (Collector ?? throw new InvalidOperationException("file_log_collector_missing")).Dispose();
            if (Worker is not null)
            {
                try { await Worker.StopAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error)
                {
                    File.WriteAllText(Path.Combine(Source.Root, "file-log-worker-stop-failure.txt"), error.ToString());
                    throw;
                }
                Worker.Dispose();
            }
            Writer?.Dispose(); if (_completed) Controller?.Dispose();
        }
        finally { Accepted.Dispose(); Base.Dispose(); }
    }
}

using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class JudgementOwnedStep : IAsyncDisposable
{
    private readonly JudgementIntegrationFixture _fixture;
    private readonly AcceptedExecutionAdmission _accepted;
    internal StepWriterScope Scope { get; }
    internal ConfigurationWriterProcess? Writer { get; }
    internal ConfigurationRunController? Controller { get; }
    internal StepJudgementWorker? Worker { get; private set; }
    internal AttemptBusinessJudgement Owner { get; private set; } = null!;
    internal StepEvidenceCollector Collector { get; private set; } = null!;
    internal StepEvidenceBinding Binding { get; private set; } = null!;
    internal Guid? ConfigurationSession { get; }
    private StoppedConfigurationEditor? _stopped;
    private bool _collected, _completed;
    private readonly Func<JudgementProbeRequest, CancellationToken, Task<JudgementProbeReply>>? _observeProbe;

    internal JudgementOwnedStep(JudgementIntegrationFixture fixture,
        Func<JudgementProbeRequest, CancellationToken, Task<JudgementProbeReply>>? observeProbe = null)
    {
        _fixture = fixture; _observeProbe = observeProbe; var source = fixture.Source;
        _accepted = fixture.Base.Take(fixture.Accepted.Acceptance.OperationId);
        var step = Assert.Single(_accepted.Prepared.Total.Plan.ReadWire().Steps);
        if (_accepted.Prepared.Configurations.TryGetValue(step.StepId, out var configuration))
        {
            var runs = new ConfigurationRunStore(source.Instance, source.Database, new()); var session = Guid.NewGuid();
            runs.Begin(ConfigurationRunStore.BeginIdentity(session, JudgementIntegrationFixture.Subject, fixture.Accepted.JournalId, step.StepId, configuration),
                fixture.Accepted.JournalId, step.StepId, configuration, DateTimeOffset.UtcNow);
            ConfigurationSession = session;
        }
        Scope = new(source.Instance, source.Database, new(), source.Lease, fixture.Accepted.JournalId, step.StepId);
        if (ConfigurationSession is { } id)
            Controller = new(source.Instance, source.Database, new(), source.Lease, fixture.Base.Index, id,
                JudgementIntegrationFixture.Subject, _accepted.Prepared.Configurations[step.StepId], Scope, deferRegistration: true);
        else Writer = new(source.Instance, source.Lease, Scope, deferRegistration: true);
    }

    internal void Prepare()
    {
        var source = _fixture.Source;
        if (Controller is not null) Controller.RegisterPreparedJob(); else Writer!.RegisterPreparedJob();
        var state = Scope.VerifyReadOnly(); Binding = StepResultCodec.Binding(state.Latest.Root, Assert.Single(state.Steps));
        Owner = new(Binding.AttemptId, new StepEvidenceReader(source.Instance, source.Database, new()).Judgement(state.Total, Binding.StepId));
        Worker = new(source.Instance, source.Database, new(), source.Lease, state.Total, Binding, Owner,
            () => StepResultAdmission.SuppressesLocalStop(new StepEvidenceReader(source.Instance, source.Database, new()).AcceptedClose(Binding)), _observeProbe);
        Worker.Prepare();
        Collector = Controller is not null ? new(source.Instance, source.Database, new(), source.Lease, Binding, Controller, Owner, Worker) :
            new(source.Instance, source.Database, new(), source.Lease, Binding, Writer!, Owner, Worker);
    }

    internal void Start() { Scope.EnterApplying(); Collector.Start(); }
    internal async Task<StoppedConfigurationEditor> StopPrimary()
    {
        return _stopped ??= Controller is not null ? await Controller.StopWriterAsync(TimeSpan.FromSeconds(10)) : await Writer!.StopAsync(TimeSpan.FromSeconds(10));
    }

    internal async Task Collect()
    {
        var stopped = await StopPrimary(); var proof = await Collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
        Terminal().RecordCollected(_fixture.Accepted.JournalId, proof); _collected = true;
    }

    internal async Task Complete()
    {
        if (Controller is not null) await Controller.FinishAsync(Guid.NewGuid(), interrupted: false);
        var terminal = Terminal(); terminal.FreezeDispatch(_fixture.Accepted.JournalId, interrupted: false);
        terminal.CloseUndispatched(_fixture.Accepted.JournalId); terminal.MarkApplied(_fixture.Accepted.JournalId);
        terminal.Commit(_fixture.Accepted.JournalId, LifecycleOutcome.Completed, "execution_completed"); _completed = true;
    }

    internal void CompleteCollectedCancellation()
    {
        var terminal = Terminal();
        terminal.CloseUndispatched(_fixture.Accepted.JournalId); terminal.MarkApplied(_fixture.Accepted.JournalId);
        terminal.Commit(_fixture.Accepted.JournalId, LifecycleOutcome.Cancelled, "execution_cancel_requested"); _completed = true;
    }

    internal async Task CompletePreparedCancellation()
    {
        await StopPrimary(); if (Worker is not null) await Worker.StopAsync(TimeSpan.FromSeconds(10));
        _fixture.Cancel(); Terminal().CommitPreparedCancellation(_fixture.Accepted.JournalId); _completed = true;
    }

    private AttemptTerminalStore Terminal() => new(_fixture.Source.Instance, _fixture.Source.Database, new(), _fixture.Source.Lease);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_completed)
            {
                Owner?.Cancel(); await StopPrimary();
                if (Worker is not null) await Worker.StopAsync(TimeSpan.FromSeconds(10));
            }
            if (_collected) Collector.Dispose();
            Worker?.Dispose(); Writer?.Dispose(); if (_completed) Controller?.Dispose();
        }
        finally { _accepted.Dispose(); }
    }
}

using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ProviderPublicationTests
{
    [Theory(DisplayName = "Actual Provider Terminal And Accepted Cancellation Keep Their Winning Publication Order")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdersTerminalCancellation(bool terminalFirst)
    {
        await using var fixture = await ManualProvider.CreateAsync();
        fixture.Collector.Start(); await fixture.Runtime.ReadyAsync();
        if (terminalFirst)
        {
            fixture.ReleaseTerminal();
            _ = await fixture.Control.Terminal.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Single(ProviderTerminalCodec.Read(fixture.State.Latest.Root, fixture.State.Total));
        }
        fixture.Runtime.Cancel();
        if (!terminalFirst)
        {
            fixture.ReleaseTerminal();
            await WaitAsync(() => File.Exists(fixture.Runtime.Marker + ".terminal-sent"));
        }
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(15));
        var proof = await fixture.Collector.CollectAsync(stopped, TimeSpan.FromSeconds(15));
        var terminal = fixture.TerminalStore;
        terminal.RecordCollected(fixture.Parent, proof);
        terminal.CloseUndispatched(fixture.Parent); terminal.MarkApplied(fixture.Parent);
        var receipt = terminal.Commit(fixture.Parent, LifecycleOutcome.Cancelled, "execution_cancel_requested");
        var state = fixture.State;
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
        Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Equal(terminalFirst ? 1 : 0, ProviderTerminalCodec.Read(state.Latest.Root, state.Total).Length);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var native = StepEvidenceCodec.Read(new(fixture.Source.Instance, new()), result.Binding, result.Latest.Artifact);
        Assert.NotNull(native.ProviderControl);
        var archive = new ProviderControlReader(fixture.Source.Instance, fixture.Source.Database, new())
            .Read(result.Binding, native.ProviderControl!);
        Assert.NotNull(archive.Terminal);
        Assert.All(stopped.Closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        Assert.Equal(1, fixture.Runtime.Base.Count("attempt_receipts"));
        Assert.Equal(0, fixture.Runtime.Base.Count("resource_isolations"));
    }

    [Fact(DisplayName = "An Actual Terminal Sql Failure Reconciles Its Original Physical Prefix And Still Collects The Stopped Worker")]
    public async Task ReconcilesTerminalProjection()
    {
        await using var fixture = await ManualProvider.CreateAsync();
        fixture.Collector.Start(); await fixture.Runtime.ReadyAsync();
        var before = fixture.State;
        fixture.Runtime.Base.Execute("CREATE TRIGGER fixture_provider_terminal BEFORE UPDATE OF revision ON operation_journals " +
            $"WHEN NEW.journal_id='{fixture.Parent:D}' AND NEW.revision={before.Latest.Revision + 1} " +
            "BEGIN SELECT RAISE(ABORT,'fixture_provider_terminal'); END;");
        try
        {
            fixture.ReleaseTerminal();
            await WaitAsync(() => fixture.State.Latest.Revision > fixture.State.Journal.Revision);
        }
        finally { fixture.Runtime.Base.Execute("DROP TRIGGER fixture_provider_terminal;"); }
        var ahead = fixture.State;
        var intended = Assert.Single(ProviderTerminalCodec.Read(ahead.Latest.Root, ahead.Total));
        Assert.Equal(before.Journal.Revision, ahead.Journal.Revision);
        Assert.Equal(before.Latest.Revision + 1, ahead.Latest.Revision);
        var proof = PreparedProviderTerminal.FromNative(fixture.Control);
        new ProviderTerminalStore(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease).Record(proof);
        var projected = fixture.State;
        Assert.Equal(ahead.Latest.Revision, projected.Journal.Revision);
        Assert.Equal(intended, Assert.Single(ProviderTerminalCodec.Read(projected.Latest.Root, projected.Total)));
        Assert.Equal(ahead.Latest.Revision, projected.Latest.Revision);
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(15));
        var collected = await fixture.Collector.CollectAsync(stopped, TimeSpan.FromSeconds(15));
        fixture.TerminalStore.RecordCollected(fixture.Parent, collected);
        fixture.TerminalStore.FreezeDispatch(fixture.Parent, interrupted: false);
        fixture.TerminalStore.MarkApplied(fixture.Parent);
        var receipt = fixture.TerminalStore.Commit(fixture.Parent, LifecycleOutcome.Completed, "execution_completed");
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(intended, Assert.Single(ProviderTerminalCodec.Read(fixture.State.Latest.Root, fixture.State.Total)));
        Assert.All(stopped.Closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        Assert.Equal(0, fixture.Runtime.Base.Count("resource_isolations"));
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
        Assert.True(ready());
    }

    private sealed class ManualProvider : IAsyncDisposable
    {
        internal ProviderRuntimeFixture Runtime { get; }
        internal PlanBuilderFixture Source => Runtime.Source;
        private AcceptedExecutionAdmission Accepted { get; }
        internal ConfigurationWriterProcess Writer { get; }
        internal ProviderControlSession Control { get; }
        internal StepEvidenceCollector Collector { get; }
        internal Guid Parent => Runtime.Accepted.JournalId;
        internal AttemptWriterState State => Runtime.State;
        internal AttemptTerminalStore TerminalStore => new(Source.Instance, Source.Database, new(), Source.Lease);

        private ManualProvider(ProviderRuntimeFixture runtime)
        {
            Runtime = runtime; Accepted = runtime.Base.Take(runtime.Accepted.Acceptance.OperationId);
            var step = Assert.Single(Accepted.Prepared.Total.Plan.ReadWire().Steps);
            var provider = Accepted.Prepared.Providers[step.StepId];
            var scope = new StepWriterScope(Source.Instance, Source.Database, new(), Source.Lease, Parent, step.StepId, provider: provider);
            Writer = new(Source.Instance, Source.Lease, scope);
            var state = scope.VerifyReadOnly();
            var binding = StepResultCodec.Binding(state.Latest.Root, Assert.Single(state.Steps));
            Control = new(Source.Instance, Source.Database, new(), Source.Lease, binding, provider, Accepted.Prepared.TimeBudget, Writer, null);
            Collector = new(Source.Instance, Source.Database, new(), Source.Lease, binding, Writer, provider: Control);
            scope.EnterApplying();
        }

        internal static async Task<ManualProvider> CreateAsync() => new(await ProviderRuntimeFixture.CreateAsync("terminal-gated"));
        internal void ReleaseTerminal() => File.WriteAllText(Runtime.Marker + ".terminal-release", "release");

        public async ValueTask DisposeAsync()
        {
            try
            {
                var stopped = await Writer.StopAsync(TimeSpan.FromSeconds(15));
                try { await Collector.CollectAsync(stopped, TimeSpan.FromSeconds(15)); }
                finally { Collector.Dispose(); Control.Dispose(); Writer.Dispose(); }
            }
            finally { Accepted.Dispose(); await Runtime.DisposeAsync(); }
        }
    }
}

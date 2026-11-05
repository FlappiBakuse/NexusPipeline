using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class StepWriterFixture : IDisposable
{
    internal ExecutionAdmissionFixture Base { get; }
    internal AcceptedExecutionAdmission Accepted { get; }
    internal StepWriterScope Scope { get; }
    internal ConfigurationWriterProcess Writer { get; }
    internal OwnedProcessOutput Output { get; } = new();
    internal Guid JournalId => Accepted.Receipt.JournalId;
    internal string Marker { get; }
    internal string StepId { get; }

    internal StepWriterFixture(string outputMode = "wait", Func<IConfigurationWriterScope, IConfigurationWriterScope>? scopeWrapper = null,
        AutomationJudgement? judgement = null, AutomationLimits? limits = null)
    {
        Base = new();
        try
        {
            var source = Base.Source; Marker = Path.Combine(source.Root, "actual-step-ready");
            var catalog = source.Definitions.Read();
            var script = catalog.Scripts.Single(item => item.Id == source.OtherScriptId);
            var root = catalog.Roots.Single(item => item.Id == script.RootId);
            var actual = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
            var changes = new AutomationChanges(catalog.Revision, [root with { Path = Path.GetDirectoryName(actual)! }],
                [script with { Executable = new(root.Id, Path.GetFileName(actual)), Arguments = ["process-output", outputMode, Marker],
                    Judgement = judgement ?? script.Judgement, Limits = limits ?? script.Limits }], [], [], [], [], []);
            source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "step-writer-fixture", changes), changes, PlanBuilderFixture.CreatedAt);
            var operation = Guid.NewGuid();
            Base.Service.Start(operation, "step-writer-fixture", source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt);
            Accepted = Base.Take(operation);
            StepId = Assert.Single(Accepted.Prepared.Total.Plan.ReadWire().Steps).StepId;
            Scope = new(source.Instance, source.Database, new(), source.Lease, JournalId, StepId);
            Writer = new(source.Instance, source.Lease, scopeWrapper?.Invoke(Scope) ?? Scope);
        }
        catch { Output.Dispose(); Base.Dispose(); throw; }
    }

    internal async Task<OwnedWorkerIdentity> StartAsync()
    {
        Scope.EnterApplying(); var started = Writer.Start(Output);
        var timer = Stopwatch.StartNew();
        while (!File.Exists(Marker + ".output-ready") && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(File.Exists(Marker + ".output-ready"));
        Assert.Equal(started.ProcessId.ToString(), File.ReadAllText(Marker + ".output-ready"));
        return started;
    }

    internal void AssertClosed(StoppedConfigurationEditor stopped, bool executed)
    {
        var state = Scope.VerifyReadOnly(); var step = Assert.Single(state.Steps); var closure = stopped.Closure;
        Assert.Equal(executed, stopped.WasExecuted); Assert.Equal(state.Journal.OperationId, closure.OperationId);
        Assert.Equal(new[] { JournalId }, closure.JournalIds.ToArray()); Assert.Equal(step.JobName, closure.JobName);
        Assert.Equal(step.ClosedElapsedMilliseconds, closure.RunElapsedMilliseconds);
        Assert.All(closure.Family, member => Assert.Equal(NexusPipeline.Core.Execution.WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        Assert.Equal(closure.Family.Length, closure.Family.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count());
        var stored = new JobClosureStore(Base.Source.Instance, Base.Source.Database, new()).Read(JournalId, closure.WriterId, closure.JobName, closure.SessionId);
        Assert.NotNull(stored); Assert.Equal(closure.Root, stored.Root); Assert.True(closure.Family.SequenceEqual(stored.Family));
        Assert.Equal(1, Base.Count("journal_writer_jobs")); Assert.Equal(executed ? 1 : 0, Base.Count("journal_writer_job_roots"));
        Assert.Equal(1, Base.Count("journal_writer_job_closures")); Assert.Equal(0, Base.Count("attempt_receipts"));
    }

    public void Dispose()
    {
        try { Writer.Dispose(); }
        finally { Output.Dispose(); Accepted.Dispose(); Base.Dispose(); }
    }
}

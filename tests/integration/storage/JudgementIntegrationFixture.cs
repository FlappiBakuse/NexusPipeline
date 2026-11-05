using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class JudgementIntegrationFixture : IDisposable
{
    internal const string Subject = "judgement-integration-fixture";
    internal ExecutionAdmissionFixture Base { get; }
    internal PlanBuilderFixture Source => Base.Source;
    internal ExecutionRuntimeConsumer Consumer { get; }
    internal AttemptAcceptanceResult Accepted { get; }
    internal string Marker { get; }
    internal AttemptWriterState State => AttemptWriterState.Read(Source.Instance, Source.Database, new(), Accepted.JournalId);

    internal JudgementIntegrationFixture(AutomationJudgementLanguage language, string script, string mode = "tail", bool configuration = false,
        Action<ExecutionAdmissionFixture>? beforeAccept = null)
    {
        Base = new(judgementRuntime: new(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, Environment.GetEnvironmentVariable("NEXUS_TEST_PYTHON_EXE")));
        try
        {
            Marker = Path.Combine(Source.Root, "judgement-integration-ready");
            var catalog = Source.Definitions.Read(); var id = configuration ? Source.ScriptId : Source.OtherScriptId;
            var head = catalog.Scripts.Single(item => item.Id == id);
            var native = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
            var root = new AutomationRoot(Guid.NewGuid(), 0, "Judgement native primary", AutomationRootKind.Script, Path.GetDirectoryName(native)!);
            var changed = head with { RootId = root.Id, Executable = new(root.Id, Path.GetFileName(native)),
                Arguments = ["process-output", mode, Marker], Limits = new(1, 30, 120), Judgement = new(language, [], [], script) };
            var changes = new AutomationChanges(catalog.Revision, [root], [changed], [], [], [], [], []);
            Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), Subject, changes), changes, DateTimeOffset.UtcNow);
            beforeAccept?.Invoke(Base);
            var binding = configuration ? Source.BindingA : Source.OtherBinding;
            Accepted = Base.Service.Start(Guid.NewGuid(), Subject, Source.Request(new BindingTarget(binding)), DateTimeOffset.UtcNow);
            Consumer = new(Source.Instance, Source.Database, new(), Source.Lease, Base.Resources, Base.Index, Base.Inbox);
        }
        catch { Base.Dispose(); throw; }
    }

    internal void Cancel()
    {
        var state = State;
        new ExecutionCancellationStore(Source.Instance, Source.Database, new(), Source.Lease).Cancel(
            ExecutionCancellationStore.Identity(Guid.NewGuid(), Subject, Accepted.JournalId, state.Journal.OperationId,
                Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), Accepted.JournalId, DateTimeOffset.UtcNow);
    }

    internal async Task Ready()
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(Marker + ".output-ready") && timer.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(10);
        Assert.True(File.Exists(Marker + ".output-ready"));
    }

    public void Dispose() => Base.Dispose();
}

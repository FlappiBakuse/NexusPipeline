using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionHooksFixture : IDisposable
{
    internal const string Subject = "execution-hooks-fixture";
    internal ExecutionAdmissionFixture Base { get; } = new();
    internal PlanBuilderFixture Source => Base.Source;
    internal ExecutionRuntimeConsumer Consumer { get; }
    private readonly AttemptAcceptanceResult? _accepted;
    internal AttemptAcceptanceResult Accepted => _accepted ?? throw new InvalidOperationException();
    internal string Marker { get; }
    internal AttemptWriterState State => AttemptWriterState.Read(Source.Instance, Source.Database, new(), Accepted.JournalId);

    internal ExecutionHooksFixture(bool configuration, int preExit = 0, int postExit = 0, bool waitPre = false, bool accept = true)
    {
        try
        {
            Marker = Path.Combine(Source.Root, "hooks-ready");
            var catalog = Source.Definitions.Read();
            var native = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
            var root = new AutomationRoot(Guid.NewGuid(), 0, "Hook native", AutomationRootKind.Script, Path.GetDirectoryName(native)!);
            var script = catalog.Scripts.Single(script => script.Id == Source.ScriptId) with
            {
                RootId = root.Id, Executable = new(root.Id, Path.GetFileName(native)),
                Arguments = Arguments("main", 0, false), ConfigurationPaths = configuration
                    ? catalog.Scripts.Single(script => script.Id == Source.ScriptId).ConfigurationPaths : [],
                LogPaths = [], Limits = new(1, 30, 120),
                Judgement = new(AutomationJudgementLanguage.Keywords, ["done"], ["failed"], null)
            };
            var binding = catalog.Bindings.Single(binding => binding.Id == Source.BindingA) with
            {
                PreHook = new(new(root.Id, Path.GetFileName(native)), Arguments("pre", preExit, waitPre)),
                PostHook = new(new(root.Id, Path.GetFileName(native)), Arguments("post", postExit, false))
            };
            var change = new AutomationChanges(catalog.Revision, [root], [script], [], [binding], [], [], []);
            Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), Subject, change), change, DateTimeOffset.UtcNow);
            Consumer = new(Source.Instance, Source.Database, new(), Source.Lease, Base.Resources, Base.Index, Base.Inbox);
            if (accept) _accepted = Base.Service.Start(Guid.NewGuid(), Subject, Source.Request(new BindingTarget(Source.BindingA)), DateTimeOffset.UtcNow);
        }
        catch { Base.Dispose(); throw; }

        System.Collections.Immutable.ImmutableArray<string> Arguments(string role, int exit, bool wait) =>
            ["execution-hook", role, Marker, configuration ? Source.Configuration : "-", exit.ToString(System.Globalization.CultureInfo.InvariantCulture), wait.ToString()];
    }

    internal void Cancel()
    {
        var state = State;
        new ExecutionCancellationStore(Source.Instance, Source.Database, new(), Source.Lease).Cancel(
            ExecutionCancellationStore.Identity(Guid.NewGuid(), Subject, Accepted.JournalId, state.Journal.OperationId,
                Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), Accepted.JournalId, DateTimeOffset.UtcNow);
    }
    internal async Task Ready(string role)
    {
        var path = Marker + "." + role; var timer = Stopwatch.StartNew();
        while (!File.Exists(path) && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(10);
        Assert.True(File.Exists(path), path);
    }
    public void Dispose() => Base.Dispose();
}

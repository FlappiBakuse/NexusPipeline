using System.Runtime.Versioning;
using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Automation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionSequentialFixture : IDisposable
{
    internal const string Subject = "sequential-fixture";
    internal ExecutionAdmissionFixture Base { get; } = new();
    internal PlanBuilderFixture Source => Base.Source;
    private readonly AcceptedExecutionAdmission? _accepted;
    internal AcceptedExecutionAdmission Accepted => _accepted ?? throw new InvalidOperationException();
    internal Guid ParentId => Accepted.Receipt.JournalId;
    internal IReadOnlyList<ExecutionPlanStep> Steps => Accepted.Prepared.Total.Plan.ReadWire().Steps;
    internal AttemptTerminalStore Terminal => new(Source.Instance, Source.Database, new(), Source.Lease);
    internal string Marker { get; }
    internal IReadOnlyDictionary<string, Guid> Sessions { get; }
    private readonly List<SequentialNativeStep> _native = [];

    internal ExecutionSequentialFixture(bool configuration = false, string mode = "tail", bool accept = true,
        AutomationJudgement? judgement = null, AutomationLimits? limits = null, bool independentSuccessors = false, bool successDependency = false,
        bool captureFiles = true)
    {
        try
        {
        Marker = Path.Combine(Source.Root, "sequential-ready");
        var catalog = Source.Definitions.Read(); var native = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var scriptId = configuration ? Source.ScriptId : Source.OtherScriptId;
        var script = catalog.Scripts.Single(item => item.Id == scriptId);
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Sequential native", AutomationRootKind.Script, Path.GetDirectoryName(native)!);
        var newA = new AutomationBinding(Guid.Parse("f0000000-0000-0000-0000-000000000001"), 0, Source.UserA, scriptId, true, 0);
        var newC = new AutomationBinding(Guid.Parse("01000000-0000-0000-0000-000000000001"), 0, Source.UserC, scriptId, true, 0);
        var order = configuration ? new[] { Source.BindingA, Source.BindingB, Source.BindingC } : new[] { newA.Id, Source.OtherBinding, newC.Id };
        var head = script with { RootId = root.Id, Executable = new(root.Id, Path.GetFileName(native)),
            Arguments = ["process-output", mode, Marker], LogPaths = captureFiles ? script.LogPaths : [],
            Judgement = judgement ?? script.Judgement, Limits = limits ?? script.Limits with { MaximumAttempts = 1 } };
        var scripts = new List<AutomationScript> { head };
        List<AutomationBinding> bindings = configuration ? [] : [newA, newC];
        if (independentSuccessors || successDependency)
        {
            var successors = LimitSuccessors(head, Source.UserB, Source.UserC, Marker);
            scripts.AddRange(successors.Scripts); bindings.AddRange(successors.Bindings);
            order = [order[0], successors.Bindings[0].Id, successors.Bindings[1].Id];
        }
        var byBinding = catalog.Bindings.Concat(bindings).ToDictionary(binding => binding.Id);
        var groups = order.Select(id => byBinding[id].ScriptId).Distinct().ToArray();
        bindings = bindings.Select(binding => binding with { IsEnabled = order.Contains(binding.Id) }).ToList();
        bindings.AddRange(catalog.Bindings.Where(binding => groups.Contains(binding.ScriptId) && !order.Contains(binding.Id))
            .Select(binding => binding with { IsEnabled = false }));
        var userOrder = order.Select(id => byBinding[id].UserId).Distinct().ToArray();
        var users = catalog.Users.Select(user => user with { Order = Array.IndexOf(userOrder, user.Id) }).ToImmutableArray();
        var change = new AutomationChanges(catalog.Revision, [root], [.. scripts], users, [.. bindings],
            [catalog.Queues.Single() with { ScriptIds = [.. groups], SuccessDependencies = successDependency ? [new(groups[1], [groups[0]])] : [] }], [], []);
        Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), Subject, change), change, PlanBuilderFixture.CreatedAt);
        if (!accept) { Sessions = new Dictionary<string, Guid>(); return; }
        var operation = Guid.NewGuid();
        Base.Service.Start(operation, Subject, Source.Request(new QueueTarget(Source.QueueId)), PlanBuilderFixture.CreatedAt);
        _accepted = Base.Take(operation); Assert.Equal(order.Select(id => id.ToString("D")), Steps.Select(step => step.BindingId));
        var sessions = new Dictionary<string, Guid>(StringComparer.Ordinal); var runs = new ConfigurationRunStore(Source.Instance, Source.Database, new());
        foreach (var (step, prepared) in Accepted.Prepared.Configurations)
        {
            var session = Guid.NewGuid();
            runs.Begin(ConfigurationRunStore.BeginIdentity(session, Subject, ParentId, step, prepared), ParentId, step, prepared, PlanBuilderFixture.CreatedAt);
            sessions.Add(step, session);
        }
        Sessions = sessions;
        }
        catch { _accepted?.Dispose(); Base.Dispose(); throw; }
    }

    internal StepWriterScope Scope(int index) => new(Source.Instance, Source.Database, new(), Source.Lease, ParentId, Steps[index].StepId);
    private static (AutomationScript[] Scripts, AutomationBinding[] Bindings) LimitSuccessors(AutomationScript head, Guid secondUser, Guid thirdUser, string marker)
    {
        var scripts = new[] { secondUser, thirdUser }.Select((_, index) => head with
        {
            Id = Guid.NewGuid(), Revision = 0, Name = "Limit successor " + (index + 1),
            Arguments = ["process-output", "tail", marker], ConfigurationPaths = [], LogPaths = [], Limits = new(1, 30, 120),
            Judgement = new(AutomationJudgementLanguage.Keywords, ["done"], ["failed"], null)
        }).ToArray();
        return (scripts, new[] { secondUser, thirdUser }.Select((user, index) =>
            new AutomationBinding(Guid.NewGuid(), 0, user, scripts[index].Id, true, 0)).ToArray());
    }
    internal SequentialNativeStep Create(int index)
    {
        var result = new SequentialNativeStep(this, index); _native.Add(result); return result;
    }
    internal AttemptWriterState State => AttemptWriterState.Read(Source.Instance, Source.Database, new(), ParentId);

    public void Dispose()
    {
        try { for (var index = _native.Count - 1; index >= 0; index--) _native[index].Dispose(); }
        finally { _accepted?.Dispose(); Base.Dispose(); }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SequentialNativeStep : IDisposable
{
    private readonly ExecutionSequentialFixture _fixture;
    internal StepWriterScope Scope { get; }
    internal ConfigurationWriterProcess? Writer { get; }
    internal ConfigurationRunController? Controller { get; }
    internal StepEvidenceCollector Collector { get; }
    internal StepEvidenceBinding Binding { get; }
    private StoppedConfigurationEditor? _stopped;
    private bool _collected;
    private bool _configurationFinished;
    internal SequentialNativeStep(ExecutionSequentialFixture fixture, int index)
    {
        _fixture = fixture; var source = fixture.Source; Scope = fixture.Scope(index); var stepId = fixture.Steps[index].StepId;
        if (fixture.Sessions.TryGetValue(stepId, out var session))
            Controller = new(source.Instance, source.Database, new(), source.Lease, fixture.Base.Index, session, ExecutionSequentialFixture.Subject,
                fixture.Accepted.Prepared.Configurations[stepId], Scope);
        else Writer = new(source.Instance, source.Lease, Scope);
        var state = fixture.State; Binding = StepResultCodec.Binding(state.Latest.Root, state.Steps.Single(item => item.StepId == stepId));
        Collector = Writer is not null ? new(source.Instance, source.Database, new(), source.Lease, Binding, Writer) :
            new(source.Instance, source.Database, new(), source.Lease, Binding, Controller!);
    }
    internal void Start() { Scope.EnterApplying(); Collector.Start(); }
    internal async Task Exit()
    {
        Assert.Equal(37, await (Writer is not null ? Writer.AwaitRootAsync(TimeSpan.FromSeconds(10)) :
            Controller!.AwaitRootAsync(TimeSpan.FromSeconds(10))));
        await Collector.ObserveExitAsync();
    }
    internal async Task Stop()
    { _stopped = Writer is not null ? await Writer.StopAsync(TimeSpan.FromSeconds(10)) : await Controller!.StopWriterAsync(TimeSpan.FromSeconds(10)); }
    internal async Task Collect(bool record = true)
    {
        var proof = await Collector.CollectAsync(_stopped ?? throw new InvalidOperationException(), TimeSpan.FromSeconds(10));
        if (record) _fixture.Terminal.RecordCollected(_fixture.ParentId, proof);
        _collected = true;
    }
    internal async Task FinishConfiguration(Guid? operationId = null)
    {
        if (Controller is null) return;
        await Controller.FinishAsync(operationId ?? Guid.NewGuid(), interrupted: false); _configurationFinished = true;
    }
    internal async Task Complete()
    { Start(); await Exit(); await Stop(); await Collect(); await FinishConfiguration(); }
    public void Dispose()
    {
        if (_stopped is not null && _collected) Collector.Dispose();
        Writer?.Dispose();
        if (_configurationFinished) Controller?.Dispose();
    }
}

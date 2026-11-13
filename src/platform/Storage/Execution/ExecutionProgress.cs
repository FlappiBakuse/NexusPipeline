using NexusPipeline.Platform.Serialization.Protocol;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed record ExecutionTaskView(string TaskId, string Title, bool IsSelected, bool IsEnabled,
    IReadOnlyList<string> Dependencies, string State, string? ReasonCode)
{
    public IReadOnlyDictionary<string, string>? TitleLocales { get; init; }
    public string WorkflowRole { get; init; } = "business";
    public string? CompletionKind { get; init; }
}
public sealed record ExecutionStepView(string StepId, string ScriptId, string? UserId, string? BindingId,
    IReadOnlyList<string> DependsOn, string Phase, IReadOnlyList<ExecutionTaskView> Tasks)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasTaskReport { get; init; }
    public IReadOnlyList<string> LogRoles { get; init; } = Array.AsReadOnly(new[] { "main" });
    public int MaximumInvocations { get; init; } = 1;
    public int InvocationOrdinal { get; init; }
    public IReadOnlyList<ExecutionInvocationView> Invocations { get; init; } = [];
    public string? Engine { get; init; }
    public string? Business { get; init; }
    public string? ReasonCode { get; init; }
}
public sealed record ExecutionAccountView(Guid UserId, AttemptFacts Facts);
public sealed record ExecutionInvocationView(int Ordinal, string Engine, string Business, string ReasonCode)
{
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public long? DurationMilliseconds { get; init; }
}
public sealed record ExecutionRunView(Guid OperationId, Guid RunId, Guid AttemptId, long Ordinal,
    string Phase, bool CanCancel, IReadOnlyList<ExecutionStepView> Steps)
{
    public IReadOnlyList<ExecutionAccountView> Accounts { get; init; } = [];
    public bool CancellationRequested { get; init; }
    public ExecutionRunDisplay? Display { get; init; }
}

public sealed record ExecutionRunDisplay(string TargetKind, Guid TargetId, string TargetName, string Mode, long MaximumAttempts)
{
    internal static ExecutionRunDisplay From(ExecutionSelection selection, long maximumAttempts, bool automatic)
    {
        var script = selection.Items.FirstOrDefault()?.Script;
        if (selection.Request.Target is ProjectInitializationTarget initialization)
            return new("projectInitialization", initialization.BindingId, script?.Name ?? "", "manual", maximumAttempts);
        return new(selection.Queue is null ? "script" : "queue", selection.Queue?.Id ?? script?.Id ?? Guid.Empty,
            selection.Queue?.Name ?? script?.Name ?? "", automatic ? "auto" : "manual", maximumAttempts);
    }
}

internal sealed class ExecutionProgress
{
    private readonly object _gate = new();
    private readonly Action _changed;
    private readonly string _subject;
    private ExecutionRunView _view;

    internal ExecutionProgress(AcceptedExecutionAdmission accepted, Action changed)
    {
        _subject = accepted.Identity.SubjectId;
        _changed = changed;
        var attempt = accepted.Prepared.Attempt.Plan.ReadWire();
        var total = accepted.Prepared.Total.Plan.ReadWire();
        _view = new(accepted.Identity.OperationId, Guid.Parse(attempt.RunId), Guid.Parse(attempt.AttemptId),
            attempt.AttemptOrdinal, "preparing", false, total.Steps.Select(step => new ExecutionStepView(
                step.StepId, step.ScriptId, step.UserId, step.BindingId, Array.AsReadOnly(step.DependsOn.ToArray()), "pending",
                Array.AsReadOnly(step.Tasks.Select(task => new ExecutionTaskView(task.TaskId, task.Title, task.IsSelected,
                    task.IsEnabled, Array.AsReadOnly(task.Dependencies.ToArray()), "unknown", null)
                    { WorkflowRole = task.WorkflowRole ?? "business", TitleLocales = task.TitleLocales?.ToDictionary(pair => pair.Key, pair => pair.Value.GetString()!) }).ToArray()))
                { HasTaskReport = step.Adapter is not null || step.Provider is not null,
                    MaximumInvocations = checked((int)(step.MaximumAttempts ?? 1)),
                    LogRoles = Array.AsReadOnly(ExecutionBindingRoles.Required(step)
                    .Where(role => role != "judgement").Select(role => role == "process" ? "main" : role).ToArray()) }).ToArray())
        { Display = accepted.Prepared.Display };
    }

    internal ExecutionRunView Read(string subject)
    { lock (_gate) return _view with { CanCancel = subject == _subject && !_view.CancellationRequested, Steps = Array.AsReadOnly(_view.Steps.ToArray()) }; }

    internal void CancelAccepted(Guid runId, Guid attemptId)
    {
        lock (_gate)
        {
            if (_view.RunId != runId || _view.AttemptId != attemptId || _view.CancellationRequested) return;
            _view = _view with { CancellationRequested = true, Phase = "stopping" };
        }
        _changed();
    }

    internal void Step(string id, string phase)
    {
        lock (_gate)
            _view = _view with { Phase = phase, Steps = _view.Steps.Select(step => step.StepId == id ? step with
            {
                Phase = phase, Tasks = phase == "preparing" ? Array.AsReadOnly(step.Tasks.Select(task =>
                    task with { State = "unknown", ReasonCode = null, CompletionKind = null }).ToArray()) : step.Tasks
            } : step).ToArray() };
        _changed();
    }

    internal void Phase(string phase)
    { lock (_gate) _view = _view with { Phase = phase }; _changed(); }

    internal void Invocation(string stepId, int ordinal)
    {
        lock (_gate)
            _view = _view with { Steps = _view.Steps.Select(step => step.StepId == stepId
                ? step with { InvocationOrdinal = ordinal } : step).ToArray() };
        _changed();
    }

    internal void Observe(string stepId, IReadOnlyList<AdapterTaskObservation> observations)
    {
        lock (_gate)
            _view = _view with { Steps = _view.Steps.Select(step => step.StepId != stepId ? step : step with
            {
                Tasks = Array.AsReadOnly(step.Tasks.Select(task => observations.FirstOrDefault(value => value.TaskId == task.TaskId)
                    is { } result ? task with { State = result.State.ToString().ToLowerInvariant(), ReasonCode = result.ReasonCode,
                        CompletionKind = NexusPipeline.Platform.Extensions.TaskAdapterProtocol.WriteObservation(result).CompletionKind } : task).ToArray())
            }).ToArray() };
        _changed();
    }
}

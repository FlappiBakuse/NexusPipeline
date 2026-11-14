using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class AttemptHistoryCursor
{
    private ImmutableArray<JournalDocument> _history = [];
    private EffectGateState? _gate;
    internal int Count => _history.Length;
    internal JournalDocument? Previous => _history.IsEmpty ? null : _history[^1];
    internal HashSet<BlobReference> Baselines { get; } = [];
    internal HashSet<HookResultIntent> Hooks { get; } = [];
    internal HashSet<StepResultState> Results { get; } = [];
    internal HistoricalTargetEvidence Targets { get; }
    internal ImmutableDictionary<string, int>? PreviousInvocations { get; set; }

    internal AttemptHistoryCursor(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        ExecutionPlan total, string totalSha256)
    { Targets = new(instance, database, contracts, total, totalSha256); }

    private AttemptHistoryCursor(AttemptHistoryCursor previous)
    {
        _history = previous._history; _gate = previous._gate;
        Baselines.UnionWith(previous.Baselines); Hooks.UnionWith(previous.Hooks); Results.UnionWith(previous.Results);
        Targets = previous.Targets.Fork(); PreviousInvocations = previous.PreviousInvocations;
    }

    internal AttemptHistoryCursor? Fork(IReadOnlyList<JournalDocument> history, EffectGateState gate)
    {
        if (history.Count < Count || _gate is EffectGateState.Entered or EffectGateState.Sealed && gate != _gate) return null;
        for (var index = 0; index < Count; index++)
            if (!ReferenceEquals(_history[index], history[index])) return null;
        return new(this);
    }

    internal void Complete(IReadOnlyList<JournalDocument> history, EffectGateState gate)
    { _history = history.ToImmutableArray(); _gate = gate; }
}

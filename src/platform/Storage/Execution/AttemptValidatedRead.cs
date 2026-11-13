using System.Collections.Immutable;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class AttemptValidatedRead
{
    private readonly ImmutableArray<JournalDocument> _history;
    private readonly ImmutableArray<CaptureSavedEvidence> _captures;
    private readonly string _totalSha256;
    private readonly object _viewGate = new();
    private ExecutionPlan? _viewTotal;
    private ImmutableArray<StepWriterState> _jobs, _allJobs;
    private ImmutableArray<ExecutionStepCompletion> _coverage;
    internal ImmutableDictionary<string, int> PreviousInvocations { get; }
    internal ImmutableArray<JournalDocument> History => _history;
    internal ImmutableArray<CaptureSavedEvidence> Captures => _captures;

    internal AttemptValidatedRead(IReadOnlyList<JournalDocument> history,
        ImmutableArray<CaptureSavedEvidence> captures, string totalSha256, ImmutableDictionary<string, int> previousInvocations)
    {
        if (history.Count == 0) throw Invalid();
        _history = history.ToImmutableArray(); _totalSha256 = totalSha256; PreviousInvocations = previousInvocations;
        _captures = captures.Select(value => value with { Source = value.Source with {
            HelperImage = value.Source.HelperImage with {
                RuntimeFiles = value.Source.HelperImage.RuntimeFiles.ToImmutableArray() } } }).ToImmutableArray();
    }

    private AttemptValidatedRead(ImmutableArray<JournalDocument> history,
        ImmutableArray<CaptureSavedEvidence> captures, string totalSha256, ImmutableDictionary<string, int> previousInvocations)
    { _history = history; _captures = captures; _totalSha256 = totalSha256; PreviousInvocations = previousInvocations; }

    internal AttemptValidatedRead WithCoherentFreshHistory(IReadOnlyList<JournalDocument> fresh)
    {
        if (fresh.Count != _history.Length) throw Invalid();
        for (var index = 0; index < fresh.Count; index++)
            if (!fresh[index].HasSameBytes(_history[index])) throw Invalid();
        if (fresh.Select((document, index) => ReferenceEquals(document, _history[index])).All(value => value)) return this;
        return new(fresh.ToImmutableArray(), _captures, _totalSha256, PreviousInvocations);
    }

    internal ImmutableArray<StepWriterState> Jobs(AttemptWriterState state, bool all)
    {
        if (!ReferenceEquals(state.Latest, _history[^1])) return all
            ? WriterIntentCodec.ReadAllJobs(state.Latest.Root, state.Total) : WriterIntentCodec.ReadJobs(state.Latest.Root, state.Total);
        lock (_viewGate)
        {
            RequireTotal(state.Total);
            if (all && !_allJobs.IsDefault) return _allJobs;
            if (!all && !_jobs.IsDefault) return _jobs;
            using var read = new AttemptReadScope(_history, state.Total);
            if (all) return _allJobs = WriterIntentCodec.ReadAllJobs(state.Latest.Root, state.Total);
            return _jobs = WriterIntentCodec.ReadJobs(state.Latest.Root, state.Total);
        }
    }

    internal ImmutableArray<ExecutionStepCompletion> Coverage(AttemptWriterState state)
    {
        if (!ReferenceEquals(state.Latest, _history[^1])) return StepResultCodec.Coverage(state.Latest.Root, state.Total);
        lock (_viewGate)
        {
            RequireTotal(state.Total);
            if (!_coverage.IsDefault) return _coverage;
            using var read = new AttemptReadScope(_history, state.Total);
            return _coverage = StepResultCodec.Coverage(state.Latest.Root, state.Total);
        }
    }

    private void RequireTotal(ExecutionPlan total)
    {
        if (ReferenceEquals(_viewTotal, total)) return;
        _viewTotal = total; _jobs = default; _allJobs = default; _coverage = default;
    }

    internal void RequireFor(AttemptWriterState state)
    {
        var last = _history[^1];
        if (!last.HasSameBytes(state.Latest) || last.JournalId != state.Journal.JournalId ||
            last.PlanPath != state.Journal.PlanPath || last.PlanDigest != state.Journal.PlanDigest ||
            state.Attempt.ExecutionPlan.Sha256 != _totalSha256 ||
            state.Journal.Revision > last.Revision) throw Invalid();
    }

    private static IOException Invalid() => new("execution_writer_journal_changed");
}

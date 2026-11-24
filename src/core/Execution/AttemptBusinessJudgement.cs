using NexusPipeline.Core.Automation;
using System.Collections.Immutable;

namespace NexusPipeline.Core.Execution;

public sealed class AttemptBusinessJudgement
{
    private readonly object _gate = new();
    private readonly KeywordJudgementRules _success;
    private readonly KeywordJudgementRules _failure;
    private BusinessJudgementDecision? _decision;
    private JudgementInvocation? _active;
    private long _generation;
    private bool _cancelled;
    private bool _finalRequested;
    private bool _finalCompleted;

    public Guid AttemptId { get; }
    public JudgementInputIdentity InputIdentity { get; }
    public ExecutionJudgementMode Mode { get; }
    public BusinessJudgementDecision? Decision { get { lock (_gate) return _decision; } }
    public BusinessOutcome Business { get { lock (_gate) return _decision?.Outcome ?? BusinessOutcome.Unverified; } }
    public bool IsBusy { get { lock (_gate) return _active is not null; } }
    public bool IsCancelled { get { lock (_gate) return _cancelled; } }
    public bool FinalCompleted { get { lock (_gate) return _finalCompleted; } }

    public AttemptBusinessJudgement(Guid attemptId, AutomationJudgement judgement)
        : this(attemptId, judgement, Guid.NewGuid()) { }

    public static AttemptBusinessJudgement Replay(Guid attemptId, AutomationJudgement judgement, Guid ownerId)
        => new(attemptId, judgement, ownerId);

    private AttemptBusinessJudgement(Guid attemptId, AutomationJudgement judgement, Guid ownerId)
    {
        ArgumentNullException.ThrowIfNull(judgement);
        if (attemptId == Guid.Empty || ownerId == Guid.Empty || !Enum.IsDefined(judgement.Language) ||
            !Valid(judgement.SuccessKeywords) || !Valid(judgement.FailureKeywords))
            throw new ArgumentException("execution_judgement_definition_invalid");
        AttemptId = attemptId;
        InputIdentity = new(attemptId, ownerId);
        _success = new(judgement.Language == AutomationJudgementLanguage.Keywords ? judgement.SuccessKeywords : []);
        _failure = new(judgement.Language == AutomationJudgementLanguage.Keywords ? judgement.FailureKeywords : []);
        Mode = judgement.Language != AutomationJudgementLanguage.Keywords ? ExecutionJudgementMode.Script :
            _success.IsConfigured || _failure.IsConfigured ? ExecutionJudgementMode.Keywords : ExecutionJudgementMode.None;
    }

    public BusinessJudgementDecision? ObserveLine(JudgementInputIdentity identity, string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            if (identity != InputIdentity || _cancelled || _decision is not null || Mode != ExecutionJudgementMode.Keywords) return null;
            var success = _success.Consume(line);
            var failure = _failure.Consume(line);
            if (success is null && failure is null) return null;
            var outcome = failure is not null && (success is null || failure <= success) ? BusinessOutcome.Failed : BusinessOutcome.Succeeded;
            return _decision = new(outcome, outcome == BusinessOutcome.Failed ? "judgement_keyword_failed" : "judgement_keyword_succeeded", "");
        }
    }

    public JudgementInvocation? TryBeginPeriodic()
    {
        lock (_gate)
        {
            if (Mode != ExecutionJudgementMode.Script || _cancelled || _active is not null || _finalRequested || _decision is not null) return null;
            return Begin(false);
        }
    }

    public void RequestFinal()
    {
        lock (_gate)
            if (Mode == ExecutionJudgementMode.Script && !_cancelled && !_finalCompleted) _finalRequested = true;
    }

    public JudgementInvocation? TryBeginFinal()
    {
        lock (_gate)
        {
            if (!_finalRequested || _cancelled || _finalCompleted || _active is not null) return null;
            return Begin(true);
        }
    }

    public bool Complete(JudgementInvocation invocation, BusinessJudgementResult result)
    {
        lock (_gate)
        {
            if (!Matches(invocation)) return false;
            if (_cancelled) { _active = null; return false; }
            ArgumentNullException.ThrowIfNull(result);
            if (result.Outcome is not (BusinessOutcome.Unverified or BusinessOutcome.Succeeded or BusinessOutcome.Partial or BusinessOutcome.Failed) ||
                result.Reason is null || result.Reason.Length > 4096 || result.Outcome == BusinessOutcome.Partial && string.IsNullOrWhiteSpace(result.Reason))
                throw new ArgumentException("execution_judgement_result_invalid");
            Finish(invocation);
            if (_decision is not null || result.Outcome == BusinessOutcome.Unverified) return false;
            _decision = new(result.Outcome, "judgement_script_" + result.Outcome.ToString().ToLowerInvariant(), result.Reason);
            return true;
        }
    }

    public bool CompleteFault(JudgementInvocation invocation)
    {
        lock (_gate)
        {
            if (!Matches(invocation)) return false;
            if (_cancelled) { _active = null; return false; }
            Finish(invocation);
            return true;
        }
    }

    public void Cancel()
    {
        lock (_gate) _cancelled = true;
    }

    private JudgementInvocation Begin(bool final) => _active = new(AttemptId, _generation = checked(_generation + 1), final, InputIdentity.OwnerId);
    private bool Matches(JudgementInvocation invocation) => _active is not null && _active == invocation;
    private void Finish(JudgementInvocation invocation)
    {
        _active = null;
        if (invocation.IsFinal) _finalCompleted = true;
    }

    private static bool Valid(System.Collections.Immutable.ImmutableArray<string> values) => !values.IsDefault && values.Length <= 64 &&
        values.All(value => !string.IsNullOrEmpty(value) && value.Length <= 4096 && !value.Contains('\0'));
}

internal sealed class KeywordJudgementRules
{
    private readonly List<HashSet<string>> _groups = [];
    internal bool IsConfigured => _groups.Count != 0;

    internal KeywordJudgementRules(ImmutableArray<string> expressions)
    {
        foreach (var expression in expressions)
            foreach (var rawLine in expression.Split('\n'))
            {
                var words = rawLine.Trim().Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (words.Length != 0) _groups.Add(new(words, StringComparer.OrdinalIgnoreCase));
            }
    }

    internal int? Consume(string line)
    {
        int? firstCompletion = null;
        foreach (var pending in _groups)
        {
            if (pending.Count == 0) continue;
            var hits = new List<(string Word, int Position)>();
            foreach (var word in pending)
            {
                var position = line.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                if (position >= 0) hits.Add((word, position));
            }
            foreach (var hit in hits) pending.Remove(hit.Word);
            if (pending.Count != 0 || hits.Count == 0) continue;
            // Earlier lines already supplied other words; this group becomes true at its last missing word on this line.
            var completion = hits.Max(hit => hit.Position);
            firstCompletion = firstCompletion is null ? completion : Math.Min(firstCompletion.Value, completion);
        }
        return firstCompletion;
    }
}

public enum ExecutionJudgementMode { None, Keywords, Script }

public sealed record JudgementInputIdentity(Guid AttemptId, Guid OwnerId);
public sealed record JudgementInvocation(Guid AttemptId, long Generation, bool IsFinal, Guid OwnerId);
public sealed record BusinessJudgementResult(BusinessOutcome Outcome, string Reason);
public sealed record BusinessJudgementDecision(BusinessOutcome Outcome, string ReasonCode, string Reason);

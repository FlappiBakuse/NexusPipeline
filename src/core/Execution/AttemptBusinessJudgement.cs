using NexusPipeline.Core.Automation;

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

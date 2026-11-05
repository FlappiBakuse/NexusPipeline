namespace NexusPipeline.Core.Execution;

public static class ProviderFactsPolicy
{
    public static ExecutionStepFact Compose(ExecutionStepFact primary, ExecutionProviderDecision? terminal, bool wasExecuted)
    {
        ArgumentNullException.ThrowIfNull(primary);
        if (primary.Engine == EngineOutcome.Running || !Enum.IsDefined(primary.Engine) ||
            !wasExecuted && (primary.Engine != EngineOutcome.NotStarted || terminal is not null) ||
            wasExecuted && primary.Engine == EngineOutcome.NotStarted ||
            terminal is not null && (terminal.Engine is not (EngineOutcome.Succeeded or EngineOutcome.Failed or EngineOutcome.Cancelled) ||
                terminal.Sequence is < 2 or > ProviderProtocolState.MaximumEvents || !Reason(terminal.ReasonCode)))
            throw new ArgumentException("execution_provider_facts_invalid");
        if (!wasExecuted) return primary with { };
        var auxiliary = terminal?.Engine ?? EngineOutcome.Unknown;
        var engine = AttemptFactsPolicy.AggregateEngine([primary.Engine, auxiliary]);
        var reason = engine == primary.Engine ? primary.ReasonCode :
            terminal?.ReasonCode ?? "execution_provider_terminal_unverified";
        return primary with { Engine = engine, ReasonCode = reason };
    }
    public static ExecutionStepFact ComposeLauncher(ExecutionStepFact primary, EngineOutcome launchEngine, string launchReason)
    {
        ArgumentNullException.ThrowIfNull(primary);
        if (!Enum.IsDefined(primary.Engine) || primary.Engine == EngineOutcome.Running ||
            !Enum.IsDefined(launchEngine) || launchEngine is EngineOutcome.Running or EngineOutcome.Cancelled || !Reason(launchReason))
            throw new ArgumentException("execution_provider_launcher_facts_invalid");
        var engine = AttemptFactsPolicy.AggregateEngine([primary.Engine, launchEngine]);
        return primary with { Engine = engine, ReasonCode = engine == primary.Engine ? primary.ReasonCode : launchReason };
    }

    private static bool Reason(string? value) => value is { Length: >= 1 and <= 80 } &&
        value[0] is >= 'a' and <= 'z' && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

using System.Collections.Immutable;

namespace NexusPipeline.Core.Execution;

public sealed record CompletionAuthorizationSource(FrozenCompletionIntent Intent, string SubjectId);

public sealed class CompletionAuthorizationContext
{
    public CompletionActionGroup Frozen { get; }
    public ImmutableArray<CompletionAuthorizationSource> Sources { get; }

    private CompletionAuthorizationContext(CompletionActionGroup frozen, ImmutableArray<CompletionAuthorizationSource> sources)
    { Frozen = frozen; Sources = sources; }

    public static CompletionAuthorizationContext Create(CompletionActionGroup frozen,
        IReadOnlyList<CompletionAuthorizationSource> sources)
    {
        ArgumentNullException.ThrowIfNull(frozen); ArgumentNullException.ThrowIfNull(sources);
        if (sources.Any(item => item is null || item.Intent is null)) throw new ArgumentException("completion_authorization_context_unbound");
        var ordered = sources.OrderBy(item => item.Intent.RunId).ToImmutableArray();
        var actual = CompletionActionPolicy.Combine(ordered.Select(item => item.Intent).ToArray());
        if (actual.Action != frozen.Action || !actual.Intents.SequenceEqual(frozen.Intents.OrderBy(item => item.RunId)) ||
            ordered.Any(item => string.IsNullOrWhiteSpace(item.SubjectId) || item.SubjectId.Length > 512 ||
                item.SubjectId != item.SubjectId.Trim() || item.SubjectId.Any(char.IsControl)))
            throw new ArgumentException("completion_authorization_context_unbound");
        return new(actual, ordered);
    }

    public bool Matches(CompletionAuthorizationContext other) =>
        other is not null && Frozen.Action == other.Frozen.Action && Sources.SequenceEqual(other.Sources);
}

public interface ICompletionLocalAuthorization
{
    Task<bool> RequestApprovalAsync(CompletionAuthorizationContext context, CancellationToken cancellation);
    bool IsApproved(CompletionAuthorizationContext context);
    T UseApproved<T>(CompletionAuthorizationContext context, Func<T> effect);
    void Forget(CompletionAuthorizationContext context);
    void ObserveTerminal(AttemptReceipt receipt);
    void Revoke();
}

public interface IHostCompletionExit
{
    void RequestExit();
}

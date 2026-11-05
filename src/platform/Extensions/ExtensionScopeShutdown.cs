namespace NexusPipeline.Platform.Extensions;

internal static class ExtensionScopeShutdown
{
    internal sealed class State
    {
        internal bool IsStopping { get; private set; }
        private Task? _stopped;
        internal Task Stop(IReadOnlyList<Member> batch)
        {
            IsStopping = true;
            if (_stopped is null)
            {
                foreach (var item in batch) item.Scope.Activation.Revoke();
                _stopped = CloseAsync(batch);
            }
            return _stopped;
        }
    }

    internal sealed record Member(ExtensionScope Scope, Task Callback);
    internal static IEnumerable<Member> Members(ExtensionScope? current, Task currentCallback,
        ExtensionScope? candidate, Task? candidateCallback)
    {
        if (current is not null) yield return new(current, currentCallback);
        if (candidate is not null && !ReferenceEquals(candidate, current))
            yield return new(candidate, candidateCallback ?? throw new IOException("extension_candidate_callback_unconfirmed"));
    }

    internal static async Task CloseAsync(IReadOnlyList<Member> batch)
    {
        await Task.Yield();
        var cleanup = batch.Select(item => item.Scope.DisposeAsync().AsTask()).ToArray();
        var callbacks = batch.Select(item => EndedAsync(item.Callback)).ToArray();
        await Task.WhenAll(cleanup.Concat(callbacks)).ConfigureAwait(false);
    }

    private static async Task EndedAsync(Task actual)
    {
        try { await actual.ConfigureAwait(false); }
        catch { }
    }
}


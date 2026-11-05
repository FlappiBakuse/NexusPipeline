using NexusPipeline.Extensibility.Compilation;

namespace NexusPipeline.Platform.Extensions;

internal sealed class ProjectCompilerRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Activation, string Id), Entry> _entries = [];
    private sealed record Entry(ExtensionScope Scope, string Id, IProjectCompiler Compiler);

    internal IAsyncDisposable Register(ExtensionScope scope, string compilerId, IProjectCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler); RequireId(compilerId); scope.Cancellation.ThrowIfCancellationRequested();
        var entry = new Entry(scope, compilerId, compiler);
        lock (_gate)
            if (!_entries.TryAdd((scope.ActivationId, compilerId), entry)) throw new InvalidOperationException("compiler_already_registered");
        return new Registration(this, entry);
    }

    internal async ValueTask<T> UseAsync<T>(string extensionId, string compilerId,
        Func<IProjectCompiler, Action, ValueTask<T>> callback, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); RequireId(compilerId);
        Entry entry;
        lock (_gate)
        {
            var found = _entries.Values.Where(item => item.Scope.Activation.ExtensionId == extensionId && item.Id == compilerId &&
                item.Scope.IsPublished && !item.Scope.Cancellation.IsCancellationRequested).Take(2).ToArray();
            if (found.Length != 1) throw new IOException("compiler_unavailable");
            entry = found[0];
        }
        using var use = entry.Scope.BeginUse();
        void Check()
        {
            cancellation.ThrowIfCancellationRequested(); entry.Scope.Cancellation.ThrowIfCancellationRequested();
            lock (_gate)
                if (!entry.Scope.IsPublished || !_entries.TryGetValue((entry.Scope.ActivationId, compilerId), out var current) ||
                    !ReferenceEquals(entry, current)) throw new IOException("compiler_activation_changed");
        }
        Check(); var result = await callback(entry.Compiler, Check).ConfigureAwait(false); Check(); return result;
    }

    internal static void RequireId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id[0] is < 'a' or > 'z' ||
            id.Any(value => value is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '.' and not '-'))
            throw new ArgumentException("compiler_id_invalid");
    }

    private sealed class Registration(ProjectCompilerRegistry owner, Entry entry) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lock (owner._gate)
                if (owner._entries.TryGetValue((entry.Scope.ActivationId, entry.Id), out var current) && ReferenceEquals(entry, current))
                    owner._entries.Remove((entry.Scope.ActivationId, entry.Id));
            return ValueTask.CompletedTask;
        }
    }
}

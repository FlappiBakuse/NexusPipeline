using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Execution;

namespace NexusPipeline.Platform.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionRegistrationPort : IExecutionRegistrationPort
{
    private readonly ExtensionScope _scope;
    private readonly VerifiedProviderPackage _package;
    private readonly ExecutionProviderRegistry _registry;
    private readonly VerifiedProviderPackage.Prepared? _prepared;
    private int _registered;

    internal ExecutionRegistrationPort(ExtensionScope scope, VerifiedProviderPackage package, ExecutionProviderRegistry registry,
        VerifiedProviderPackage.Prepared? prepared = null)
    {
        if (scope.Activation.ExtensionId != package.Manifest.ExtensionId ||
            prepared is not null && !ReferenceEquals(prepared.Package, package))
            throw new IOException("execution_provider_activation_unbound");
        _scope = scope; _package = package; _registry = registry; _prepared = prepared;
    }

    public async ValueTask RegisterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _scope.Cancellation.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _registered, 1, 0) != 0)
            throw new InvalidOperationException("execution_provider_already_registered");
        IAsyncDisposable? registration = null;
        try
        {
            using var verified = _prepared is null ? _package.Hold() : _prepared.TakeHold(_package);
            registration = _registry.Register(_scope, _package);
            await _scope.OwnAsync(registration).ConfigureAwait(false);
            _scope.Cancellation.ThrowIfCancellationRequested();
        }
        catch
        {
            if (registration is not null) await registration.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

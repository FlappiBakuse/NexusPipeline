using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Emulation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledEmulatorPort(PreparedExtensionActivation source, InstalledExtensionConsumption consumption,
    ExtensionScope scope, EmulatorProviderRegistry registry, ExtensionManifest manifest) : IEmulatorRegistration
{
    public async ValueTask RegisterAsync(string providerId, IEmulatorProvider provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider); cancellationToken.ThrowIfCancellationRequested(); source.RequireBeforeCallback();
        ProjectCompilerRegistry.RequireId(providerId);
        if (!manifest.Capabilities.Contains("emulator.provide", StringComparer.Ordinal) ||
            manifest.Managed?.Emulators?.Contains(providerId, StringComparer.Ordinal) != true)
            throw new UnauthorizedAccessException("emulator_provider_not_declared");
        var registration = registry.Register(scope, providerId, new Provider(consumption, provider));
        await scope.OwnAsync(registration).ConfigureAwait(false); source.RequireBeforeCallback();
    }

    private sealed class Provider(InstalledExtensionConsumption consumption, IEmulatorProvider inner) : IEmulatorProvider
    {
        public IReadOnlyList<EmulatorLayout> Layouts { get { using var use = consumption.Begin(); var result = inner.Layouts.ToArray(); use.RequireCurrent(); return result; } }
        public EmulatorTargetDescription Describe(EmulatorTarget target) { using var use = consumption.Begin(); var result = inner.Describe(target); use.RequireCurrent(); return result; }
        public async ValueTask<EmulatorDiscoveryResult> DiscoverAsync(IEmulatorInventory inventory, bool supportsAndroid, CancellationToken cancellationToken)
        {
            using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, consumption.CallbackLifetime);
            var result = await inner.DiscoverAsync(inventory, supportsAndroid, lifetime.Token).ConfigureAwait(false);
            use.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested(); return result;
        }
        public EmulatorTarget Select(IReadOnlyList<EmulatorSource> sources, string targetId, string endpoint, bool supportsAndroid)
        {
            using var use = consumption.Begin();
            var result = inner.Select(sources, targetId, endpoint, supportsAndroid); use.RequireCurrent(); return result;
        }
        public IEmulatorSession Open(IEmulatorHost host, EmulatorTarget target)
        {
            using var use = consumption.Begin(); var result = inner.Open(host, target); use.RequireCurrent();
            return new Session(consumption, result);
        }
    }

    private sealed class Session(InstalledExtensionConsumption consumption, IEmulatorSession inner) : IEmulatorSession
    {
        public ValueTask<EmulatorAdbSession> StartAsync(CancellationToken cancellationToken) => CallAsync(inner.StartAsync, cancellationToken);
        public ValueTask<EmulatorCapture> CaptureAsync(CancellationToken cancellationToken) => CallAsync(inner.CaptureAsync, cancellationToken);
        public async ValueTask CloseAsync(bool explicitSelection, CancellationToken cancellationToken) =>
            _ = await CallAsync(async token => { await inner.CloseAsync(explicitSelection, token).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false);
        public async ValueTask CancelAsync(CancellationToken cancellationToken) =>
            _ = await CallAsync(async token => { await inner.CancelAsync(token).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false);
        private async ValueTask<T> CallAsync<T>(Func<CancellationToken, ValueTask<T>> callback, CancellationToken cancellation)
        {
            using var use = await consumption.BeginAsync(cancellation).ConfigureAwait(false);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, consumption.CallbackLifetime);
            var result = await callback(lifetime.Token).ConfigureAwait(false); use.RequireCurrent(); cancellation.ThrowIfCancellationRequested(); return result;
        }
    }
}

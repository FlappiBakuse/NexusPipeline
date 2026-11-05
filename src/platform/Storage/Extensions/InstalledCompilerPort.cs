using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Extensibility.Compilation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledCompilerPort(PreparedExtensionActivation source, InstalledExtensionConsumption consumption,
    ExtensionScope scope, ProjectCompilerRegistry registry, ExtensionManifest manifest) : ICompilerRegistration
{
    public async ValueTask RegisterAsync(string compilerId, IProjectCompiler compiler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(compiler); cancellationToken.ThrowIfCancellationRequested(); source.RequireBeforeCallback();
        ProjectCompilerRegistry.RequireId(compilerId);
        if (!manifest.Capabilities.Contains("source.compile", StringComparer.Ordinal) ||
            manifest.Managed?.Compilers?.Contains(compilerId, StringComparer.Ordinal) != true ||
            manifest.Permissions.FileAccess?.Contains("selected-script-root", StringComparer.Ordinal) != true)
            throw new UnauthorizedAccessException("compiler_not_declared");
        var registration = registry.Register(scope, compilerId, new Compiler(consumption, compiler));
        await scope.OwnAsync(registration).ConfigureAwait(false); source.RequireBeforeCallback();
    }

    private sealed class Compiler(InstalledExtensionConsumption consumption, IProjectCompiler inner) : IProjectCompiler
    {
        public async ValueTask<JsonElement> DiscoverAsync(ISelectedSource source, JsonElement selection, CancellationToken cancellationToken)
        {
            using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, consumption.CallbackLifetime);
            var result = await inner.DiscoverAsync(source, selection, lifetime.Token).ConfigureAwait(false);
            use.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested(); return result.Clone();
        }

        public async ValueTask<CompilationResult> CompileAsync(ISelectedSource source, JsonElement selection, CancellationToken cancellationToken)
        {
            using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, consumption.CallbackLifetime);
            var result = await inner.CompileAsync(source, selection, lifetime.Token).ConfigureAwait(false);
            use.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested(); return result;
        }
    }
}

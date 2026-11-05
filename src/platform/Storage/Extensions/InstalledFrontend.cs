using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Extensibility.Assets;
using NexusPipeline.Extensibility.Frontend;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledFrontend
{
    private readonly ExtensionManifest _manifest;
    private readonly IExtensionPackageAssets _assets;
    private readonly InstalledExtensionConsumption _consumption;
    private readonly ExtensionScope _scope;
    private readonly IExtensionFrontend? _handler;
    private readonly NexusPipeline.Extensibility.Lifecycle.IExtensionActivation _readiness;
    private readonly FrontendOperation[] _operations;

    internal InstalledFrontend(ExtensionManifest manifest, IExtensionPackageAssets assets,
        InstalledExtensionConsumption consumption, ExtensionScope scope, IExtensionFrontend? handler,
        NexusPipeline.Extensibility.Lifecycle.IExtensionActivation readiness)
    {
        if (manifest.Frontend is null || !manifest.Capabilities.Contains("ui.contribute"))
            throw new IOException("extension_frontend_undeclared");
        _manifest = manifest; _assets = assets; _consumption = consumption; _scope = scope; _handler = handler;
        _readiness = readiness;
        _operations = handler?.Operations.ToArray() ?? [];
        if (_operations.Length > 64 || _operations.Any(item => !ValidName(item.Name) || !Enum.IsDefined(item.Access)) ||
            _operations.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != _operations.Length)
            throw new IOException("extension_frontend_operations_invalid");
    }

    internal async ValueTask<JsonElement> ReadManifestAsync(CancellationToken cancellation)
    {
        await _readiness.WaitReadyAsync(cancellation).ConfigureAwait(false);
        using var use = await _consumption.BeginAsync(cancellation).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(_manifest);
    }

    internal async ValueTask<T> UseProjectAsync<T>(Func<Action, CancellationToken, ValueTask<T>> execute,
        Action requireCaller, CancellationToken cancellation, CancellationToken clientLifetime)
    {
        await _readiness.WaitReadyAsync(cancellation).ConfigureAwait(false);
        using var use = await _consumption.BeginAsync(cancellation).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _scope.Cancellation, clientLifetime);
        void Current() { linked.Token.ThrowIfCancellationRequested(); requireCaller(); use.RequireCurrent(); }
        Current(); var result = await execute(Current, linked.Token).ConfigureAwait(false); Current(); return result;
    }

    internal async ValueTask<ControlCapability> CapabilityAsync(string operation, CancellationToken cancellation)
    {
        await _readiness.WaitReadyAsync(cancellation).ConfigureAwait(false);
        // Authorization uses the activated handler's frozen declaration; each invocation owns fresh installation evidence.
        using var use = _scope.BeginUse();
        return Access(operation) switch
        {
            FrontendAccess.Read => ControlCapability.SettingsRead,
            FrontendAccess.Write => ControlCapability.SettingsWrite,
            FrontendAccess.RevealSecret => ControlCapability.SecretRead,
            FrontendAccess.Desktop => ControlCapability.Desktop,
            _ => throw new IOException("extension_frontend_access_invalid")
        };
    }

    private FrontendAccess Access(string operation)
    {
        return _operations.SingleOrDefault(item => item.Name == operation)?.Access
            ?? throw new IOException("extension_frontend_operation_unavailable");
    }

    internal async ValueTask<byte[]> ReadAssetAsync(string asset, CancellationToken cancellation)
    {
        await _readiness.WaitReadyAsync(cancellation).ConfigureAwait(false);
        using var use = await _consumption.BeginAsync(cancellation).ConfigureAwait(false);
        var path = asset == "entry" ? _manifest.Frontend!.Entry :
            int.TryParse(asset, out var index) && index >= 0 && index < _manifest.Frontend!.Styles.Count ?
                _manifest.Frontend.Styles[index] : throw new IOException("extension_frontend_asset_unavailable");
        var maximum = asset == "entry" ? 2 * 1024 * 1024 : 128 * 1024;
        await using var lease = await _assets.AcquireAsync(cancellation).ConfigureAwait(false);
        var file = lease.File(path);
        if (file.SizeBytes is < 1 || file.SizeBytes > maximum) throw new IOException("extension_frontend_asset_limit");
        using var input = lease.OpenRead(path);
        var bytes = new byte[checked((int)file.SizeBytes)];
        await input.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
        use.RequireCurrent(); return bytes;
    }

    internal async ValueTask<FrontendContent> InvokeAsync(string operation, Guid? operationId, string subjectId,
        bool isLocal, ReadOnlyMemory<byte> payload, Action requireCaller, CancellationToken cancellation,
        string contentType = "application/json", string? fileName = null, Guid? clientId = null, CancellationToken clientLifetime = default)
    {
        await _readiness.WaitReadyAsync(cancellation).ConfigureAwait(false);
        using var use = await _consumption.BeginAsync(cancellation).ConfigureAwait(false);
        var call = new FrontendCall(operation, operationId, subjectId, isLocal, payload)
        { ContentType = contentType, FileName = fileName, ClientId = clientId, ClientLifetime = clientLifetime };
        var access = Access(call.Operation);
        var upload = call.ContentType != "application/json";
        if (upload && (access != FrontendAccess.Write ||
            call.ContentType is not ("image/png" or "image/jpeg" or "image/webp") ||
            fileName is not { Length: >= 1 and <= 256 } || fileName.Any(value => char.IsControl(value) || value is '/' or '\\')))
            throw new IOException("extension_frontend_upload_invalid");
        if (_handler is null || call.Payload.Length > (upload ? 8388608 : 1048576) ||
            access == FrontendAccess.Read && call.OperationId is not null)
            throw new IOException("extension_frontend_request_invalid");
        if (access != FrontendAccess.Read && (call.OperationId is null || call.OperationId == Guid.Empty))
            throw new IOException("extension_frontend_operation_identity_required");
        requireCaller();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _scope.Cancellation, clientLifetime);
        var result = await _consumption.InvokeAcceptedAsync(use.Root, requireCaller,
            () => _handler.InvokeAsync(call, linked.Token), subjectId, clientId, operationId, invocationCancellation: linked.Token,
            isDesktopCall: access == FrontendAccess.Desktop, acceptedUse: use).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested(); requireCaller(); use.RequireCurrent();
        if (result is null || result.Content.Length > 8 * 1024 * 1024 ||
            result.ContentType is not ("application/json" or "image/png" or "image/jpeg" or "image/webp"))
            throw new IOException("extension_frontend_response_invalid");
        if (result.ContentType == "application/json")
        {
            using var json = StrictJson.Parse(result.Content);
        }
        return new(result.ContentType, result.Content.ToArray());
    }

    internal async ValueTask<FrontendContent> ReadImageAsync(string operation, string assetId, string subjectId,
        bool isLocal, Action requireCaller, CancellationToken cancellation)
    {
        await _readiness.WaitReadyAsync(cancellation).ConfigureAwait(false);
        using var use = await _consumption.BeginAsync(cancellation).ConfigureAwait(false);
        if (Access(operation) != FrontendAccess.Read || assetId is not { Length: >= 1 and <= 128 } ||
            assetId.Any(value => !char.IsAsciiLetterOrDigit(value) && value != '-'))
            throw new IOException("extension_frontend_image_request_invalid");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { assetId });
        var result = await InvokeAsync(operation, null, subjectId, isLocal, payload, requireCaller, cancellation).ConfigureAwait(false);
        if (result.ContentType == "application/json") throw new IOException("extension_frontend_image_response_invalid");
        return result;
    }

    private static bool ValidName(string name) => name is { Length: >= 1 and <= 80 } &&
        name[0] is >= 'a' and <= 'z' && name[^1] != '-' && !name.Contains("--", StringComparison.Ordinal) &&
        name.All(value => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}

internal sealed record FrontendContent(string ContentType, ReadOnlyMemory<byte> Content);

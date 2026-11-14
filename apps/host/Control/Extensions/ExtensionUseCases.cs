using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Host.Control.Http;

namespace NexusPipeline.Host.Control.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionUseCases(IExtensionPackages packages, NexusPipeline.Platform.Storage.Extensions.ExtensionRuntimeLifecycle runtime, IExtensionOperationQueries operations,
    CancellationToken hostStopping)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) } };
    internal async Task<object> ReadAsync(AuthenticatedControlRequest request)
    {
        request.Require(ControlCapability.ExtensionsRead);
        await runtime.ReconcileFilesAsync(hostStopping);
        request.Require(ControlCapability.ExtensionsRead);
        return new { packages.HasReleaseTrust, items = runtime.ReadStates() };
    }
    internal ExtensionCatalogView? Catalog(AuthenticatedControlRequest request, ExtensionStoreChannel channel)
    { request.Require(ControlCapability.ExtensionsRead); return packages.ReadCached(channel); }
    internal async Task<ExtensionCatalogDetails?> CatalogDetailsAsync(AuthenticatedControlRequest request,
        ExtensionCatalogIdentity identity, string extensionId, CancellationToken cancellation)
    {
        request.Require(ControlCapability.ExtensionsRead);
        var details = await packages.CatalogDetailsAsync(identity, extensionId, cancellation);
        request.Require(ControlCapability.ExtensionsRead);
        return details;
    }
    internal Task<ExtensionCatalogView> RefreshAsync(AuthenticatedControlRequest request, ExtensionStoreChannel channel)
    { request.Require(ControlCapability.ExtensionsWrite); return packages.RefreshAsync(request.SubjectId, channel, hostStopping); }
    internal ExtensionApplyState? Operation(AuthenticatedControlRequest request, Guid operationId)
    { request.Require(ControlCapability.OperationsRead); return operations.Read(operationId, request.SubjectId); }
    internal ExtensionOperationPage OperationHistory(AuthenticatedControlRequest request, int offset, int limit)
    { request.Require(ControlCapability.OperationsRead); return operations.List(request.SubjectId, offset, limit); }
    internal Task<ExtensionApplyAccepted> ApplyAsync(AuthenticatedControlRequest request, JsonElement body)
    {
        request.Require(ControlCapability.ExtensionsWrite);
        var command = body.GetProperty("command").Deserialize<ExtensionApplyCommand>(Json)
            ?? throw new ControlRequestException(400, "extension_request_invalid");
        return packages.ApplyAsync(ControlRequestBody.Uuid(body.GetProperty("operationId")), request.SubjectId, command, CancellationToken.None);
    }
    internal Task<ExtensionRuntimeState> ReconcileAsync(AuthenticatedControlRequest request, JsonElement body)
    {
        request.Require(ControlCapability.ExtensionsWrite);
        var selection = body.Deserialize<ExtensionRuntimeSelection>(Json)
            ?? throw new ControlRequestException(400, "extension_request_invalid");
        return runtime.ReconcileAsync(selection, hostStopping);
    }
}

using System.Net;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.ControlPlane.Http;

internal sealed record AccessDecision(bool Allowed, string? DenyReason);

internal static class RequestAccessPolicy
{
    internal static PluginClientConnectionKind Connection(WebRequest request)
    {
        IPAddress? address = request.RemoteEndPoint?.Address;
        if (address is null) return PluginClientConnectionKind.Unknown;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address) ? PluginClientConnectionKind.Local : PluginClientConnectionKind.Remote;
    }

    internal static AccessDecision Decide(PluginClientConnectionKind connection, PluginOperationAccess operation)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (operation == PluginOperationAccess.General || connection == PluginClientConnectionKind.Local) return new(true, null);
        return new(false, connection == PluginClientConnectionKind.Unknown ? "client_origin_unverified"
            : operation == PluginOperationAccess.HostFilePicker ? "host_file_picker_requires_local" : "native_config_editor_requires_local");
    }

    internal static AccessDecision Decide(WebRequest request, PluginOperationAccess operation) => Decide(Connection(request), operation);

    internal static string Key(PluginOperationAccess operation) => operation switch
    {
        PluginOperationAccess.General => "general",
        PluginOperationAccess.HostFilePicker => "hostFilePicker",
        PluginOperationAccess.NativeConfigEditor => "nativeConfigEditor",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    internal static object Capabilities(WebRequest request) => new
    {
        schemaVersion = 1,
        connectionKind = Connection(request).ToString().ToLowerInvariant(),
        operations = Enum.GetValues<PluginOperationAccess>().ToDictionary(Key, operation => Decide(request, operation)),
    };

    internal static async Task<bool> RequireAsync(HttpListenerContext context, PluginOperationAccess operation)
    {
        AccessDecision decision = Decide(context.Request, operation);
        if (decision.Allowed) return true;
        await HttpHelper.ErrorAsync(context, "operation_requires_local", 403,
            details: new { operation = Key(operation), denyReason = decision.DenyReason }).ConfigureAwait(false);
        return false;
    }
}

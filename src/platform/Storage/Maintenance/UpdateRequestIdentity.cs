using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal static class UpdateRequestIdentity
{
    internal static OperationIdentity Create(HostUpdateRequest request)
    {
        if (request.OperationId == Guid.Empty || request.Subject is not { Length: >= 1 and <= 256 } || request.Subject.Any(char.IsControl) ||
            request.HostVersion is not { Length: >= 1 and <= 80 } || !Enum.IsDefined(request.Timing) || request.Window is null ||
            !Enum.IsDefined(request.Window.Mode) || request.Window.Route is not { Length: >= 1 and <= 256 } ||
            request.Window.Mode == UpdateWindowMode.Headless && (request.Window.Visible || request.Window.Route != "/") ||
            request.Window.Route[0] != '/' || request.Window.Route.StartsWith("//", StringComparison.Ordinal) ||
            request.Window.Route.Any(c => char.IsControl(c) || c is '\\' or '?' or '#' or '%' or ':')) throw Invalid();
        UpdateFeedPolicy.Require(request.Feed, 1, null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { feed = request.Feed, request.HostVersion, request.Timing, request.Window });
        return new(request.OperationId, request.Subject, Convert.ToHexStringLower(SHA256.HashData(bytes)), "maintenance.update");
    }

    internal static OperationIdentity FromSource(HostUpdateSource source, string subject) =>
        Create(new(source.OperationId, subject, source.Feed.Identity, source.HostVersion, source.Timing, source.Window));

    private static IOException Invalid() => new("maintenance_update_request_invalid");
}

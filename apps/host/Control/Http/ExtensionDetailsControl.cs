using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionDetailsControl(ControlAuthentication authority, ExtensionDetailsReader details)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/extensions/{extensionId}/icon", (HttpContext context, string extensionId) =>
        {
            var caller = authority.Require(context, ControlCapability.ExtensionsRead);
            new ControlQueryValues(context.Request.Query).Validate();
            var image = details.ReadIcon(extensionId);
            caller.Require(ControlCapability.ExtensionsRead);
            return image is { } value ? Results.Bytes(value.Bytes, value.ContentType) : Results.NoContent();
        });
        app.MapGet("/control/v1/extensions/{extensionId}/details", (HttpContext context, string extensionId) =>
        {
            authority.Require(context, ControlCapability.ExtensionsRead);
            new ControlQueryValues(context.Request.Query).Validate();
            var value = details.Read(extensionId);
            return value is null ? ControlErrors.Result(404, "extension_not_installed") : Results.Json(value);
        });
    }
}

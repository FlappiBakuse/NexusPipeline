using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Host.Control.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class ValueUseCases(ValueService values)
{
    internal object Read(AuthenticatedControlRequest request, Guid bindingId)
    { request.Require(ControlCapability.ConfigurationRead); return new { bindingId, resources = values.Read(bindingId) }; }
    internal object Files(AuthenticatedControlRequest request, Guid bindingId, string resourceId, long revision, int afterPosition, int limit)
    {
        request.Require(ControlCapability.ConfigurationRead);
        return values.ReadFiles(bindingId, resourceId, revision, afterPosition, limit);
    }
    internal Stream OpenOriginal(AuthenticatedControlRequest request, Guid bindingId, string resourceId, long revision,
        string relativePath, string sourceSha256)
    {
        request.Require(ControlCapability.ConfigurationRead);
        return values.OpenOriginal(bindingId, resourceId, revision, relativePath, sourceSha256);
    }
    internal object Document(AuthenticatedControlRequest request, Guid bindingId, string resourceId, long revision,
        string relativePath, string sourceSha256, string syntax)
    {
        request.Require(ControlCapability.ConfigurationRead);
        var parsedSyntax = syntax switch
        {
            "json" => ConfigurationSyntax.Json, "jsonWithComments" => ConfigurationSyntax.JsonWithComments,
            "yaml" => ConfigurationSyntax.Yaml, _ => throw new ControlRequestException(400, "configuration_value_request_invalid")
        };
        return values.ReadDocument(bindingId, resourceId, revision, relativePath, sourceSha256, parsedSyntax);
    }
    internal object Execute(AuthenticatedControlRequest request, Guid bindingId, string action, JsonElement body)
    {
        request.Require(ControlCapability.ConfigurationEdit);
        ControlRequestBody.RequireMembers(body, "format", "version", "expectedBindingRevision", "relativePath", "syntax", "patch");
        var syntax = body.GetProperty("syntax").GetString() switch
        {
            "json" => ConfigurationSyntax.Json, "jsonWithComments" => ConfigurationSyntax.JsonWithComments,
            "yaml" => ConfigurationSyntax.Yaml, _ => throw new ControlRequestException(400, "configuration_value_request_invalid")
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body.GetProperty("patch"));
        try
        {
            var revision = body.GetProperty("expectedBindingRevision").GetInt64();
            var path = body.GetProperty("relativePath").GetString()!;
            if (action == "preview") return values.Preview(request.SubjectId, bindingId, revision, path, syntax, bytes);
            if (action != "apply") throw new ControlRequestException(404, "control_not_found");
            var result = values.Apply(request.SubjectId, bindingId, revision, path, syntax, bytes, DateTimeOffset.UtcNow);
            return new { result.OperationId, bindingId, result.Saved.Address.ResourceId, result.Saved.Revision, result.IsNew, result.HasChanges };
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
}

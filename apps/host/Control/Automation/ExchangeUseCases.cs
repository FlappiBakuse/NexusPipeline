using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;

namespace NexusPipeline.Host.Control.Automation;

[SupportedOSPlatform("windows")]
internal sealed class ExchangeUseCases(AutomationDefinitionService definitions, AutomationImportService imports, AutomationBundleCodec codec)
{
    internal object Import(AuthenticatedControlRequest request, string action, JsonElement root)
    {
        request.Require(ControlCapability.AutomationExchange);
        var operation = ControlRequestBody.Uuid(root.GetProperty("operationId"));
        var revision = root.GetProperty("expectedRevision").GetInt64();
        var roots = root.GetProperty("roots").EnumerateArray().ToDictionary(
            value => ControlRequestBody.Uuid(value.GetProperty("id")), value => value.GetProperty("path").GetString()!);
        var input = Encoding.UTF8.GetBytes(root.GetProperty("bundle").GetRawText());
        var files = root.GetProperty("attachments").EnumerateArray().ToDictionary(
            value => ControlRequestBody.Uuid(value.GetProperty("id")), value => value.GetProperty("path").GetString()!);
        if (request.Origin != ControlOrigin.Local && files.Count != 0) throw new UnauthorizedAccessException();
        if (action == "apply")
        {
            var replay = imports.ReadAccepted(operation, request.SubjectId, input, roots, revision, root.GetProperty("previewDigest").GetString()!);
            if (replay is not null) return replay;
        }
        var parsed = codec.Decode(input, roots, revision);
        AutomationUseCases.RequireLocalDeclarations(request, definitions.Read(), parsed.Definitions);
        request.Require(ControlCapability.AutomationExchange);
        if (action == "apply")
            return imports.Apply(operation, request.SubjectId, input, roots, revision,
                root.GetProperty("previewDigest").GetString()!, files, CancellationToken.None);
        var preview = imports.Preview(operation, input, roots, revision);
        return new { preview.OperationId, preview.InputDigest, preview.PreviewDigest, preview.ExpectedRevision,
            preview.CanApply, preview.Paths, preview.Problems };
    }
    internal PreparedAutomationExport Export(AuthenticatedControlRequest request, long revision, Guid? template)
    {
        request.Require(ControlCapability.AutomationExchange);
        if (definitions.Read().Revision != revision) throw new NexusPipeline.Core.Automation.AutomationConflictException();
        var prepared = imports.PrepareExport(DateTimeOffset.UtcNow, template);
        try
        {
            if (definitions.Read().Revision != revision) throw new NexusPipeline.Core.Automation.AutomationConflictException();
            request.Require(ControlCapability.AutomationExchange);
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }
}

using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Host.Control.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class ImportUseCases(ConfigurationImportService service, ContractReader contracts, BindingSourceImport? sources = null)
{
    internal async ValueTask<object> ExecuteAsync(AuthenticatedControlRequest request, JsonElement body, CancellationToken cancellation)
    {
        request.Require(ControlCapability.ConfigurationEdit);
        contracts.RequireParsedSchema(body, "control-configuration-import.schema.json");
        var selection = new ConfigurationImportRequest(ControlRequestBody.Uuid(body.GetProperty("operationId")),
            ControlRequestBody.Uuid(body.GetProperty("bindingId")), body.GetProperty("expectedBindingRevision").GetInt64(),
            body.GetProperty("expectedScriptRevision").GetInt64(), body.GetProperty("resources").EnumerateArray().Select(item =>
                new ConfigurationImportResource(item.GetProperty("resourceId").GetString()!, item.GetProperty("expectedRevision").GetInt64())).ToImmutableArray()) {
                CandidateId = body.TryGetProperty("candidateId", out var candidate) ? candidate.GetString() : null,
                ExpectedInstalledRevision = body.TryGetProperty("expectedInstalledRevision", out var installed) ? installed.GetInt64() : null
            };
        request.Require(ControlCapability.ConfigurationEdit);
        var result = selection.CandidateId is null ? service.Import(request.SubjectId, selection, DateTimeOffset.UtcNow, cancellation) :
            await (sources ?? throw new ConfigurationException("configuration_source_unavailable")).ImportAsync(request.SubjectId, selection,
                () => request.Require(ControlCapability.ConfigurationEdit), cancellation);
        return new { result.OperationId, result.IsNew,
            resources = result.Items.Select(item => new { item.Saved.Address, item.Saved.Revision, item.HasChanges }).ToArray() };
    }
}

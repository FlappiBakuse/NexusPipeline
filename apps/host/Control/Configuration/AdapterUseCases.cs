using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Host.Control.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class AdapterUseCases(ITaskAdapterPort adapters, ContractReader contracts)
{
    internal async Task<object> ExecuteAsync(AuthenticatedControlRequest request, string action, JsonElement body)
    {
        void Current() => request.Require(action is "previewRepair" or "applyRepair" ? ControlCapability.ConfigurationRepair : ControlCapability.ConfigurationRead);
        Current();
        var selection = Selection(body.GetProperty("selection"));
        switch (action)
        {
            case "discover":
                return await adapters.DiscoverAsync(request.SubjectId, selection, CancellationToken.None, Current);
            case "propose":
            {
                var result = await adapters.ProposeConfigurationAsync(request.SubjectId, selection, CancellationToken.None, Current);
                return new { result.Source, result.Diagnostics, result.ProposalDigest,
                    candidates = result.Patches.Select((item, index) => new { index, item.Binding.ResourceId,
                        expectedRevision = item.Binding.Revision, item.Binding.SourceSha256, editCount = item.Edits.Length }).ToArray() };
            }
            case "previewRepair":
                return await adapters.PreviewRepairAsync(request.SubjectId, selection,
                    body.GetProperty("candidateIndex").GetInt32(), body.GetProperty("proposalDigest").GetString()!, CancellationToken.None, Current);
            case "applyRepair":
            {
                var saved = await adapters.ApplyRepairAsync(request.SubjectId, selection,
                    body.GetProperty("candidateIndex").GetInt32(), body.GetProperty("proposalDigest").GetString()!,
                    DateTimeOffset.UtcNow, CancellationToken.None, Current);
                return new { saved.OperationId, saved.Saved.Address.BindingId, saved.Saved.Address.ResourceId,
                    saved.Saved.Revision, saved.HasChanges, saved.IsNew };
            }
            default: throw new ControlRequestException(404, "control_not_found");
        }
    }

    private AdapterSelection Selection(JsonElement value) => new(
        ControlRequestBody.Uuid(value.GetProperty("operationId")), ControlRequestBody.Uuid(value.GetProperty("bindingId")),
        value.GetProperty("expectedBindingRevision").GetInt64(), value.GetProperty("extensionId").GetString()!,
        value.GetProperty("expectedInstalledRevision").GetInt64(), value.GetProperty("resources").EnumerateArray().Select(item =>
            new AdapterResourceSelection(item.GetProperty("resourceId").GetString()!, item.GetProperty("expectedRevision").GetInt64(),
                item.GetProperty("relativePath").GetString()!)).ToImmutableArray(),
        value.TryGetProperty("configurationIntents", out var intents) ? intents.EnumerateArray()
            .Select(item => new ConfigurationPatchReader(contracts).Read(JsonSerializer.SerializeToUtf8Bytes(item))).ToImmutableArray() : []);
}

using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed record BoundConfigurationJournal(FrozenConfigurationExchange Plan, PreparedConfigurationExchange? Prepared, JournalDocument Physical)
{
    private JsonObject? _document;
    internal JsonObject Document => _document ??= JsonNode.Parse(Physical.Export())!.AsObject();
}

internal static class ConfigurationJournalBinding
{
    internal static BoundConfigurationJournal Read(RuntimeInstance instance, JournalDocument physical, ContractReader contracts, StateDatabase database,
        ConfigurationPlanCodec.OpenedPlan? opened = null)
    {
        var document = physical.Root;
        var plan = opened is null ? ReadPlan(instance, physical, contracts) : opened.ReadContents(instance, physical);
        if (plan.JournalId != physical.JournalId || plan.Kind != physical.Kind || plan.OperationId != document.GetProperty("operationId").GetGuid())
            throw new IOException("configuration_journal_plan_unbound");
        var participants = document.GetProperty("participants");
        var count = participants.GetArrayLength();
        if (count > 2 || count > 0 && (participants[0].GetProperty("kind").GetString() != "directory" ||
            !participants[0].GetProperty("identity").TryGetProperty("format", out var workspaceFormat) || workspaceFormat.GetString() != "nexus.configuration.workspace"))
            throw new JsonException("configuration_participant_unknown");
        var prepared = count == 0 ? null : new ConfigurationWorkspaceCodec(contracts)
            .Decode(participants[0].GetProperty("identity"), plan, physical.PlanDigest);
        if (count == 2)
        {
            if (plan.Kind != JournalKind.ConfigEdit && !(plan.Kind == JournalKind.ConfigExchange && plan.RunState is not null && plan.EditPolicy is not null &&
                !plan.PhysicalClaims.IsDefaultOrEmpty) || participants[1].GetProperty("kind").GetString() != "file" ||
                !participants[1].GetProperty("identity").TryGetProperty("format", out var outputFormat) || outputFormat.GetString() != "nexus.configuration.edit-output-reference")
                throw new JsonException("configuration_participant_unknown");
            var codec = new EditOutputCodec(instance, contracts, new(instance, contracts));
            prepared = codec.Read(codec.ReadReference(participants[1].GetProperty("identity")), prepared!, physical.PlanDigest, database);
        }
        foreach (var receipt in document.GetProperty("receipts").EnumerateArray())
        {
            var step = receipt.GetProperty("stepId").GetString();
            var effect = receipt.GetProperty("effect").GetString();
            var known = step switch
            {
                "prepare-workspace" => effect == "completed",
                "seal-output" => effect == "completed" && count == 2,
                "preserve-original" or "install-candidate" => effect is "started" or "completed",
                "retire-candidate" or "restore-original" => effect is "started" or "reverted",
                "verify-no-effect" => effect == "notExecuted",
                "interruption-recovered" => effect == "reverted",
                "isolate-configuration" => effect == "unknown",
                _ => false
            };
            if (!known || receipt.TryGetProperty("outcomes", out var outcomes) && outcomes.ValueKind != JsonValueKind.Null)
                throw new JsonException("configuration_receipt_unknown");
        }
        if (count == 2)
        {
            var receipts = document.GetProperty("receipts").EnumerateArray().ToArray();
            var sealedAt = receipts.Select((item, index) => (item, index)).Where(pair => pair.item.GetProperty("stepId").GetString() == "seal-output").ToArray();
            if (sealedAt.Length != 1 || !receipts.Take(sealedAt[0].index).Any(item => item.GetProperty("stepId").GetString() == "install-candidate" &&
                item.GetProperty("effect").GetString() == "completed")) throw new JsonException("configuration_edit_output_receipt_unbound");
        }
        return new(plan, prepared, physical);
    }

    private static FrozenConfigurationExchange ReadPlan(RuntimeInstance instance, JournalDocument physical, ContractReader contracts)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, physical.PlanPath));
        return new ConfigurationPlanCodec(instance, contracts, new ImmutableBlobStore(instance, contracts))
            .Read(new(physical.PlanPath, new FileInfo(path).Length, physical.PlanDigest));
    }
}

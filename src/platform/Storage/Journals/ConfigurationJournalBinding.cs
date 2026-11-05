using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed record BoundConfigurationJournal(FrozenConfigurationExchange Plan, PreparedConfigurationExchange? Prepared, JsonObject Document);

internal static class ConfigurationJournalBinding
{
    internal static BoundConfigurationJournal Read(RuntimeInstance instance, JournalDocument physical, ContractReader contracts, StateDatabase database)
    {
        var document = JsonNode.Parse(physical.Export())!.AsObject();
        var planPath = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, physical.PlanPath));
        var plan = new ConfigurationPlanCodec(instance, contracts, new ImmutableBlobStore(instance, contracts))
            .Read(new(physical.PlanPath, new FileInfo(planPath).Length, physical.PlanDigest));
        if (plan.JournalId != physical.JournalId || plan.Kind != physical.Kind || plan.OperationId != document["operationId"]!.GetValue<Guid>())
            throw new IOException("configuration_journal_plan_unbound");
        var participants = document["participants"]!.AsArray();
        if (participants.Count > 2 || participants.Count > 0 && (participants[0]!["kind"]!.GetValue<string>() != "directory" ||
            participants[0]!["identity"]?["format"]?.GetValue<string>() != "nexus.configuration.workspace"))
            throw new JsonException("configuration_participant_unknown");
        var prepared = participants.Count == 0 ? null : new ConfigurationWorkspaceCodec(contracts)
            .Decode(JsonSerializer.SerializeToElement(participants[0]!["identity"]), plan, physical.PlanDigest);
        if (participants.Count == 2)
        {
            if (plan.Kind != JournalKind.ConfigEdit && !(plan.Kind == JournalKind.ConfigExchange && plan.RunState is not null && plan.EditPolicy is not null &&
                !plan.PhysicalClaims.IsDefaultOrEmpty) || participants[1]!["kind"]!.GetValue<string>() != "file" ||
                participants[1]!["identity"]?["format"]?.GetValue<string>() != "nexus.configuration.edit-output-reference")
                throw new JsonException("configuration_participant_unknown");
            var codec = new EditOutputCodec(instance, contracts, new(instance, contracts));
            prepared = codec.Read(codec.ReadReference(JsonSerializer.SerializeToElement(participants[1]!["identity"])), prepared!, physical.PlanDigest, database);
        }
        foreach (var receipt in document["receipts"]!.AsArray())
        {
            var step = receipt!["stepId"]!.GetValue<string>();
            var effect = receipt["effect"]!.GetValue<string>();
            var known = step switch
            {
                "prepare-workspace" => effect == "completed",
                "seal-output" => effect == "completed" && participants.Count == 2,
                "preserve-original" or "install-candidate" => effect is "started" or "completed",
                "retire-candidate" or "restore-original" => effect is "started" or "reverted",
                "verify-no-effect" => effect == "notExecuted",
                "interruption-recovered" => effect == "reverted",
                "isolate-configuration" => effect == "unknown",
                _ => false
            };
            if (!known || receipt["outcomes"] is not null) throw new JsonException("configuration_receipt_unknown");
        }
        if (participants.Count == 2)
        {
            var receipts = document["receipts"]!.AsArray();
            var sealedAt = receipts.Select((item, index) => (item, index)).Where(pair => pair.item!["stepId"]!.GetValue<string>() == "seal-output").ToArray();
            if (sealedAt.Length != 1 || !receipts.Take(sealedAt[0].index).Any(item => item!["stepId"]!.GetValue<string>() == "install-candidate" &&
                item["effect"]!.GetValue<string>() == "completed")) throw new JsonException("configuration_edit_output_receipt_unbound");
        }
        return new(plan, prepared, document);
    }
}

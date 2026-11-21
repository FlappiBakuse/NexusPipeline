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
    private const string OutputFormat = "nexus.configuration.edit-output-reference";

    internal static BoundConfigurationJournal Read(RuntimeInstance instance, JournalDocument physical, ContractReader contracts, StateDatabase database,
        ConfigurationPlanCodec.OpenedPlan? opened = null)
        => ReadCore(instance, physical, contracts, database, opened, verifyContent: true, verifyOutputContent: true);

    internal static BoundConfigurationJournal ReadForRestoration(RuntimeInstance instance, JournalDocument physical, ContractReader contracts,
        StateDatabase database, ConfigurationPlanCodec.OpenedPlan? opened = null, bool verifyOutputContent = false)
        => ReadCore(instance, physical, contracts, database, opened, verifyContent: false, verifyOutputContent: verifyOutputContent);

    internal static bool HasSealedOutput(JournalDocument physical) => HasSealedOutput(physical.Root.GetProperty("participants"));

    private static BoundConfigurationJournal ReadCore(RuntimeInstance instance, JournalDocument physical, ContractReader contracts, StateDatabase database,
        ConfigurationPlanCodec.OpenedPlan? opened, bool verifyContent, bool verifyOutputContent)
    {
        var document = physical.Root;
        var plan = opened is null ? ReadPlan(instance, physical, contracts, verifyContent) : verifyContent ?
            opened.ReadContents(instance, physical) : opened.ReadStructure(instance, physical);
        if (plan.JournalId != physical.JournalId || plan.Kind != physical.Kind || plan.OperationId != document.GetProperty("operationId").GetGuid())
            throw new IOException("configuration_journal_plan_unbound");
        var participants = document.GetProperty("participants");
        var count = participants.GetArrayLength();
        var hasOutput = HasSealedOutput(participants);
        var proofIndex = hasOutput ? 2 : 1;
        var hasProof = count == proofIndex + 1 && HasFormat(participants[proofIndex], ConfigurationRestorationProof.Format);
        if (count > 1 + (hasOutput ? 1 : 0) + (hasProof ? 1 : 0) || count > 0 &&
            (participants[0].GetProperty("kind").GetString() != "directory" || !HasFormat(participants[0], "nexus.configuration.workspace")))
            throw new JsonException("configuration_participant_unknown");
        var prepared = count == 0 ? null : new ConfigurationWorkspaceCodec(contracts)
            .Decode(participants[0].GetProperty("identity"), plan, physical.PlanDigest, requireLivePath: verifyContent);
        if (hasOutput)
        {
            if (plan.Kind != JournalKind.ConfigEdit && !(plan.Kind == JournalKind.ConfigExchange && plan.RunState is not null && plan.EditPolicy is not null &&
                !plan.PhysicalClaims.IsDefaultOrEmpty) || participants[1].GetProperty("kind").GetString() != "file")
                throw new JsonException("configuration_participant_unknown");
            var codec = new EditOutputCodec(instance, contracts, new(instance, contracts));
            prepared = codec.Read(codec.ReadReference(participants[1].GetProperty("identity")), prepared!, physical.PlanDigest, database, verifyOutputContent);
        }
        if (hasProof)
            prepared = prepared! with { Reconstruction = ConfigurationRestorationProof.Read(participants[proofIndex], participants[0], plan, physical) };
        if (ValidateReceipts(document.GetProperty("receipts"), hasOutput, hasProof))
        {
            if (prepared is null || plan.Before.Snapshot.Shape == FileObjectKind.Missing ||
                physical.Phase is not (JournalPhase.Reverting or JournalPhase.Reverted or JournalPhase.Committed or JournalPhase.Quarantined))
                throw new JsonException("configuration_reconstruction_intent_unbound");
            prepared = prepared with { ReconstructionStarted = true };
        }
        return new(plan, prepared, physical);
    }

    private static bool ValidateReceipts(JsonElement receipts, bool hasOutput, bool hasProof)
    {
        var installed = false;
        var sealedOutput = false;
        var retiring = false;
        var reconstructionStarted = false;
        var reconstructed = false;
        var restored = false;
        foreach (var receipt in receipts.EnumerateArray())
        {
            var step = receipt.GetProperty("stepId").GetString();
            var effect = receipt.GetProperty("effect").GetString();
            var known = step switch
            {
                "prepare-workspace" => effect == "completed",
                "seal-output" => effect == "completed" && hasOutput && installed && !sealedOutput,
                ConfigurationRestorationProof.Step => retiring && !restored && (effect switch
                {
                    "started" => !reconstructionStarted && receipt.GetProperty("detailCode").GetString() == ConfigurationRestorationProof.Intent,
                    "completed" => reconstructionStarted && hasProof && !reconstructed,
                    _ => false
                }),
                "preserve-original" or "install-candidate" => effect is "started" or "completed",
                "retire-candidate" or "restore-original" => effect is "started" or "reverted",
                "verify-no-effect" => effect == "notExecuted",
                "interruption-recovered" => effect == "reverted",
                "isolate-configuration" => effect == "unknown",
                _ => false
            };
            if (!known || receipt.TryGetProperty("outcomes", out var outcomes) && outcomes.ValueKind != JsonValueKind.Null)
                throw new JsonException("configuration_receipt_unknown");
            installed |= step == "install-candidate" && effect == "completed";
            sealedOutput |= step == "seal-output";
            retiring |= step == "retire-candidate" && effect == "started";
            reconstructionStarted |= step == ConfigurationRestorationProof.Step && effect == "started";
            reconstructed |= step == ConfigurationRestorationProof.Step && effect == "completed";
            restored |= step == "restore-original" && effect == "reverted";
        }
        if (hasOutput != sealedOutput) throw new JsonException("configuration_edit_output_receipt_unbound");
        if (hasProof != reconstructed) throw new JsonException("configuration_reconstruction_receipt_unbound");
        return reconstructionStarted;
    }

    private static bool HasSealedOutput(JsonElement participants) =>
        participants.GetArrayLength() > 1 && HasFormat(participants[1], OutputFormat);

    private static bool HasFormat(JsonElement participant, string expected) =>
        participant.GetProperty("identity").TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String && format.GetString() == expected;

    private static FrozenConfigurationExchange ReadPlan(RuntimeInstance instance, JournalDocument physical, ContractReader contracts, bool verifyContent)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, physical.PlanPath));
        var reference = new BlobReference(physical.PlanPath, new FileInfo(path).Length, physical.PlanDigest);
        var codec = new ConfigurationPlanCodec(instance, contracts, new ImmutableBlobStore(instance, contracts));
        return verifyContent ? codec.Read(reference) : codec.ReadStructure(reference);
    }
}

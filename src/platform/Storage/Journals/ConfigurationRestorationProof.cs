using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed record ConfigurationRestorationProof(string ObjectDigest)
{
    internal const string Format = "nexus.configuration.restoration";
    internal const string Step = "prepare-reconstruction";
    internal const string Intent = "configuration_reconstruction_started";

    internal static string Digest(IReadOnlyList<PhysicalTreeObject> objects) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(objects)));

    internal JsonObject Participant(Guid journalId, string planDigest, string resourceKey) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "file", ["resourceKey"] = resourceKey,
        ["identity"] = new JsonObject
        {
            ["format"] = Format, ["version"] = 1, ["journalId"] = journalId.ToString("D"),
            ["planDigest"] = planDigest, ["objectDigest"] = ObjectDigest
        }
    };

    internal static ConfigurationRestorationProof Read(JsonElement participant, JsonElement workspace, FrozenConfigurationExchange plan, JournalDocument physical)
    {
        var identity = participant.GetProperty("identity");
        if (physical.Phase is not (JournalPhase.Reverting or JournalPhase.Reverted or JournalPhase.Committed or JournalPhase.Quarantined) ||
            participant.GetProperty("kind").GetString() != "file" ||
            participant.GetProperty("resourceKey").GetString() != workspace.GetProperty("resourceKey").GetString() ||
            plan.Before.Snapshot.Shape == FileObjectKind.Missing || identity.EnumerateObject().Count() != 5 ||
            !identity.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var formatVersion) || formatVersion != 1 ||
            !HasString(identity, "journalId", plan.JournalId.ToString("D")) || !HasString(identity, "planDigest", physical.PlanDigest) ||
            !identity.TryGetProperty("objectDigest", out var digest) || digest.ValueKind != JsonValueKind.String ||
            digest.GetString() is not { Length: 64 } value || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new JsonException("configuration_reconstruction_proof_unbound");
        return new(value);
    }

    private static bool HasString(JsonElement value, string name, string expected) =>
        value.TryGetProperty(name, out var actual) && actual.ValueKind == JsonValueKind.String && actual.GetString() == expected;
}

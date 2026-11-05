using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record MaintenanceHelperChallenge(string Format, int Version, string Kind,
    MaintenanceStartupBinding Binding, Guid WriterId, string JobName, string Nonce, long JobHandle);
internal sealed record MaintenanceHelperReady(string Format, int Version, string Kind,
    MaintenanceStartupBinding Binding, Guid WriterId, string Nonce);

internal static class MaintenanceHelperFrames
{
    private const string Format = "nexus.maintenance.helper-ready";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal static MaintenanceHelperChallenge Challenge(MaintenanceHandoffInputs inputs, Guid writerId, string jobName, long jobHandle) =>
        new(Format, 1, "challenge", Binding(inputs), writerId, jobName, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), jobHandle);
    internal static MaintenanceHelperReady Ready(MaintenanceHelperChallenge challenge) =>
        new(Format, 1, "ready", challenge.Binding, challenge.WriterId, challenge.Nonce);
    internal static MaintenanceStartupBinding Binding(MaintenanceHandoffInputs inputs) =>
        new(inputs.Plan.InstanceId, inputs.Plan.OperationId, inputs.Plan.JournalId, inputs.Operation.HandoffId!.Value,
            inputs.Operation.Plan, inputs.Operation.Source);

    internal static byte[] Encode(MaintenanceHelperChallenge value) { Require(value); return BoundedJson.Serialize(value, Options); }
    internal static byte[] Encode(MaintenanceHelperReady value) { Require(value); return BoundedJson.Serialize(value, Options); }
    internal static MaintenanceHelperChallenge ReadChallenge(byte[] bytes)
    { var value = Read<MaintenanceHelperChallenge>(bytes); Require(value); return value; }
    internal static MaintenanceHelperReady ReadReady(byte[] bytes)
    { var value = Read<MaintenanceHelperReady>(bytes); Require(value); return value; }

    private static T Read<T>(byte[] bytes) where T : class
    {
        if (bytes.Length > MaintenanceStartupFrames.MaximumBytes) throw Invalid();
        using var document = StrictJson.Parse(bytes);
        var value = JsonSerializer.Deserialize<T>(bytes, Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(document.RootElement, JsonSerializer.SerializeToElement(value, Options))) throw Invalid();
        return value;
    }
    private static void Require(MaintenanceHelperChallenge value)
    {
        if (value.Format != Format || value.Version != 1 || value.Kind != "challenge" || value.WriterId == Guid.Empty || !Hex(value.Nonce) || value.JobHandle <= 0 ||
            value.JobName is not { Length: 50 } || !value.JobName.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(value.JobName[18..], "N", out _)) throw Invalid();
        MaintenanceStartupFrames.Require(value.Binding);
    }
    private static void Require(MaintenanceHelperReady value)
    {
        if (value.Format != Format || value.Version != 1 || value.Kind != "ready" || value.WriterId == Guid.Empty || !Hex(value.Nonce)) throw Invalid();
        MaintenanceStartupFrames.Require(value.Binding);
    }
    private static bool Hex(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static IOException Invalid() => new("maintenance_helper_ready_frame_invalid");
}

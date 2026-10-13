using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NexusPipeline.Shared.Results;

namespace NexusPipeline.Modules.Scripts.Contracts;

internal sealed record ProviderConfigurationSaveRequest(string OperationId, ScriptInstance Script, JsonObject Configuration)
{
    public string UserId { get; init; } = "";
    public string ExpectedUserRevision { get; init; } = "";
}
internal sealed record ProviderConfigurationOperationStatus(string OperationId, string ScriptId, string State,
    string Revision, string ErrorCode, bool CleanupConfirmed, JsonObject Progress)
{
    public string UserId { get; init; } = "";
}

internal interface IProviderConfigurationOperations
{
    ProviderConfigurationOperationStatus? Find(string id);
    IReadOnlyList<ProviderConfigurationOperationStatus> Snapshot();
    ProviderConfigurationOperationStatus Start(ProviderConfigurationSaveRequest request, Func<ScriptInstance, string, OperationResult<ScriptInstance>> commit);
    bool Cancel(string id);
}

internal static class ProviderConfigurationContract
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32, WriteIndented = true };
    internal static string Fingerprint(ScriptInstance? script) => script is null ? ""
        : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(script))).ToLowerInvariant();
    internal static bool SafeId(string? value) => value is { Length: >= 1 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class CompletionActionCodec
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static string Encode<T>(T value) => Encoding.UTF8.GetString(BoundedJson.Serialize(value, Options));

    internal static T Decode<T>(string value)
    {
        using var document = StrictJson.Parse(Encoding.UTF8.GetBytes(value));
        return document.RootElement.Deserialize<T>(Options) ?? throw Invalid();
    }

    internal static OperationIdentity Identity(Guid operationId, string subject, CompletionActionGroup group)
        => new(operationId, subject, Digest(Encode(group)), "execution.completion");

    internal static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static void RequireTransition(CompletionActionSnapshot previous, CompletionActionSnapshot next)
    {
        if (next.OperationId != previous.OperationId || next.SubjectId != previous.SubjectId ||
            next.AcceptedAtUtc != previous.AcceptedAtUtc || Encode(next.Frozen) != Encode(previous.Frozen) ||
            !Enum.IsDefined(next.Phase) || next.ReasonCode is { } reason && !Reason(reason) ||
            next.NotBeforeUtc.Offset != TimeSpan.Zero || next.NotBeforeUtc < previous.NotBeforeUtc)
            throw Invalid();
        var valid = (previous.Phase, next.Phase) switch
        {
            (CompletionEffectPhase.Prepared, CompletionEffectPhase.Countdown or CompletionEffectPhase.Cancelled or CompletionEffectPhase.Failed) => true,
            (CompletionEffectPhase.Countdown, CompletionEffectPhase.Entered or CompletionEffectPhase.Cancelled or CompletionEffectPhase.Failed) => true,
            (CompletionEffectPhase.Entered, CompletionEffectPhase.Accepted or CompletionEffectPhase.Failed or CompletionEffectPhase.Unknown) => true,
            _ => false
        };
        if (!valid || next.Phase != CompletionEffectPhase.Countdown && next.NotBeforeUtc != previous.NotBeforeUtc)
            throw Invalid();
        if (next.Phase == CompletionEffectPhase.Countdown && next.NotBeforeUtc != next.AcceptedAtUtc.AddSeconds(
            next.Frozen.Action == AutomationCompletionAction.Exit ? 0 : 60)) throw Invalid();
    }

    internal static void RequireInitial(CompletionActionSnapshot value)
    {
        if (value.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(value.SubjectId) || value.SubjectId.Length > 256 ||
            value.Phase != CompletionEffectPhase.Prepared || value.AcceptedAtUtc.Offset != TimeSpan.Zero ||
            value.AcceptedAtUtc <= DateTimeOffset.UnixEpoch || value.NotBeforeUtc != value.AcceptedAtUtc || value.ReasonCode is not null ||
            value.Frozen.Action == AutomationCompletionAction.None)
            throw Invalid();
        if (Encode(CompletionActionPolicy.Combine(value.Frozen.Intents)) != Encode(value.Frozen)) throw Invalid();
    }

    private static bool Reason(string value) => value.Length is >= 1 and <= 80 &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    internal static IOException Invalid() => new("completion_metadata_unbound");
}

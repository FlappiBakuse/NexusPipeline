using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Settings;

internal sealed record StartupIntentDocument(string Format, int Version, Guid InstanceId, Guid OperationId,
    long ExpectedRevision, bool Enable, string? BeforeRegistrationDigest, StartupRegistrationCommand Command, ProcessIdentity Creator);
internal sealed record StartupRegistrationRecord(string Format, int Version, StartupRegistrationCommand Command);

internal static class StartupIntentCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static byte[] Encode(StartupIntentDocument value)
    {
        Validate(value);
        return Serialize(value);
    }

    internal static StartupIntentDocument Decode(byte[] bytes, string digest)
    {
        if (Hash(bytes) != digest) throw new IOException("startup_intent_digest_changed");
        try
        {
            var value = Decode<StartupIntentDocument>(bytes);
            Validate(value);
            return value;
        }
        catch (Exception error) when (error is HostSettingsException or ArgumentException or JsonException)
        { throw new IOException("startup_intent_document_unknown", error); }
    }

    internal static byte[] EncodeCommand(StartupRegistrationCommand command)
    {
        StartupRegistrationPolicy.Validate(command);
        return Serialize(new StartupRegistrationRecord("nexus.startup.registration", 1, command));
    }

    internal static StartupRegistrationCommand DecodeCommand(byte[] bytes)
    {
        try
        {
            var value = Decode<StartupRegistrationRecord>(bytes);
            if (value.Format != "nexus.startup.registration" || value.Version != 1) throw new IOException("startup_registration_format_unknown");
            StartupRegistrationPolicy.Validate(value.Command);
            return value.Command;
        }
        catch (Exception error) when (error is HostSettingsException or ArgumentException or JsonException)
        { throw new IOException("startup_registration_record_unknown", error); }
    }

    internal static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private static T Decode<T>(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 262144) throw new IOException("startup_intent_size_invalid");
        using var document = StrictJson.Parse(bytes);
        var value = document.Deserialize<T>(Options) ?? throw new IOException("startup_intent_null");
        if (!Serialize(value).AsSpan().SequenceEqual(bytes)) throw new IOException("startup_intent_encoding_unknown");
        return value;
    }

    private static byte[] Serialize<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length is < 1 or > 262144) throw new IOException("startup_intent_size_invalid");
        return bytes;
    }

    private static void Validate(StartupIntentDocument value)
    {
        if (value is null || value.Format != "nexus.startup.intent" || value.Version != 1 || value.InstanceId == Guid.Empty ||
            value.OperationId == Guid.Empty || value.ExpectedRevision is < 0 or >= 9007199254740991 ||
            value.BeforeRegistrationDigest is { } digest && (digest.Length != 64 || digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new IOException("startup_intent_identity_invalid");
        StartupRegistrationPolicy.Validate(value.Command);
        if (value.Command.Definition.InstanceId != value.InstanceId || value.Creator is not { ProcessId: > 0 } creator ||
            creator.CreatedAtUtc.Kind != DateTimeKind.Utc || creator.CreatedAtUtc <= DateTime.UnixEpoch ||
            !Path.IsPathFullyQualified(creator.ImagePath) || Path.GetFullPath(creator.ImagePath) != creator.ImagePath ||
            creator.ImageSha256 is not { Length: 64 } hash || hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            creator.UserSid != value.Command.Definition.UserSid)
            throw new IOException("startup_intent_creator_unbound");
    }
}

internal sealed class StartupIntentDraft(StartupIntentDocument document)
{
    private readonly byte[] _bytes = StartupIntentCodec.Encode(document);
    internal StartupIntentDocument Read() => StartupIntentCodec.Decode(_bytes.ToArray(), StartupIntentCodec.Hash(_bytes));
    internal byte[] Export() => _bytes.ToArray();
}

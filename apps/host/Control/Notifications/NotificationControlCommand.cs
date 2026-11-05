using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Notifications;

namespace NexusPipeline.Host.Control.Notifications;

internal sealed class NotificationControlCommand : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    internal string Action { get; private init; } = "";
    internal Guid OperationId { get; private init; }
    internal long CatalogRevision { get; private init; }
    internal Guid TaskId { get; private init; }
    internal long TaskRevision { get; private init; }
    internal NotificationTaskInput? Input { get; private init; }
    internal Guid[] Order { get; private init; } = [];
    internal int Maximum { get; private init; }
    internal List<NotificationSecretEdit> Secrets { get; } = [];

    internal static NotificationControlCommand Read(JsonElement root)
    {
        try { return ReadCore(root); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw Invalid(); }
    }

    private static NotificationControlCommand ReadCore(JsonElement root)
    {
        var action = Required(root, "action").GetString() ?? throw Invalid();
        string[] specific = action switch
        {
            "save" => new[] { "taskId", "expectedTaskRevision", "input", "secrets" },
            "delete" or "test" => ["taskId", "expectedTaskRevision"],
            "reorder" => ["order"], "limit" => ["maximum"],
            _ => throw Invalid()
        };
        ControlRequestBody.RequireMembers(root, ["format", "version", "action", "operationId",
            "expectedCatalogRevision", .. specific]);
        if (Required(root, "format").GetString() != "nexus.control.notification-command" ||
            Required(root, "version").GetInt32() != 1) throw Invalid();
        var result = new NotificationControlCommand
        {
            Action = action, OperationId = ControlRequestBody.Uuid(Required(root, "operationId")),
            CatalogRevision = Revision(Required(root, "expectedCatalogRevision")),
            TaskId = specific.Contains("taskId") ? ControlRequestBody.Uuid(Required(root, "taskId")) : Guid.Empty,
            TaskRevision = specific.Contains("expectedTaskRevision") ? Revision(Required(root, "expectedTaskRevision")) : 0,
            Input = action == "save" ? InputValue(Required(root, "input")) : null,
            Order = action == "reorder" ? OrderValue(Required(root, "order")) : [],
            Maximum = action == "limit" ? Required(root, "maximum").GetInt32() : 0
        };
        try
        {
            if (action == "save" && root.TryGetProperty("secrets", out var edits)) result.ReadSecrets(edits);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static NotificationTaskInput InputValue(JsonElement input)
    {
        ControlRequestBody.RequireMembers(input, "name", "remark", "kind", "isEnabled", "configuration");
        foreach (var field in new[] { "name", "remark", "kind", "isEnabled", "configuration" }) _ = Required(input, field);
        var config = Required(input, "configuration");
        string[] fields = ["screenshotEnabled", "timeoutSeconds", "webhookKind", "webhookTemplate", "feishuAppId",
            "slackChannelId", "smtpHost", "smtpPort", "smtpSecurity", "smtpUser", "smtpFrom", "smtpTo", "smtpSubjectPrefix"];
        ControlRequestBody.RequireMembers(config, fields);
        foreach (var field in fields) _ = Required(config, field);
        return input.Deserialize<NotificationTaskInput>(Json) ?? throw Invalid();
    }

    private void ReadSecrets(JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 5) throw Invalid();
        var seen = new HashSet<NotificationSecretField>();
        foreach (var item in values.EnumerateArray())
        {
            ControlRequestBody.RequireMembers(item, "field", "expectedRevision", "disposition", "value");
            var field = NotificationSecretFields.Parse(Required(item, "field").GetString() ?? throw Invalid());
            if (!seen.Add(field)) throw Invalid();
            var revision = Revision(Required(item, "expectedRevision"));
            var disposition = Required(item, "disposition").GetString() switch
                { "keep" => SecretDisposition.Keep, "set" => SecretDisposition.Set, "clear" => SecretDisposition.Clear, _ => throw Invalid() };
            var hasValue = item.TryGetProperty("value", out var value);
            if (hasValue != (disposition == SecretDisposition.Set)) throw Invalid();
            Secrets.Add(new(field, revision, disposition, hasValue ? CopySecret(value) : null));
        }
    }

    private static SecretValue CopySecret(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var chars = ArrayPool<char>.Shared.Rent(65537);
        try
        {
            using var encoded = new SecretJsonBuffer();
            using (var writer = new Utf8JsonWriter(encoded)) { value.WriteTo(writer); writer.Flush(); }
            var reader = new Utf8JsonReader(encoded.Written);
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) throw Invalid();
            var count = reader.CopyString(chars);
            if (count is < 1 or > 65536) throw Invalid();
            return new(chars.AsSpan(0, count));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars.AsSpan()));
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private sealed class SecretJsonBuffer : IBufferWriter<byte>, IDisposable
    {
        private const int Capacity = 6 * 65536 + 8192;
        private readonly byte[] _bytes = ArrayPool<byte>.Shared.Rent(Capacity);
        private int _written;
        internal ReadOnlySpan<byte> Written => _bytes.AsSpan(0, _written);
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint < 0 || Math.Max(1, sizeHint) > Capacity - _written) throw Invalid();
            return _bytes.AsMemory(_written, Capacity - _written);
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public void Advance(int count)
        {
            if (count < 0 || count > Capacity - _written) throw Invalid();
            _written += count;
        }
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_bytes);
            ArrayPool<byte>.Shared.Return(_bytes);
        }
    }

    private static Guid[] OrderValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 99) throw Invalid();
        return value.EnumerateArray().Select(ControlRequestBody.Uuid).ToArray();
    }
    private static long Revision(JsonElement value) =>
        value.TryGetInt64(out var revision) && revision is >= 0 and <= NotificationTaskPolicy.MaximumRevision ? revision : throw Invalid();
    private static JsonElement Required(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) ? member : throw Invalid();
    private static ControlRequestException Invalid() => new(400, "notification_request_invalid");
    public void Dispose()
    {
        foreach (var edit in Secrets) edit.Value?.Dispose();
        Secrets.Clear();
    }
}

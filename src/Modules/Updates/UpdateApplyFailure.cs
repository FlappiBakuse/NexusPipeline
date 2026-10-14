using System.Text.Json;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Versioning;

namespace NexusPipeline.Modules.Updates;

internal sealed record UpdateApplyFailure(string TransactionId, string TargetVersion, string Code, DateTimeOffset AtUtc)
{
    internal object ToPublicView() => new { transactionId = TransactionId, targetVersion = TargetVersion, code = Code, atUtc = AtUtc.ToString("O") };

    internal static UpdateApplyFailure? ReadLatest(string installation, string currentVersion)
    {
        string directory = Path.Combine(installation, ".nxp", "state", "updates");
        if (!NexusVersion.TryParse(currentVersion, out var current)) return null;
        UpdateApplyFailure? latest = null;
        bool succeeded = false;
        try
        {
            PayloadPathSafety.RequireLinkFree(directory);
            if (!Directory.Exists(directory)) return null;
            foreach (string path in Directory.EnumerateFiles(directory, "*.result.json"))
            {
                try
                {
                    PayloadPathSafety.RequireLinkFree(path);
                    if (new FileInfo(path).Length is < 1 or > 65536) continue;
                    using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 8 });
                    var value = document.RootElement;
                    string[] fields = ["TransactionId", "Version", "Succeeded", "Code", "AtUtc", "DesktopResumeIntent"];
                    if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(item => item.Name).Order().SequenceEqual(fields.Order())) continue;
                    string? id = value.GetProperty("TransactionId").GetString(), version = value.GetProperty("Version").GetString(), code = value.GetProperty("Code").GetString();
                    if (!Guid.TryParseExact(id, "N", out _) || Path.GetFileName(path) != id + ".result.json"
                        || !NexusVersion.TryParse(version, out _) || code is not { Length: > 0 and <= 64 }
                        || code.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
                        || !value.GetProperty("AtUtc").TryGetDateTimeOffset(out var at) || at == default
                        || value.GetProperty("Succeeded").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                    var intent = value.GetProperty("DesktopResumeIntent");
                    if (intent.ValueKind != JsonValueKind.Object
                        || !intent.EnumerateObject().Select(item => item.Name).Order().SequenceEqual(new[] { "Mode", "SchemaVersion" })
                        || intent.GetProperty("SchemaVersion").GetRawText() != "1"
                        || intent.GetProperty("Mode").GetString() is not ("show" or "background")) continue;
                    if (latest is not null && (at < latest.AtUtc || at == latest.AtUtc && string.CompareOrdinal(id, latest.TransactionId) <= 0)) continue;
                    latest = new(id!, version!, code, at);
                    succeeded = value.GetProperty("Succeeded").GetBoolean();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return !succeeded && latest is not null && NexusVersion.TryParse(latest.TargetVersion, out var target) && target.CompareTo(current) > 0 ? latest : null;
    }
}

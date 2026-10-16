using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexusPipeline.Shared.Versioning;

namespace NexusPipeline.Modules.Updates;

internal sealed record ApplicationBuildIdentity(
    string ProductVersion,
    string InstallationGeneration,
    string BuildId,
    string FrontendHash,
    JsonElement BuildInputs)
{
    internal const int MaxBytes = 64 * 1024;
    private static readonly string[] InputFields =
    [
        "schemaVersion", "productVersion", "generation", "rid", "sourceSha", "sourceTreeSha",
        "partnerSha", "workflowSha", "frontendHash", "frontendPackageLockSha256",
        "desktopPackageLockSha256", "electronVersion", "electronArchiveSha256",
        "bootstrapProtocolVersion", "toolchain",
    ];
    private static readonly string[] ToolchainFields =
    [
        "dotnetSdkVersion", "dotnetRuntimeVersion", "nodeVersion", "npmVersion", "pythonVersion",
        "innoSetupVersion", "innoCompilerSha256", "innoDistributionSha256",
    ];
    private static readonly string[] RecordFields =
    [
        "schemaVersion", "product", "productVersion", "installationGeneration", "rid", "buildId",
        "frontendHash", "bootstrapProtocolVersion", "buildInputs",
    ];

    internal static ApplicationBuildIdentity Parse(ReadOnlyMemory<byte> bytes)
    {
        Require(bytes.Length is > 0 and <= MaxBytes, "Identity exceeds the size limit");
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 5 });
        JsonElement record = document.RootElement;
        CanonicalBytes(record);
        RequireFields(record, RecordFields);
        Require(record.GetProperty("schemaVersion").GetRawText() == "1", "Invalid schemaVersion");
        Require(String(record, "product") == "NexusPipeline", "Invalid product");
        JsonElement inputs = record.GetProperty("buildInputs");
        string id = ComputeBuildId(inputs);
        Require(String(record, "buildId") == id, "Build ID mismatch");
        Require(String(record, "productVersion") == String(inputs, "productVersion"), "Product version mismatch");
        Require(String(record, "installationGeneration") == String(inputs, "generation"), "Generation mismatch");
        Require(String(record, "rid") == String(inputs, "rid"), "RID mismatch");
        Require(String(record, "frontendHash") == String(inputs, "frontendHash"), "Frontend hash mismatch");
        Require(record.GetProperty("bootstrapProtocolVersion").GetRawText() == "1", "Invalid bootstrap protocol");
        return new(String(record, "productVersion"), String(record, "installationGeneration"),
            id, String(record, "frontendHash"), inputs.Clone());
    }

    internal static string ComputeBuildId(JsonElement inputs)
    {
        byte[] canonical = CanonicalBytes(inputs);
        string schema = inputs.GetProperty("schemaVersion").GetRawText();
        Require(schema is "1" or "2", "Invalid schemaVersion");
        RequireFields(inputs, schema == "2" ? [.. InputFields, "runtimeProfileId", "runtimeProfileSha256", "runtimeInventorySha256"] : InputFields);
        if (schema == "2")
        {
            RequirePattern(String(inputs, "runtimeProfileId"), "[a-z0-9][a-z0-9-]{0,63}");
            foreach (string key in new[] { "runtimeProfileSha256", "runtimeInventorySha256" }) RequirePattern(String(inputs, key), "[0-9a-f]{64}");
        }
        Require(inputs.GetProperty("bootstrapProtocolVersion").GetRawText() == "1", "Invalid bootstrap protocol");
        Require(String(inputs, "generation") == "g0170" && String(inputs, "rid") == "win-x64", "Invalid target");
        string version = String(inputs, "productVersion");
        Require(NexusVersion.TryParse(version, out NexusVersion parsed) && parsed.ToString() == version, "Invalid product version");
        RequirePattern(String(inputs, "electronVersion"), @"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)");
        foreach (string key in new[] { "sourceSha", "sourceTreeSha", "partnerSha", "workflowSha" })
            RequirePattern(String(inputs, key), "[0-9a-f]{40}");
        foreach (string key in new[] { "frontendHash", "frontendPackageLockSha256", "desktopPackageLockSha256", "electronArchiveSha256" })
            RequirePattern(String(inputs, key), "[0-9a-f]{64}");
        JsonElement toolchain = inputs.GetProperty("toolchain");
        RequireFields(toolchain, ToolchainFields);
        foreach (string key in ToolchainFields)
            RequirePattern(String(toolchain, key), key.EndsWith("Sha256", StringComparison.Ordinal) ? "[0-9a-f]{64}" : "[ -~]+");
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    internal static byte[] CanonicalBytes(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Identity must be an object");
        var output = new StringBuilder();
        Write(value, output, 0);
        Require(output.Length <= MaxBytes, "Identity exceeds the size limit");
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static void Write(JsonElement value, StringBuilder output, int depth)
    {
        Require(depth <= 4, "Identity nesting exceeds the limit");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                JsonProperty[] properties = value.EnumerateObject().ToArray();
                Require(properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == properties.Length,
                    "Duplicate key");
                output.Append('{');
                bool first = true;
                foreach (JsonProperty property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) output.Append(',');
                    first = false;
                    WriteString(property.Name, output);
                    output.Append(':');
                    Write(property.Value, output, depth + 1);
                }
                output.Append('}');
                break;
            case JsonValueKind.String:
                WriteString(value.GetString()!, output);
                break;
            case JsonValueKind.Number:
                RequirePattern(value.GetRawText(), "0|[1-9][0-9]*");
                Require(value.TryGetInt64(out long number) && number is >= 0 and <= 9007199254740991,
                    "Invalid integer");
                output.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                throw new InvalidDataException("Values must be strings, objects or non-negative safe integers");
        }
    }

    private static void WriteString(string value, StringBuilder output)
    {
        Require(value.All(c => c is >= ' ' and <= '~'), "Values must be printable ASCII");
        output.Append('"');
        foreach (char c in value)
        {
            if (c is '"' or '\\') output.Append('\\');
            output.Append(c);
        }
        output.Append('"');
    }

    private static void RequireFields(JsonElement value, string[] fields)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Expected object");
        string[] actual = value.EnumerateObject().Select(p => p.Name).ToArray();
        Require(actual.Length == fields.Length && actual.ToHashSet(StringComparer.Ordinal).SetEquals(fields), "Invalid fields");
    }

    private static string String(JsonElement value, string key)
    {
        JsonElement field = value.GetProperty(key);
        Require(field.ValueKind == JsonValueKind.String, $"Invalid {key}");
        return field.GetString()!;
    }

    private static void RequirePattern(string value, string pattern) =>
        Require(Regex.IsMatch(value, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking), "Invalid value");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

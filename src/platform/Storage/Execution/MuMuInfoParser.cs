using System.Text.Json;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class MuMuInfoParser
{
    internal static void RequireCaptureMapping(string stdout, long index, string endpoint)
    {
        if (index is < 0 or > int.MaxValue) throw new IOException("execution_capture_mapping_unverified");
        using var parsed = ReadInfo(stdout, endpoint, out var port);
        var mapping = FindMapping(parsed.RootElement, port);
        if (mapping.Name != index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new IOException("execution_capture_mapping_unverified");
        var row = mapping.Value;
        if (!row.TryGetProperty("is_process_started", out var process) || process.ValueKind != JsonValueKind.True ||
            !row.TryGetProperty("is_android_started", out var android) || android.ValueKind != JsonValueKind.True)
            throw new IOException("execution_capture_mapping_unverified");
    }

    internal static string Parse(string stdout, string endpoint)
    {
        using var parsed = ReadInfo(stdout, endpoint, out var port);
        return FindMapping(parsed.RootElement, port).Name;
    }

    private static JsonDocument ReadInfo(string stdout, string endpoint, out int port)
    {
        var uri = new Uri("http://" + ExecutionAndroidCommands.Endpoint(endpoint));
        if (!uri.IsLoopback) throw new IOException("execution_mumu_loopback_required");
        port = uri.Port;
        return StrictJson.Parse(System.Text.Encoding.UTF8.GetBytes(stdout));
    }

    private static JsonProperty FindMapping(JsonElement root, int endpointPort)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new IOException("execution_mumu_info_invalid");
        var matches = new List<JsonProperty>();
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) throw new IOException("execution_mumu_info_invalid");
            if (!property.Value.TryGetProperty("adb_port", out var port)) continue;
            if (!port.TryGetInt32(out var number) || number is < 1 or > 65535) throw new IOException("execution_mumu_info_invalid");
            if (number != endpointPort) continue;
            if (property.Name.Length is < 1 or > 10 || property.Name.Any(character => character is < '0' or > '9') ||
                !int.TryParse(property.Name, out var index) || index < 0 || property.Name != index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new IOException("execution_mumu_index_invalid");
            matches.Add(property);
        }
        if (matches.Count != 1) throw new IOException("execution_mumu_mapping_unproven");
        return matches[0];
    }
}

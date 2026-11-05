using System.Text.Json;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class MuMuInfoParser
{
    internal static void RequireCaptureMapping(string stdout, long index, string endpoint)
    {
        if (index is < 0 or > int.MaxValue || Parse(stdout, endpoint) != index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new IOException("execution_capture_mapping_unverified");
        using var parsed = StrictJson.Parse(System.Text.Encoding.UTF8.GetBytes(stdout));
        var row = parsed.RootElement.GetProperty(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!row.TryGetProperty("is_process_started", out var process) || process.ValueKind != JsonValueKind.True ||
            !row.TryGetProperty("is_android_started", out var android) || android.ValueKind != JsonValueKind.True)
            throw new IOException("execution_capture_mapping_unverified");
    }

    internal static string Parse(string stdout, string endpoint)
    {
        var uri = new Uri("http://" + ExecutionAndroidCommands.Endpoint(endpoint));
        if (!uri.IsLoopback) throw new IOException("execution_mumu_loopback_required");
        using var json = StrictJson.Parse(System.Text.Encoding.UTF8.GetBytes(stdout));
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("execution_mumu_info_invalid");
        var matches = new List<string>();
        foreach (var property in json.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) throw new IOException("execution_mumu_info_invalid");
            if (!property.Value.TryGetProperty("adb_port", out var port)) continue;
            if (!port.TryGetInt32(out var number) || number is < 1 or > 65535) throw new IOException("execution_mumu_info_invalid");
            if (number != uri.Port) continue;
            if (property.Name.Length is < 1 or > 10 || property.Name.Any(character => character is < '0' or > '9') ||
                !int.TryParse(property.Name, out var index) || index < 0 || property.Name != index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new IOException("execution_mumu_index_invalid");
            matches.Add(property.Name);
        }
        if (matches.Count != 1) throw new IOException("execution_mumu_mapping_unproven");
        return matches[0];
    }
}

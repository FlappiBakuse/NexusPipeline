using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Execution.Judgement;

[SupportedOSPlatform("windows")]
internal static class JudgementNativeProbes
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static async Task<JudgementProbeReply> ObserveAsync(JudgementProbeRequest request, CancellationToken cancellation, HttpClient? httpClient = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (request.Method != "httpGet") return Observe(request, cancellation);
        if (httpClient is null) return JudgementProbeProtocol.Fault(request, "judgement_probe_unavailable");
        try
        {
            var options = Networking.HttpGetProbe.Read(request.Options);
            var value = await Networking.HttpGetProbe.CaptureAsync(options, httpClient, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return JudgementProbeProtocol.Success(request, JsonSerializer.SerializeToElement(value, Options));
        }
        catch (JsonException) { return JudgementProbeProtocol.Fault(request, "judgement_probe_options_invalid"); }
    }

    internal static JudgementProbeReply Observe(JudgementProbeRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (request.Method == "httpGet") return JudgementProbeProtocol.Fault(request, "judgement_probe_unavailable");
        try
        {
            var filterName = request.Method == "processes" ? "nameContains" : "titleContains";
            var (pid, filter) = ReadOptions(request.Options, filterName);
            var result = request.Method == "processes"
                ? JsonSerializer.SerializeToElement(ProcessSnapshotProbe.Capture(pid, filter, cancellation), Options)
                : JsonSerializer.SerializeToElement(WindowSnapshotProbe.Capture(pid, filter, cancellation), Options);
            cancellation.ThrowIfCancellationRequested();
            return JudgementProbeProtocol.Success(request, result);
        }
        catch (JsonException) { return JudgementProbeProtocol.Fault(request, "judgement_probe_options_invalid"); }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        { return JudgementProbeProtocol.Fault(request, "judgement_probe_unavailable"); }
    }

    private static (int? Pid, string Filter) ReadOptions(JsonElement options, string filterName)
    {
        if (options.ValueKind != JsonValueKind.Object) throw Invalid();
        int? pid = null; var filter = "";
        foreach (var property in options.EnumerateObject())
        {
            if (property.Name == "pid")
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var number) || number <= 0) throw Invalid();
                pid = number;
            }
            else if (property.Name == filterName)
            {
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { Length: <= 256 } text)
                    throw Invalid();
                filter = text;
            }
            else throw Invalid();
        }
        return (pid, filter);
    }

    private static JsonException Invalid() => new("judgement_probe_options_invalid");
}

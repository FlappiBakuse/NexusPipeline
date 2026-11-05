using System.Net;
using System.Text;
using System.Text.Json;

namespace NexusPipeline.Platform.Networking;

internal sealed record HttpGetObservation(bool Ok, int Status, string Body, bool Truncated, string? Error);
internal sealed record HttpProbeRequest(Uri Destination, int TimeoutMilliseconds, int MaximumBodyBytes);

internal static class HttpGetProbe
{
    internal const int MaximumBodyBytes = 2 * 1024 * 1024;
    internal const int MaximumTimeoutMilliseconds = 15000;

    internal static HttpProbeRequest Read(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object) throw Invalid();
        string? url = null;
        var timeout = 10000;
        var maximum = MaximumBodyBytes;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in options.EnumerateObject())
        {
            if (!names.Add(item.Name)) throw Invalid();
            switch (item.Name)
            {
                case "url":
                    if (item.Value.ValueKind != JsonValueKind.String) throw Invalid();
                    url = item.Value.GetString(); break;
                case "timeoutMs":
                    if (item.Value.ValueKind != JsonValueKind.Number || !item.Value.TryGetInt32(out timeout)) throw Invalid();
                    timeout = Math.Clamp(timeout, 1, MaximumTimeoutMilliseconds); break;
                case "maxBytes":
                    if (item.Value.ValueKind != JsonValueKind.Number || !item.Value.TryGetInt32(out maximum)) throw Invalid();
                    maximum = Math.Clamp(maximum, 1, MaximumBodyBytes); break;
                default: throw Invalid();
            }
        }
        if (url is null || url.Length is < 1 or > 2048 || url.Any(char.IsControl) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var destination) || destination.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(destination.Host) || destination.UserInfo.Length != 0 || destination.Fragment.Length != 0)
            throw Invalid();
        return new(destination, timeout, maximum);
    }

    internal static async Task<HttpGetObservation> CaptureAsync(HttpProbeRequest probe, HttpClient client, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(probe.TimeoutMilliseconds);
        using var request = new HttpRequestMessage(HttpMethod.Get, probe.Destination);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            await using var content = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var output = new MemoryStream(Math.Min(probe.MaximumBodyBytes + 1, 65536));
            var buffer = new byte[8192];
            while (output.Length <= probe.MaximumBodyBytes)
            {
                var take = checked((int)Math.Min(buffer.Length, probe.MaximumBodyBytes + 1 - output.Length));
                var count = await content.ReadAsync(buffer.AsMemory(0, take), deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                output.Write(buffer, 0, count);
            }
            cancellation.ThrowIfCancellationRequested();
            var truncated = output.Length > probe.MaximumBodyBytes;
            var bytes = output.GetBuffer().AsSpan(0, checked((int)Math.Min(output.Length, probe.MaximumBodyBytes)));
            return new(response.IsSuccessStatusCode, (int)response.StatusCode, Encoding.UTF8.GetString(bytes), truncated, null);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { return new(false, 0, "", false, "timeout"); }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { cancellation.ThrowIfCancellationRequested(); return new(false, 0, "", false, "network_error"); }
    }

    private static JsonException Invalid() => new("judgement_probe_options_invalid");
}

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Platform.Networking;

namespace NexusPipeline.Host.Composition;

internal static class TestHostTransport
{
    internal sealed record Exchange(string Id, string Method, string Url, int Status,
        string BodyBase64, string ContentType, string? RequestBodySha256 = null,
        Dictionary<string, string>? RequestHeaders = null,
        Dictionary<string, string>? ResponseHeaders = null);
    internal sealed record Plan(string RunId, Exchange[] Exchanges);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static Func<ProxyConfiguration, OutboundHttpTarget, bool, HttpMessageHandler>? CreateFromEnvironment()
    {
        string? file = Environment.GetEnvironmentVariable("NEXUS_TEST_HTTP_PLAN");
        if (string.IsNullOrEmpty(file)) return null;
        string runId = Environment.GetEnvironmentVariable("NEXUS_TEST_RUN_ID") ?? "";
        return Create(file, runId);
    }

    internal static Func<ProxyConfiguration, OutboundHttpTarget, bool, HttpMessageHandler> Create(string file, string runId)
    {
        if (!Path.IsPathFullyQualified(file) || string.IsNullOrWhiteSpace(runId))
            throw new InvalidDataException("HTTP fixture requires an absolute plan and run identity");
        file = Path.GetFullPath(file);
        for (FileSystemInfo? current = new FileInfo(file); current is not null;
             current = current is FileInfo f ? f.Directory : ((DirectoryInfo)current).Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked HTTP fixture path");
        if (new FileInfo(file).Length > 64 * 1024 * 1024) throw new InvalidDataException("HTTP fixture too large");
        Plan plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(file), Json)
            ?? throw new InvalidDataException("Missing HTTP fixture");
        if (plan.RunId != runId || plan.Exchanges is not { Length: > 0 and <= 1024 }
            || plan.Exchanges.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id)
                || !Uri.TryCreate(e.Url, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https"
                || string.IsNullOrWhiteSpace(e.Method) || e.Status is < 200 or > 599
                || e.BodyBase64 is null || string.IsNullOrWhiteSpace(e.ContentType))
            || plan.Exchanges.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != plan.Exchanges.Length)
            throw new InvalidDataException("Invalid HTTP fixture identity or exchanges");
        byte[][] bodies = plan.Exchanges.Select(e => Convert.FromBase64String(e.BodyBase64)).ToArray();
        string receipt = file + ".receipts.jsonl";
        if (File.Exists(receipt) && (File.GetAttributes(receipt) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked HTTP receipt");
        object gate = new();
        HashSet<string> completed = new(StringComparer.Ordinal);
        if (File.Exists(receipt))
            foreach (string line in File.ReadLines(receipt))
            {
                using JsonDocument previous = JsonDocument.Parse(line);
                if (previous.RootElement.GetProperty("runId").GetString() != runId
                    || !previous.RootElement.GetProperty("matched").GetBoolean())
                    throw new InvalidDataException("Unsuccessful or foreign HTTP receipt");
                string id = previous.RootElement.GetProperty("id").GetString()!;
                if (!plan.Exchanges.Any(e => e.Id == id) || !completed.Add(id))
                    throw new InvalidDataException("Unknown or repeated HTTP receipt");
            }
        return (proxy, target, redirect) => target == OutboundHttpTarget.Loopback
            ? proxy.CreateHandler(target, redirect)
            : new Handler(async (request, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                byte[] requestBody = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellation);
                string requestHash = Convert.ToHexString(SHA256.HashData(requestBody));
                lock (gate)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int index = Array.FindIndex(plan.Exchanges, e => !completed.Contains(e.Id)
                        && e.Method == request.Method.Method && e.Url == request.RequestUri?.AbsoluteUri
                        && (e.RequestBodySha256 is null || e.RequestBodySha256.Equals(requestHash, StringComparison.OrdinalIgnoreCase))
                        && (e.RequestHeaders is null || e.RequestHeaders.All(h =>
                            request.Headers.TryGetValues(h.Key, out var values) && string.Join(",", values) == h.Value)));
                    string? id = index < 0 ? null : plan.Exchanges[index].Id;
                    // Receipts contain only fixture identities, never credentials, URLs or request bodies.
                    File.AppendAllText(receipt, JsonSerializer.Serialize(new { runId, id, matched = index >= 0 }) + "\n");
                    if (index < 0) throw new HttpRequestException("Unplanned external HTTP request; no network fallback");
                    completed.Add(id!);
                    Exchange exchange = plan.Exchanges[index];
                    var response = new HttpResponseMessage((HttpStatusCode)exchange.Status)
                    {
                        RequestMessage = request,
                        Content = new ByteArrayContent(bodies[index]),
                    };
                    response.Content.Headers.ContentType = new(exchange.ContentType);
                    if (exchange.ResponseHeaders is not null)
                        foreach (var header in exchange.ResponseHeaders)
                            if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value))
                                throw new InvalidDataException("Invalid fixture response header");
                    return response;
                }
            });
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}

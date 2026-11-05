namespace NexusPipeline.Extensibility.Networking;

/// <summary>Only origins declared by the installed manifest are eligible; redirects do not extend that authority.</summary>
public interface IExtensionHttp
{
    /// <summary>The host owns proxy credentials, transfer limits and activation cancellation.</summary>
    ValueTask<ExtensionHttpResponse> SendAsync(ExtensionHttpRequest request, CancellationToken cancellationToken);
}

/// <summary>Request data is copied before transfer; the response bound cannot exceed eight MiB.</summary>
public sealed record ExtensionHttpRequest(Uri Address, string Method, IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Content, string? ContentType, int MaximumResponseBytes = 2097152);

/// <summary>Response bytes are not logged or persisted by this port and may contain credentials.</summary>
public sealed record ExtensionHttpResponse(int StatusCode, IReadOnlyDictionary<string, string[]> Headers,
    ReadOnlyMemory<byte> Content);

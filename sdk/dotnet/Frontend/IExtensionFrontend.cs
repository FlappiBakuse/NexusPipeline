namespace NexusPipeline.Extensibility.Frontend;

/// <summary>Declares the finite operations consumed by this extension's browser module.</summary>
public interface IExtensionFrontend
{
    /// <summary>The host freezes these declarations before publishing the activation.</summary>
    IReadOnlyList<FrontendOperation> Operations { get; }

    /// <summary>Runs under the original activation and the authenticated caller's current permissions.</summary>
    ValueTask<FrontendResult> InvokeAsync(FrontendCall call, CancellationToken cancellationToken);
}

/// <summary>Read operations must not change saved state or produce external business effects.</summary>
public enum FrontendAccess
{
    /// <summary>Requires current settings-read permission without authorizing saved mutations.</summary>
    Read,
    /// <summary>Requires current settings-write permission and an explicit operation identity.</summary>
    Write,
    /// <summary>Plaintext disclosure retains the host's local-only secret-read restriction.</summary>
    RevealSecret,
    /// <summary>Native interaction retains the host's local-only desktop authorization.</summary>
    Desktop
}

/// <summary>Names one operation and its host-enforced access category.</summary>
public sealed record FrontendOperation(string Name, FrontendAccess Access);

/// <summary>Payload bytes belong to this invocation and must not be retained after it returns.</summary>
public sealed record FrontendCall(string Operation, Guid? OperationId, string SubjectId,
    bool IsLocal, ReadOnlyMemory<byte> Payload)
{
    /// <summary>Ordinary invocations contain JSON; image uploads carry their exact image media type.</summary>
    public string ContentType { get; init; } = "application/json";
    /// <summary>An uploaded display name grants no path or file authority.</summary>
    public string? FileName { get; init; }
    /// <summary>The host issues a distinct browser session; absence grants no private draft or disclosure authority.</summary>
    public Guid? ClientId { get; init; }
    /// <summary>Client-owned drafts end when the verified browser session expires, retires or the Host stops.</summary>
    public CancellationToken ClientLifetime { get; init; }
}

/// <summary>Only bounded JSON and supported image content may cross the browser boundary.</summary>
public sealed record FrontendResult(string ContentType, ReadOnlyMemory<byte> Content);

namespace NexusPipeline.Core.Desktop;

public enum DesktopAction { SelectFile, SelectFolder, OpenExternal, RequestClose }
public sealed record DesktopSessionIdentity(Guid InstanceId, Guid ClientId, Guid SessionId);
public sealed record DesktopActionRequest(DesktopSessionIdentity Identity, Guid CorrelationId, DesktopAction Action, string? Url);
public sealed record DesktopAuthorization(bool IsApproved, string ReasonCode);

public sealed class DesktopSession
{
    public const int MaximumRequests = 4096;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _requests = [];
    private bool _ended;
    public DesktopSessionIdentity Identity { get; }

    public DesktopSession(Guid instanceId, Guid clientId)
    {
        if (instanceId == Guid.Empty || clientId == Guid.Empty) throw new ArgumentException("Desktop identities must be explicit");
        Identity = new(instanceId, clientId, Guid.NewGuid());
    }

    public DesktopAuthorization Authorize(DesktopActionRequest request)
    {
        lock (_gate)
        {
            if (_ended) return new(false, "desktop_session_ended");
            if (request.Identity != Identity) return new(false, "desktop_session_mismatch");
            if (request.CorrelationId == Guid.Empty || !Enum.IsDefined(request.Action)) return new(false, "desktop_request_invalid");
            if (request.Action == DesktopAction.OpenExternal ? !IsExternalHttps(request.Url) : request.Url is not null)
                return new(false, "desktop_action_arguments_invalid");
            if (_requests.Contains(request.CorrelationId)) return new(false, "desktop_request_already_seen");
            if (_requests.Count >= MaximumRequests) return new(false, "desktop_session_request_limit");
            _requests.Add(request.CorrelationId);
            return new(true, "desktop_action_approved");
        }
    }

    public void End()
    {
        lock (_gate) _ended = true;
    }

    public void RequireCurrent(DesktopSessionIdentity identity)
    {
        lock (_gate)
            if (_ended || identity != Identity) throw new UnauthorizedAccessException("desktop_session_unavailable");
    }

    private static bool IsExternalHttps(string? value) => value is { Length: <= 8192 } &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 &&
        !string.IsNullOrEmpty(uri.Host);
}

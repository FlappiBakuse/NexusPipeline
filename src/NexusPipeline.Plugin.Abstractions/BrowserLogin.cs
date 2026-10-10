using System.Text.Json.Nodes;

namespace NexusPipeline.Plugin.Abstractions;

public sealed record PluginClientSessionContext(
    string HostSessionId, string ClientSessionId, string ClientKind, bool NativeBrowserAvailable)
{
    public CancellationToken Lifetime { get; init; }
}

public sealed record PluginBrowserCookieRule(string Domain, string Path, string Name);
public sealed record PluginBrowserStorageRule(string Origin, string Storage, string Key, IReadOnlyList<string> JsonPath);
public sealed record PluginBrowserLoginFlow(
    string Id, string Title, string FlowVersion, string StartUri,
    IReadOnlyList<string> NavigationOrigins, IReadOnlyList<string> PopupOrigins,
    IReadOnlyList<PluginBrowserCookieRule> Cookies, IReadOnlyList<PluginBrowserStorageRule> Storage,
    bool Ready);
public sealed record PluginBrowserCookie(string Domain, string Path, string Name, string Value);
public sealed record PluginBrowserStorage(string Origin, string Storage, string Key, string? Value);
public sealed record PluginBrowserCapture(
    IReadOnlyList<PluginBrowserCookie> Cookies, IReadOnlyList<PluginBrowserStorage> Storage, DateTimeOffset CapturedAt);
public sealed record PluginBrowserInvocation(
    string OperationId, string EditorSessionId, long FieldGeneration, JsonObject Context,
    PluginClientSessionContext ClientSession);
public enum PluginBrowserLoginTerminal { Completed, Cancelled, Failed }
public sealed record PluginBrowserLoginResult(
    bool Success, string? CandidateId = null, bool Configured = false,
    string? Source = null, string? MaskedAccount = null, string? Error = null);

public interface IPluginBrowserLoginRegistry
{
    ValueTask CancelEditorAsync(PluginClientSessionContext client, string editorSessionId, CancellationToken cancellationToken = default);
    IDisposable Register(PluginBrowserLoginFlow flow,
        Func<PluginBrowserCapture, PluginBrowserInvocation, CancellationToken, ValueTask<PluginBrowserLoginResult>> complete,
        Func<PluginBrowserInvocation, PluginBrowserLoginTerminal, CancellationToken, ValueTask> onTerminal,
        Action<PluginBrowserInvocation> validateInvocation);
}

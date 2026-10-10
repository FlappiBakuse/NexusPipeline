using NexusPipeline.Modules.BrowserLogin;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Modules.Plugins.Managed;

internal sealed class PluginBrowserLoginAdapter(BrowserLoginService service, string owner) : IPluginBrowserLoginRegistry, IDisposable
{
    private readonly object _sync = new();
    private readonly List<IDisposable> _registrations = [];
    private bool _disposed;
    public ValueTask CancelEditorAsync(PluginClientSessionContext client, string editorSessionId, CancellationToken cancellationToken = default) =>
        new(service.CancelEditorAsync(owner, client, editorSessionId).WaitAsync(cancellationToken));
    public IDisposable Register(PluginBrowserLoginFlow flow,
        Func<PluginBrowserCapture, PluginBrowserInvocation, CancellationToken, ValueTask<PluginBrowserLoginResult>> complete,
        Func<PluginBrowserInvocation, PluginBrowserLoginTerminal, CancellationToken, ValueTask> onTerminal,
        Action<PluginBrowserInvocation> validateInvocation)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var registration = service.Register(owner, flow, complete, onTerminal, validateInvocation);
            _registrations.Add(registration);
            return registration;
        }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var registration in _registrations) registration.Dispose();
            _registrations.Clear();
        }
    }
}

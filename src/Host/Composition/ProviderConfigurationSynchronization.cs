using System.Text.Json.Nodes;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Host.Composition;

internal sealed class ProviderConfigurationSynchronization : IAsyncDisposable
{
    private sealed class Watch(ScriptInstance script, FileSystemWatcher watcher, UserProviderConfigurationBinding? user) : IDisposable
    {
        internal readonly ScriptInstance Script = script;
        internal readonly UserProviderConfigurationBinding? User = user;
        internal readonly FileSystemWatcher Watcher = watcher;
        internal long LastChange = Environment.TickCount64;
        internal long LastProbe;
        internal int Dirty = 1;
        internal string Signature = "";
        internal (string[] Files, string[] Directories)? Inputs;
        public void Dispose() => Watcher.Dispose();
    }
    private readonly IScriptRepository _scripts;
    private readonly IPluginExecutionProviderResolver _providers;
    private readonly Action<ProviderConfigurationSaveRequest> _start;
    private readonly Func<string, bool> _busy;
    private readonly Action<string, string, string, string> _publish;
    private readonly IUserProviderConfigurationStore? _users;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _work;
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.Ordinal);

    internal ProviderConfigurationSynchronization(IScriptRepository scripts, IPluginExecutionProviderResolver providers,
        Action<ProviderConfigurationSaveRequest> start, Func<string, bool> busy, Action<string, string, string, string> publish,
        IUserProviderConfigurationStore? users = null)
    {
        _scripts = scripts; _providers = providers; _start = start; _busy = busy; _publish = publish; _users = users;
        _work = Task.Run(RunAsync);
    }
    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                try
                {
                var scripts = _scripts.Snapshot().Where(script => script.ExecutionProviderId.Length != 0
                    && script.ExecutionProviderConfigRevision.Length != 0).ToDictionary(script => script.Id, StringComparer.Ordinal);
                var inputs = scripts.Values.ToDictionary(script => script.Id, script => (Script: script, User: (UserProviderConfigurationBinding?)null), StringComparer.Ordinal);
                foreach (var binding in _users?.ProviderBindings() ?? [])
                    if (scripts.TryGetValue(binding.ScriptId, out var bound)) inputs.Add(bound.Id + ":" + binding.UserId, (bound, binding));
                foreach (var pair in _watches.ToArray())
                    if (!inputs.TryGetValue(pair.Key, out var input) || input.Script.RootPath != pair.Value.Script.RootPath
                        || input.Script.ExecutionProviderConfigRevision != pair.Value.Script.ExecutionProviderConfigRevision
                        || input.User?.Fingerprint != pair.Value.User?.Fingerprint)
                    { Publish(pair.Value, "current", ""); pair.Value.Dispose(); _watches.Remove(pair.Key); }
                foreach (var pair in inputs)
                {
                    var script = pair.Value.Script;
                    if (_watches.ContainsKey(pair.Key)) continue;
                    if (_providers.ResolveExecutionProvider(script.ExecutionProviderId)?.Provider is not IPluginProviderConfigurationSynchronization) continue;
                    try
                    {
                        var watcher = new FileSystemWatcher(script.RootPath) { IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                        var watch = new Watch(script, watcher, pair.Value.User);
                        void Changed(string? path = null)
                        {
                            lock (watch)
                                if (path is not null && watch.Inputs is { } inputs
                                    && !inputs.Files.Contains(path, StringComparer.OrdinalIgnoreCase)
                                    && !inputs.Directories.Any(directory => path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return;
                            Interlocked.Exchange(ref watch.LastChange, Environment.TickCount64); Interlocked.Exchange(ref watch.Dirty, 1);
                        }
                        watcher.Changed += (_, item) => Changed(item.FullPath); watcher.Created += (_, item) => Changed(item.FullPath);
                        watcher.Deleted += (_, item) => Changed(item.FullPath);
                        watcher.Renamed += (_, item) => { Changed(item.OldFullPath); Changed(item.FullPath); }; watcher.Error += (_, _) => Changed();
                        watcher.EnableRaisingEvents = true; _watches.Add(pair.Key, watch);
                    }
                    catch (Exception error) when (error is IOException or ArgumentException)
                    { _publish(script.Id, "needs_attention", "source.watch.unavailable", pair.Value.User?.UserId ?? ""); }
                }
                foreach (var watch in _watches.Values.ToArray())
                {
                    long now = Environment.TickCount64;
                    if (_busy(watch.Script.Id) || now - Interlocked.Read(ref watch.LastChange) < 1000
                        || Volatile.Read(ref watch.Dirty) == 0 && now - watch.LastProbe < 60000) continue;
                    Interlocked.Exchange(ref watch.Dirty, 0); watch.LastProbe = now;
                    try
                    {
                        if (_providers.ResolveExecutionProvider(watch.Script.ExecutionProviderId)?.Provider is not IPluginProviderConfigurationSynchronization provider)
                        { Publish(watch, "needs_attention", "configuration.lifecycle.unavailable"); continue; }
                        var script = watch.Script;
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(30));
                        var source = await provider.InspectSourceAsync(new("watch_" + script.Id, script.ExecutionProviderConfigId, script.Id,
                            watch.User?.UserId ?? "", script.RootPath, "", watch.User?.Revision ?? script.ExecutionProviderConfigRevision,
                            new() { ["sharedRevision"] = script.ExecutionProviderConfigRevision }), deadline.Token).ConfigureAwait(false);
                        if (source.WatchFiles is not null)
                        {
                            string Resolve(string relative)
                            {
                                string root = Path.GetFullPath(script.RootPath);
                                string path = Path.GetFullPath(Path.Combine(root, relative));
                                if (Path.IsPathFullyQualified(relative) || path != root && !path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidDataException("source.watch.outside_root");
                                return path;
                            }
                            if (source.WatchFiles.Count > 50000 || source.WatchDirectories?.Count > 128) throw new InvalidDataException("source.watch.limit");
                            lock (watch) watch.Inputs = (source.WatchFiles.Select(Resolve).ToArray(), (source.WatchDirectories ?? []).Select(Resolve).ToArray());
                        }
                        string signature = source.State + ":" + source.SourceRevision + ":" + source.DiagnosticCode;
                        if (source.State == "current") { watch.Signature = signature; Publish(watch, "current", ""); continue; }
                        if (watch.Signature == signature) continue;
                        watch.Signature = signature;
                        if (source.State != "changed") { Publish(watch, "needs_attention", source.DiagnosticCode); continue; }
                        Publish(watch, "sync_pending", "source.sync_pending");
                        _start(new(Guid.NewGuid().ToString("N"), script.Clone(), new JsonObject())
                            { UserId = watch.User?.UserId ?? "", ExpectedUserRevision = watch.User?.Revision ?? "" });
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                    catch (Exception)
                    { Publish(watch, "needs_attention", "source.sync.unavailable"); }
                }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    foreach (var watch in _watches.Values) Publish(watch, "needs_attention", "source.watch.unavailable");
                }
                await Task.Delay(250, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { foreach (var watch in _watches.Values) watch.Dispose(); _watches.Clear(); }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); await _work.ConfigureAwait(false); _stop.Dispose();
    }
    private void Publish(Watch watch, string state, string code) => _publish(watch.Script.Id, state, code, watch.User?.UserId ?? "");
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Scripts;

internal sealed record ScriptTypeIcon(byte[] Bytes, string ContentType);

internal sealed class ScriptTypeIconService : IAsyncDisposable
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Lazy<ScriptTypeIcon> ProjectIcon = new(() =>
    {
        using Stream stream = typeof(ScriptTypeIconService).Assembly.GetManifestResourceStream("NexusPipeline.ScriptTypeIcon")
            ?? throw new InvalidDataException("Project icon resource is missing");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new(buffer.ToArray(), "image/x-icon");
    });
    private readonly string _directory;
    private readonly Func<Uri, HttpClient> _createClient;
    private IReadOnlyDictionary<string, PluginScriptTypeIcon?> _sources;
    private readonly CancellationTokenSource _lifetime = new();
    private int _started;
    private int _disposed;
    internal Task WarmupTask { get; private set; } = Task.CompletedTask;
    private readonly ConcurrentDictionary<string, ScriptTypeIcon> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<ScriptTypeIcon>>> _pending = new(StringComparer.OrdinalIgnoreCase);

    internal ScriptTypeIconService(string directory, Func<Uri, HttpClient> createClient,
        IReadOnlyDictionary<string, PluginScriptTypeIcon?>? sources = null)
    {
        _directory = Path.GetFullPath(directory);
        _createClient = createClient;
        _sources = sources ?? new Dictionary<string, PluginScriptTypeIcon?>();
    }

    internal void StartWarmup(IReadOnlyDictionary<string, PluginScriptTypeIcon?> sources)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) throw new InvalidOperationException("Icon warmup has already started");
        _sources = new Dictionary<string, PluginScriptTypeIcon?>(sources, StringComparer.OrdinalIgnoreCase);
        WarmupTask = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(_sources.Where(item => item.Value is not null).Select(item => item.Key),
                    new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = _lifetime.Token },
                    async (typeId, token) => { await GetAsync(typeId, token).ConfigureAwait(false); }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }

    internal async Task StopWarmupAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await WarmupTask.ConfigureAwait(false);
        await Task.WhenAll(_pending.Values.Where(task => task.IsValueCreated).Select(task => task.Value)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopWarmupAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    internal async Task<ScriptTypeIcon?> GetAsync(string typeId, CancellationToken token = default)
    {
        if (typeId.Equals("general", StringComparison.OrdinalIgnoreCase)) return ProjectIcon.Value;
        if (!_sources.TryGetValue(typeId, out PluginScriptTypeIcon? source)) return null;
        if (source is null) return ProjectIcon.Value;
        if (_icons.TryGetValue(typeId, out ScriptTypeIcon? icon)) return icon;
        Lazy<Task<ScriptTypeIcon>> pending = _pending.GetOrAdd(typeId,
            key => new(() => LoadAsync(key, source)));
        // A departing client must not cancel another client's shared download.
        return await pending.Value.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task<ScriptTypeIcon> LoadAsync(string typeId, PluginScriptTypeIcon source)
    {
        try
        {
            string key = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source.Uri.AbsoluteUri)));
            string path = Path.Combine(_directory, key + ".image");
            try
            {
                PayloadPathSafety.RequireLinkFree(path);
                if (File.Exists(path) && new FileInfo(path).Length <= MaxBytes)
                {
                    byte[] cached = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                    if (Matches(cached, source)) return Remember(typeId, cached, source);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(Timeout);
            using HttpClient client = _createClient(source.Uri);
            var policy = new RemoteResourcePolicy(new RemoteResourceRules(source.Uri,
                ["https"], ["raw.githubusercontent.com"], sameOriginOnly: true, allowLoopbackHttp: false,
                Timeout, maxRedirects: 0, rejectQuery: true, rejectFragment: true, requireHttpsDefaultPort: true));
            using HttpResponseMessage response = await policy.GetAsync(client, source.Uri, "NexusPipeline", timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Icon exceeds size limit");
            using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes) throw new InvalidDataException("Icon exceeds size limit");
                buffer.Write(chunk, 0, read);
            }
            byte[] bytes = buffer.ToArray();
            if (!Matches(bytes, source)) throw new InvalidDataException("Icon differs from its fixed upstream source");
            var result = Remember(typeId, bytes, source);
            await StoreAsync(path, bytes).ConfigureAwait(false);
            return result;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ProjectIcon.Value;
        }
        finally { _pending.TryRemove(typeId, out _); }
    }

    private ScriptTypeIcon Remember(string typeId, byte[] bytes, PluginScriptTypeIcon source)
    {
        var icon = new ScriptTypeIcon(bytes, source.ContentType);
        _icons[typeId] = icon;
        return icon;
    }

    private static bool Matches(byte[] bytes, PluginScriptTypeIcon source) => bytes.Length is > 0 and <= MaxBytes
        && string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), source.Sha256, StringComparison.Ordinal);

    private async Task StoreAsync(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PayloadPathSafety.RequireLinkFree(path);
            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
            PayloadPathSafety.RequireLinkFree(path);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Realtime;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Shared.Results;

namespace NexusPipeline.Host.Composition;

internal sealed record ProviderConfigurationWorkerIdentity(int Pid, DateTime StartTime, string ImagePath, string Sha256);
internal sealed record ProviderConfigurationJournal
{
    public int SchemaVersion { get; init; } = 1;
    public string Owner { get; init; } = "provider-configuration-v1";
    public string OperationId { get; init; } = "";
    public string RequestHash { get; init; } = "";
    public string ExpectedDefinitionHash { get; init; } = "";
    public string PreviousRoot { get; init; } = "";
    public string UserId { get; init; } = "";
    public string UserConfigRevision { get; init; } = "";
    public string ExpectedUserBindingHash { get; init; } = "";
    public ScriptInstance Script { get; init; } = new();
    public string State { get; init; } = "staging";
    public string ErrorCode { get; init; } = "";
    public bool CleanupConfirmed { get; init; } = true;
    public PluginProviderConfigurationCandidate? Candidate { get; init; }
    public ProviderConfigurationWorkerIdentity? Worker { get; init; }
    public ProviderConfigurationWorkerIdentity? StartedTarget { get; init; }
}

internal sealed class ProviderConfigurationOperations : IProviderConfigurationOperations, IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, byte> ActiveJournals = new(StringComparer.OrdinalIgnoreCase);
    private sealed class Operation(ProviderConfigurationJournal journal, string? diskHash)
    {
        internal ProviderConfigurationJournal Journal = journal;
        internal string? DiskHash = diskHash;
        internal readonly CancellationTokenSource Cancel = new();
        internal Task Work = Task.CompletedTask;
        internal IDisposable? Lease;
        internal JsonObject Progress = new();
    }
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32, WriteIndented = true };
    private readonly object _sync = new();
    private readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderConfigurationOperationStatus> _completed = new(StringComparer.Ordinal);
    private readonly List<ProviderConfigurationOperationStatus> _recoveryIssues = [];
    private readonly Dictionary<string, ProviderConfigurationOperationStatus> _sourceStatuses = new(StringComparer.Ordinal);
    private readonly ProviderConfigurationSynchronization? _synchronization;
    private readonly IScriptRepository _scripts;
    private readonly IUserProviderConfigurationStore? _users;
    private readonly IPluginExecutionProviderResolver _providers;
    private readonly ExecutionDispatcher _dispatcher;
    private readonly RealtimeEventBus _events;
    private readonly string _root;
    private bool _disposed;
    private readonly Func<ExecutionProviderDescriptor, ScriptInstance, string, Action<ProcessIdentity>, ProviderWorkerPort> _worker;

    internal ProviderConfigurationOperations(IScriptRepository scripts, IPluginExecutionProviderResolver providers,
        ExecutionDispatcher dispatcher, RealtimeEventBus events, string? root = null,
        Func<ExecutionProviderDescriptor, ScriptInstance, string, Action<ProcessIdentity>, ProviderWorkerPort>? worker = null,
        Func<ScriptInstance, string, OperationResult<ScriptInstance>>? automaticCommit = null,
        IUserProviderConfigurationStore? users = null)
    {
        _scripts = scripts; _providers = providers; _dispatcher = dispatcher; _events = events; _users = users;
        _root = root ?? Path.Combine(AppPaths.InternalDir, "provider-configurations");
        _worker = worker ?? ((descriptor, script, id, started) => new ProviderWorkerPort(descriptor.PluginDirectory,
            script.RootPath, "configuration-" + id, id, 1, null, onStarted: started));
        Recover();
        if (automaticCommit is not null) _synchronization = new(scripts, providers,
            request => Start(request, automaticCommit), IsBusy, PublishSource, users);
    }
    private bool IsBusy(string scriptId)
    {
        lock (_sync) return _disposed || _recoveryIssues.Count != 0 || _operations.Values.Any(operation => operation.Journal.Script.Id == scriptId
            && (!Terminal(operation.Journal.State) || !operation.Journal.CleanupConfirmed));
    }
    private void PublishSource(string scriptId, string state, string code, string userId)
    {
        lock (_sync)
        {
            string key = scriptId + ":" + userId;
            if (state == "current")
            {
                if (_sourceStatuses.Remove(key)) _events.Publish("provider_configuration",
                    new ProviderConfigurationOperationStatus("source_" + key, scriptId, "current", "", "", true, new()) { UserId = userId });
                return;
            }
            var status = new ProviderConfigurationOperationStatus("source_" + key, scriptId, state, "", code, true, new()) { UserId = userId };
            if (_sourceStatuses.TryGetValue(key, out var previous) && previous.State == state && previous.ErrorCode == code) return;
            _sourceStatuses[key] = status; _events.Publish("provider_configuration", status);
        }
    }

    internal static string Fingerprint(ScriptInstance? script) => ProviderConfigurationContract.Fingerprint(script);
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    internal static bool SafeId(string? value) => ProviderConfigurationContract.SafeId(value);
    private string PathFor(string id)
    {
        if (!SafeId(id)) throw new InvalidDataException("configuration.operation_id.invalid");
        string root = Path.GetFullPath(_root);
        for (string? path = root; path is not null; path = Path.GetDirectoryName(path))
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("configuration.journal.link");
        string file = Path.Combine(root, id + ".json");
        if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("configuration.journal.link");
        return file;
    }
    internal static bool Terminal(string state) => state is "ready" or "cancelled" or "needs_attention";
    private string CompletedPath(string id)
    {
        string active = PathFor(id);
        string path = Path.Combine(Path.GetDirectoryName(active)!, "completed", Path.GetFileName(active));
        PayloadPathSafety.RequireLinkFree(path);
        return path;
    }

    private ProviderConfigurationJournal? ReadCompleted(string id)
    {
        string path = CompletedPath(id);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("configuration.journal.limit");
        var journal = ParseJournal(File.ReadAllBytes(path));
        if (journal.OperationId != id || !Terminal(journal.State) || !journal.CleanupConfirmed)
            throw new InvalidDataException("configuration.completed.identity");
        return journal;
    }

    private static ProviderConfigurationOperationStatus CompletedStatus(ProviderConfigurationJournal journal) => new(
        journal.OperationId, journal.Script.Id, journal.State, journal.Candidate?.Revision ?? "", journal.ErrorCode, true, new()) { UserId = journal.UserId };

    private void Archive(Operation operation)
    {
        var journal = operation.Journal;
        if (!Terminal(journal.State) || !journal.CleanupConfirmed || operation.Lease is not null) return;
        string active = PathFor(journal.OperationId), completed = CompletedPath(journal.OperationId);
        if (operation.DiskHash != Hash(File.ReadAllBytes(active))) throw new InvalidDataException("configuration.journal.external_change");
        Directory.CreateDirectory(Path.GetDirectoryName(completed)!);
        File.Move(active, completed);
        _completed[journal.OperationId] = CompletedStatus(journal);
        while (_completed.Count > 256) _completed.Remove(_completed.Keys.First());
        _operations.Remove(journal.OperationId);
        operation.Cancel.Dispose();
    }

    private static ProviderConfigurationOperationStatus Status(Operation operation) => new(operation.Journal.OperationId,
        operation.Journal.Script.Id, Terminal(operation.Journal.State) && operation.Journal.CleanupConfirmed && operation.Lease is not null
            ? "finalizing" : operation.Journal.State, operation.Journal.Candidate?.Revision ?? "",
        operation.Journal.ErrorCode, operation.Journal.CleanupConfirmed, operation.Progress.DeepClone().AsObject()) { UserId = operation.Journal.UserId };

    public ProviderConfigurationOperationStatus? Find(string id)
    {
        lock (_sync)
        {
            if (_operations.TryGetValue(id, out var operation)) return Status(operation);
            var issue = _recoveryIssues.Find(item => item.OperationId == id);
            if (issue is not null) return issue;
            return ReadCompleted(id) is { } journal ? CompletedStatus(journal) : null;
        }
    }

    public IReadOnlyList<ProviderConfigurationOperationStatus> Snapshot()
    {
        lock (_sync) return _operations.Values.Select(Status).Concat(_completed.Values).Concat(_recoveryIssues).Concat(_sourceStatuses.Values).ToArray();
    }

    public ProviderConfigurationOperationStatus Start(ProviderConfigurationSaveRequest request,
        Func<ScriptInstance, string, OperationResult<ScriptInstance>> commit)
    {
        if (request.Script is null || request.Configuration is null) throw new InvalidDataException("configuration.request.invalid");
        if (!SafeId(request.OperationId)) throw new InvalidDataException("configuration.operation_id.invalid");
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request)) > 1024 * 1024) throw new InvalidDataException("configuration.request.limit");
        string requestHash = Hash(JsonSerializer.SerializeToUtf8Bytes(request));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recoveryIssues.Count != 0) throw new InvalidDataException("configuration.recovery_required");
            if (_operations.TryGetValue(request.OperationId, out var existing))
            {
                if (existing.Journal.RequestHash != requestHash) throw new InvalidDataException("configuration.operation_id.conflict");
                return Status(existing);
            }
            if (ReadCompleted(request.OperationId) is { } completed)
            {
                if (completed.RequestHash != requestHash) throw new InvalidDataException("configuration.operation_id.conflict");
                return CompletedStatus(completed);
            }
            if (_operations.Count >= 256) throw new InvalidDataException("configuration.operations.limit");
            var script = request.Script.Clone();
            bool isNew = string.IsNullOrWhiteSpace(script.Id);
            if (isNew) script.Id = Guid.NewGuid().ToString("N");
            if (!SafeId(script.Id) || !isNew && _scripts.FindById(script.Id) is null) throw new InvalidDataException("configuration.script.missing");
            if (request.UserId.Length != 0) script = _scripts.FindById(script.Id)?.Clone() ?? throw new InvalidDataException("configuration.script.missing");
            if (string.IsNullOrWhiteSpace(script.Name) || string.IsNullOrWhiteSpace(script.ExecutionProviderId)
                || string.IsNullOrWhiteSpace(script.RootPath)) throw new InvalidDataException("configuration.script.invalid");
            if (string.IsNullOrWhiteSpace(script.ExecutionProviderConfigId)) script.ExecutionProviderConfigId = Guid.NewGuid().ToString("N");
            var descriptor = _providers.ResolveExecutionProvider(script.ExecutionProviderId);
            if (descriptor?.Provider is not IPluginProviderConfigurationLifecycle lifecycle) throw new InvalidDataException("configuration.lifecycle.unavailable");
            var previous = isNew ? null : _scripts.FindById(script.Id);
            UserProviderConfigurationBinding? userBinding = null;
            if (request.UserId.Length != 0)
            {
                if (!SafeId(request.UserId) || isNew)
                    throw new InvalidDataException("configuration.user.script_changed");
                userBinding = _users?.FindProviderBinding(request.UserId, script.Id)
                    ?? throw new InvalidDataException("configuration.user.binding_missing");
                if (userBinding.Revision != request.ExpectedUserRevision) throw new InvalidDataException("configuration.user.cas_conflict");
            }
            var operation = new Operation(new() { OperationId = request.OperationId, RequestHash = requestHash, Script = script, PreviousRoot = previous?.RootPath ?? "",
                UserId = userBinding?.UserId ?? "", UserConfigRevision = userBinding?.Revision ?? "", ExpectedUserBindingHash = userBinding?.Fingerprint ?? "",
                ExpectedDefinitionHash = isNew ? "" : Fingerprint(_scripts.FindById(script.Id)) }, null);
            Write(operation);
            _operations.Add(request.OperationId, operation);
            ActiveJournals.TryAdd(PathFor(request.OperationId), 0);
            operation.Work = Task.Run(() => RunAsync(operation, descriptor, lifecycle, request.Configuration.DeepClone().AsObject(), commit));
            return Status(operation);
        }
    }

    public bool Cancel(string id)
    {
        lock (_sync)
        {
            if (!_operations.TryGetValue(id, out var operation) || Terminal(operation.Journal.State)) return false;
            operation.Cancel.Cancel(); return true;
        }
    }

    private void Write(Operation operation)
    {
        string path = PathFor(operation.Journal.OperationId);
        if (File.Exists(path) ? operation.DiskHash != Hash(File.ReadAllBytes(path)) : operation.DiskHash is not null)
            throw new InvalidDataException("configuration.journal.external_change");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(operation.Journal, Json);
        Directory.CreateDirectory(_root);
        JsonUtil.WriteAtomic(path, Encoding.UTF8.GetString(bytes));
        operation.DiskHash = Hash(File.ReadAllBytes(path));
    }

    private void Change(Operation operation, string state, string error = "", bool? cleanup = null)
    {
        lock (_sync)
        {
            operation.Journal = operation.Journal with { State = state, ErrorCode = error, CleanupConfirmed = cleanup ?? operation.Journal.CleanupConfirmed };
            Write(operation);
            _events.Publish("provider_configuration", Status(operation));
        }
    }

    private async Task RunAsync(Operation operation, ExecutionProviderDescriptor descriptor, IPluginProviderConfigurationLifecycle lifecycle,
        JsonObject configuration, Func<ScriptInstance, string, OperationResult<ScriptInstance>> commit)
    {
        ProviderWorkerPort? worker = null;
        bool cleanup = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(operation.Cancel.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        CancellationToken token = deadline.Token;
        try
        {
            var script = operation.Journal.Script;
            string expectedRevision = operation.Journal.UserId.Length == 0 ? script.ExecutionProviderConfigRevision : operation.Journal.UserConfigRevision;
            if (operation.Journal.UserId.Length != 0) configuration["sharedRevision"] = script.ExecutionProviderConfigRevision;
            var candidate = await lifecycle.StageAsync(new(operation.Journal.OperationId, script.ExecutionProviderConfigId, script.Id,
                operation.Journal.UserId, script.RootPath, "", expectedRevision, configuration), token).ConfigureAwait(false);
            if (candidate.OperationId != operation.Journal.OperationId || candidate.ProfileId != script.ExecutionProviderConfigId
                || !SafeId(candidate.Revision) || candidate.ExpectedRevision != expectedRevision)
                throw new InvalidDataException("configuration.candidate.identity");
            ProviderPlanPolicy.Validate(script.RootPath, new(candidate.OperationId, candidate.Revision,
                candidate.AuthorizationFingerprint, candidate.Resources, [], candidate.PrivateCandidate));
            lock (_sync) operation.Journal = operation.Journal with { Candidate = candidate };
            Change(operation, "waiting_for_idle");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (_dispatcher.TryBeginProviderConfiguration(operation.Journal.OperationId, script.Id, script.RootPath, candidate.Resources, out _, operation.Journal.PreviousRoot))
                {
                    operation.Lease = new NexusPipeline.Modules.Plugins.CallbackDisposable(() => _dispatcher.EndEditSession(script.Id, "provider-operation:" + candidate.OperationId));
                    break;
                }
                await Task.Delay(200, token).ConfigureAwait(false);
            }
            if (Fingerprint(_scripts.FindById(script.Id)) != operation.Journal.ExpectedDefinitionHash) throw new InvalidDataException("configuration.script.cas_conflict");
            if (operation.Journal.UserId.Length != 0 && _users?.FindProviderBinding(operation.Journal.UserId, script.Id)?.Fingerprint != operation.Journal.ExpectedUserBindingHash)
                throw new InvalidDataException("configuration.user.cas_conflict");
            if (lifecycle is IPluginProviderConfigurationPreparation preparation)
            {
                Change(operation, "preparing_dependencies");
                var prepared = await preparation.PrepareDependenciesAsync(candidate, token).ConfigureAwait(false);
                if (prepared.OperationId != candidate.OperationId || prepared.ProfileId != candidate.ProfileId || prepared.Revision != candidate.Revision
                    || prepared.ExpectedRevision != candidate.ExpectedRevision
                    || !prepared.Resources.SequenceEqual(candidate.Resources) || prepared.Launch != candidate.Launch)
                    throw new InvalidDataException("configuration.preparation_identity_changed");
                ProviderPlanPolicy.Validate(script.RootPath, new(prepared.OperationId, prepared.Revision,
                    prepared.AuthorizationFingerprint, prepared.Resources, [], prepared.PrivateCandidate));
                candidate = prepared;
                lock (_sync) { operation.Journal = operation.Journal with { Candidate = candidate }; Write(operation); }
            }
            Change(operation, "initializing");
            var launchTarget = await ProviderConfigurationLauncher.LaunchAsync(candidate, token, identity => {
                lock (_sync)
                {
                    operation.Journal = operation.Journal with { StartedTarget = new(identity.Pid, identity.StartTime, identity.ImageName, candidate.Launch!.Sha256) };
                    Write(operation);
                }
            }).ConfigureAwait(false);
            Change(operation, "initializing", cleanup: false);
            cleanup = false;
            worker = _worker(descriptor, script, candidate.OperationId, identity => {
                if (!Path.IsPathFullyQualified(identity.ImageName)) throw new InvalidDataException("configuration.worker.identity_unavailable");
                using var stream = File.OpenRead(identity.ImageName);
                lock (_sync)
                {
                    operation.Journal = operation.Journal with { Worker = new(identity.Pid, identity.StartTime, identity.ImageName,
                        Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()) };
                    Write(operation);
                }
            });
            var witnessed = new ConfigurationEvidencePort(worker, candidate, progress => {
                lock (_sync) { operation.Progress = progress; _events.Publish("provider_configuration", Status(operation)); }
            });
            var initialized = await lifecycle.InitializeAsync(new(candidate, witnessed, _ => ValueTask.CompletedTask, launchTarget), token).ConfigureAwait(false);
            cleanup = worker.CleanupConfirmed;
            if (!cleanup || !initialized.CleanupConfirmed) throw new InvalidDataException("configuration.cleanup_unconfirmed");
            if (!worker.WorkerStarted || !initialized.Ready || !witnessed.Initialized
                || worker.TerminalStatus != "succeeded") throw new InvalidDataException("configuration.initialization_unconfirmed");
            token.ThrowIfCancellationRequested();
            Change(operation, "commit_ready", cleanup: true);
            await lifecycle.CommitAsync(candidate, initialized, token).ConfigureAwait(false);
            Change(operation, "plugin_committed");
            token.ThrowIfCancellationRequested();
            if (operation.Journal.UserId.Length == 0)
            {
                script.ExecutionProviderConfigRevision = candidate.Revision;
                var result = commit(script, operation.Journal.ExpectedDefinitionHash);
                if (!result.Succeeded) throw new InvalidDataException(result.ErrorCode ?? "configuration.script.commit_failed");
            }
            else
            {
                var result = _users!.CommitProviderBinding(operation.Journal.UserId, script.Id,
                    operation.Journal.ExpectedUserBindingHash, candidate.Revision, operation.Journal.ExpectedDefinitionHash);
                if (!result.Succeeded) throw new InvalidDataException(result.ErrorCode ?? "configuration.user.commit_failed");
            }
            Change(operation, "ready");
        }
        catch (Exception error)
        {
            cleanup = worker?.CleanupConfirmed ?? cleanup;
            string code = error is OperationCanceledException ? operation.Cancel.IsCancellationRequested ? "configuration.cancelled" : "configuration.timeout"
                : error is InvalidDataException && error.Message.Length < 256 && !error.Message.Any(char.IsWhiteSpace) ? error.Message : "configuration.failed." + error.GetType().Name;
            try
            {
                if (operation.Journal.Candidate is { } candidate && cleanup)
                    await lifecycle.AbortAsync(candidate, CancellationToken.None).ConfigureAwait(false);
                Change(operation, operation.Cancel.IsCancellationRequested && cleanup ? "cancelled" : "needs_attention", code, cleanup);
            }
            catch { lock (_sync) operation.Journal = operation.Journal with { State = "needs_attention", ErrorCode = "configuration.journal_or_abort_failed", CleanupConfirmed = false }; cleanup = false; }
        }
        finally
        {
            if (cleanup) { operation.Lease?.Dispose(); operation.Lease = null; }
            ActiveJournals.TryRemove(PathFor(operation.Journal.OperationId), out _);
            lock (_sync)
            {
                try { Archive(operation); }
                catch (Exception error) when (RecoveryError(error))
                { operation.Journal = operation.Journal with { State = "needs_attention", ErrorCode = "configuration.archive_failed" }; }
                _events.Publish("provider_configuration", Status(operation));
            }
        }
    }

    private sealed class ConfigurationEvidencePort(ProviderWorkerPort port, PluginProviderConfigurationCandidate candidate,
        Action<JsonObject> progress) : IPluginProviderWorkerPort
    {
        internal bool Initialized;
        public Task<PluginProviderWorkerResult> RunAsync(PluginProviderWorkerRequest request, Func<PluginProviderEvent, ValueTask> onEvent, CancellationToken token)
            => port.RunAsync(request, async item => {
                if (item.Kind == "task_event") throw new InvalidDataException("configuration.formal_task_forbidden");
                if (item.Kind == "progress" && item.Evidence["code"]?.GetValue<string>() == "initialization.completed")
                {
                    Initialized = item.Evidence["operationId"]?.GetValue<string>() == candidate.OperationId
                        && item.Evidence["revision"]?.GetValue<string>() == candidate.Revision
                        && item.Evidence["fingerprint"]?.GetValue<string>() == candidate.AuthorizationFingerprint
                        && item.Evidence["formalTasksSubmitted"]?.GetValue<int>() == 0;
                    if (!Initialized) throw new InvalidDataException("configuration.initialization_identity_changed");
                }
                if (item.Kind == "progress" && item.Evidence["code"]?.GetValue<string>() is { Length: <= 128 } code)
                    progress(new() { ["code"] = code });
                await onEvent(item).ConfigureAwait(false);
            }, token);
    }

    private void Recover()
    {
        try
        {
            PathFor("probe");
            if (!Directory.Exists(_root)) return;
            string completed = Path.Combine(_root, "completed");
            PayloadPathSafety.RequireLinkFree(completed);
            if (File.Exists(completed)) throw new InvalidDataException("configuration.completed.identity");
            if (Directory.Exists(completed))
                foreach (string file in Directory.EnumerateFiles(completed, "*.json").OrderBy(File.GetLastWriteTimeUtc).TakeLast(256))
                {
                    var journal = ReadCompleted(Path.GetFileNameWithoutExtension(file))!;
                    _completed[journal.OperationId] = CompletedStatus(journal);
                }
            string[] files = Directory.EnumerateFileSystemEntries(_root).Where(path => path != completed).Take(257).ToArray();
            if (files.Length > 256) throw new InvalidDataException("configuration.operations.limit");
            foreach (string file in files)
            {
                try { RecoverFile(file); }
                catch (Exception error) when (RecoveryError(error)) { RecoveryIssue(file); }
            }
        }
        catch (Exception error) when (RecoveryError(error)) { RecoveryIssue(_root); }
    }

    private static bool RecoveryError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or ArgumentException;
    private void RecoveryIssue(string path)
    {
        string id = "recovery_" + Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(path)))[..32];
        _recoveryIssues.Add(new(id, "", "needs_attention", "", "configuration.journal.unreadable", false, new()));
    }

    private void RecoverFile(string file)
    {
            if (!Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                throw new InvalidDataException("configuration.journal.unknown_entry");
            if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new InvalidDataException("configuration.journal.limit");
            byte[] bytes = File.ReadAllBytes(PathFor(Path.GetFileNameWithoutExtension(file)));
            var journal = ParseJournal(bytes);
            if (journal.SchemaVersion != 1 || journal.Owner != "provider-configuration-v1" || journal.OperationId != Path.GetFileNameWithoutExtension(file))
                throw new InvalidDataException("configuration.journal.identity");
            var operation = new Operation(journal, Hash(bytes));
            _operations.Add(journal.OperationId, operation);
            if (Terminal(journal.State) && journal.CleanupConfirmed) { Archive(operation); return; }
            bool stopped = journal.CleanupConfirmed || WorkerStopped(journal.Worker);
            var visible = _scripts.FindById(journal.Script.Id);
            bool committed = stopped && journal.Candidate is { } candidate && visible is not null
                && (journal.UserId.Length == 0 ? visible.ExecutionProviderConfigRevision == candidate.Revision
                    : Fingerprint(visible) == journal.ExpectedDefinitionHash
                        && _users?.FindProviderBinding(journal.UserId, visible.Id)?.Revision == candidate.Revision)
                && visible.ExecutionProviderConfigId == candidate.ProfileId && visible.ExecutionProviderId == journal.Script.ExecutionProviderId;
            Change(operation, committed ? "ready" : "needs_attention", committed ? "" : "configuration.recovered_no_replay", stopped);
            if (!stopped && journal.Candidate is { } blocked
                && _dispatcher.TryBeginProviderConfiguration(journal.OperationId, journal.Script.Id, journal.Script.RootPath, blocked.Resources, out _, journal.PreviousRoot))
                operation.Lease = new NexusPipeline.Modules.Plugins.CallbackDisposable(() => _dispatcher.EndEditSession(journal.Script.Id, "provider-operation:" + journal.OperationId));
            if (stopped) Archive(operation);
    }

    private static bool WorkerStopped(ProviderConfigurationWorkerIdentity? identity)
    {
        if (identity is null) return false;
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            if (process.HasExited) return true;
            var actual = ProcessIdentity.Capture(process);
            return actual is { } value && (value.StartTime != identity.StartTime || !value.ImageName.Equals(identity.ImagePath, StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException) { return true; }
        catch { return false; }
    }

    private static ProviderConfigurationJournal ParseJournal(byte[] bytes)
    {
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("configuration.journal.limit");
        using var document = JsonDocument.Parse(bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0), new JsonDocumentOptions { MaxDepth = 32 });
        void Check(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Check(item);
            if (node.ValueKind != JsonValueKind.Object) return;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("configuration.journal.duplicate_member"); Check(property.Value); }
        }
        Check(document.RootElement);
        var journal = JsonSerializer.Deserialize<ProviderConfigurationJournal>(document.RootElement.GetRawText(), Json) ?? throw new InvalidDataException("configuration.journal.invalid");
        if (journal.Script is null || journal.SchemaVersion != 1 || journal.Owner != "provider-configuration-v1" || !SafeId(journal.OperationId)
            || journal.UserId.Length != 0 && (!SafeId(journal.UserId) || journal.ExpectedUserBindingHash.Length != 64
                || !journal.ExpectedUserBindingHash.All(char.IsAsciiHexDigit)
                || journal.UserConfigRevision.Length != 0 && !SafeId(journal.UserConfigRevision))
            || journal.UserId.Length == 0 && (journal.UserConfigRevision.Length != 0 || journal.ExpectedUserBindingHash.Length != 0)
            || !SafeId(journal.Script.Id) || !Path.IsPathFullyQualified(journal.Script.RootPath)
            || journal.PreviousRoot.Length != 0 && !Path.IsPathFullyQualified(journal.PreviousRoot)
            || journal.State is not ("staging" or "waiting_for_idle" or "preparing_dependencies" or "initializing" or "commit_ready" or "plugin_committed" or "ready" or "cancelled" or "needs_attention"))
            throw new InvalidDataException("configuration.journal.identity");
        if (journal.Candidate is { } candidate)
        {
            if (candidate.OperationId != journal.OperationId || candidate.ProfileId != journal.Script.ExecutionProviderConfigId || !SafeId(candidate.Revision)
                || candidate.Resources is null || candidate.PrivateCandidate is null || candidate.DependencyPlan is null)
                throw new InvalidDataException("configuration.journal.candidate_identity");
            ProviderPlanPolicy.Validate(journal.Script.RootPath, new(candidate.OperationId, candidate.Revision,
                candidate.AuthorizationFingerprint, candidate.Resources, [], candidate.PrivateCandidate));
        }
        return journal;
    }

    internal static IReadOnlyList<ConfigRecoveryIsolationProjection> SnapshotIsolation()
    {
        string root = Path.Combine(AppPaths.InternalDir, "provider-configurations");
        try
        {
            for (string? path = Path.GetFullPath(root); path is not null; path = Path.GetDirectoryName(path))
                if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return [ConfigRecoveryIsolationProjection.Unknown("provider-configuration-journal-link")];
            if (!Directory.Exists(root)) return [];
            string completed = Path.Combine(root, "completed");
            PayloadPathSafety.RequireLinkFree(completed);
            if (File.Exists(completed)) return [ConfigRecoveryIsolationProjection.Unknown("provider-configuration-journal-invalid")];
            string[] files = Directory.EnumerateFileSystemEntries(root).Where(path => path != completed).Take(257).ToArray();
            if (files.Length > 256) return [ConfigRecoveryIsolationProjection.Unknown("provider-configuration-journal-limit")];
            foreach (string file in files)
            {
                if (ActiveJournals.ContainsKey(file)) continue;
                if (!File.Exists(file) || !Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(file).Length > 2 * 1024 * 1024)
                    return [ConfigRecoveryIsolationProjection.Unknown("provider-configuration-journal-invalid")];
                var journal = ParseJournal(File.ReadAllBytes(file));
                if (!journal.CleanupConfirmed) return [ConfigRecoveryIsolationProjection.Unknown("provider-configuration-cleanup-unconfirmed")];
            }
            return [];
        }
        catch { return [ConfigRecoveryIsolationProjection.Unknown("provider-configuration-journal-unreadable")]; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_synchronization is not null) await _synchronization.DisposeAsync().ConfigureAwait(false);
        Operation[] operations;
        lock (_sync) { _disposed = true; operations = _operations.Values.ToArray(); foreach (var operation in operations) operation.Cancel.Cancel(); }
        await Task.WhenAll(operations.Select(operation => operation.Work)).ConfigureAwait(false);
        foreach (var operation in operations) { operation.Lease?.Dispose(); operation.Cancel.Dispose(); }
    }
}

using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record MuMuDiscoveryMapping(string Endpoint, long Index, BlobReference InfoEvidence, string ManagerImageSha256);

[SupportedOSPlatform("windows")]
internal sealed class PreparedMuMuDiscovery : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _instanceLease;
    private readonly ImmutableBlobStore _blobs;
    private readonly ResourceOperationLease _resources;
    private readonly object _gate = new();
    private readonly List<IDisposable> _held;
    private readonly OwnedWorkerJob _job;
    private readonly ProcessStartObserver _observer;
    private readonly OwnedProcessOutput _output = new();
    private readonly TargetCommandOutput _raw = new();
    private readonly ProcessOutputCapture _capture;
    private readonly Task _lines;
    private readonly Dictionary<(int Pid, DateTime Created), ProcessIdentity> _members = [];
    private readonly ExecutionPlanInterpreterImage _manager;
    private readonly string _endpoint;
    private readonly Guid _discoveryId;
    private readonly ProcessIdentity _creator;
    private BlobReference? _startedArtifact, _artifact;
    private ProcessIdentity? _root;
    private MuMuDiscoveryDocument? _document;
    private MuMuDiscoveryMapping? _mapping;
    private int _attempted, _running;
    private bool _nativeStopped, _disposed, _admissionClosed;
    private Task? _stopping;
    internal BlobReference? DiagnosticEvidence => _artifact;

    private PreparedMuMuDiscovery(RuntimeInstance instance, ContractReader contracts, InstanceLease instanceLease,
        ResourceOperationLease resources, List<IDisposable> held, OwnedWorkerJob job,
        ExecutionPlanInterpreterImage manager, string endpoint, Guid discoveryId)
    {
        _instance = instance; _instanceLease = instanceLease; _blobs = new(instance, contracts);
        _resources = resources; _held = held; _job = job; _manager = manager; _endpoint = endpoint; _discoveryId = discoveryId;
        ProcessStartObserver? observer = null;
        try
        {
            _creator = ProcessIdentityReader.Read(Environment.ProcessId);
            observer = job.ObserveProcessIdentities(identity =>
            {
                lock (_members)
                {
                    if (_members.Count >= 4096 && !_members.ContainsKey((identity.ProcessId, identity.CreatedAtUtc)))
                        throw new IOException("execution_mumu_family_limit");
                    _members[(identity.ProcessId, identity.CreatedAtUtc)] = identity;
                }
            });
            _observer = observer;
            _capture = new(_output, _raw); _lines = DiscardLinesAsync();
        }
        catch { observer?.Dispose(); _output.Dispose(); throw; }
    }

    internal static PreparedMuMuDiscovery Prepare(RuntimeInstance instance, ContractReader contracts, InstanceLease instanceLease,
        ResourceLeaseManager resources, string managerPath, string endpoint)
    {
        instanceLease.VerifyHeld(instance);
        var normalized = ExecutionAndroidCommands.Endpoint(endpoint);
        if (!new Uri("http://" + normalized).IsLoopback) throw new IOException("execution_mumu_loopback_required");
        var manager = ExecutionHookImages.Capture(managerPath, runtime: true);
        var directory = Path.GetDirectoryName(manager.Path)!;
        var files = manager.RuntimeFiles.Select(file => Path.GetFullPath(Path.Combine(directory, file.RelativePath))).Prepend(manager.Path);
        var claims = PhysicalResourceAdmission.Merge(files.SelectMany(path => PhysicalResourceAdmission.Read(path, ResourceClaimMode.Reference))
            .Concat(PhysicalResourceAdmission.Read(directory, ResourceClaimMode.Reference, includeDescendants: false))
            .Concat(TargetResourceKeys.Android(normalized, ResourceClaimMode.Reference))
            .Append(new("blobs:content", ResourceClaimMode.Reference)));
        var id = Guid.NewGuid();
        var admission = resources.TryAcquire(id, claims);
        var resourceLease = admission.Lease ?? throw new IOException("execution_mumu_discovery_resources_unavailable");
        var held = new List<IDisposable>(); OwnedWorkerJob? job = null;
        try
        {
            PlanInputLease.AcquireImage(manager, held);
            job = new();
            return new(instance, contracts, instanceLease, resourceLease, held, job, manager, normalized, id);
        }
        catch
        {
            job?.Dispose();
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
            resourceLease.Dispose(); throw;
        }
    }

    internal ExecutionPlanInterpreterImage ReadManager() =>
        JsonSerializer.Deserialize<ExecutionPlanInterpreterImage>(BoundedJson.Serialize(_manager, MuMuDiscoveryEvidence.Options),
            MuMuDiscoveryEvidence.Options) ?? throw new IOException("execution_mumu_image_invalid");

    internal async Task DiscoverAsync(TimeSpan timeout, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (Interlocked.Exchange(ref _attempted, 1) != 0) throw new IOException("execution_mumu_discovery_already_attempted");
        Volatile.Write(ref _running, 1);
        Exception? original = null; int? exit = null; var startReturned = false; var clock = Stopwatch.StartNew();
        try
        {
            _instanceLease.VerifyHeld(_instance); cancellation.ThrowIfCancellationRequested();
            OwnedWorkerIdentity owned;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_admissionClosed) throw new IOException("execution_mumu_discovery_closed");
                owned = _job.Start(_manager.Path, ExecutionAndroidCommands.MuMuInfo, Path.GetDirectoryName(_manager.Path)!,
                    _instance.InstanceId, _discoveryId, root =>
                    {
                        _root = new(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);
                        lock (_members) _members[(_root.ProcessId, _root.CreatedAtUtc)] = _root;
                        var start = new MuMuDiscoveryStart(MuMuDiscoveryEvidence.Format + "-start", 1, _instance.InstanceId, _discoveryId,
                            _endpoint, _manager, _creator, _job.Name, _root);
                        _startedArtifact = _blobs.PersistPlan(BoundedJson.Serialize(start, MuMuDiscoveryEvidence.Options));
                        _held.Add(_blobs.OpenVerified(_startedArtifact));
                    }, _output);
            }
            startReturned = true;
            exit = await _job.AwaitRootAsync(owned, timeout, cancellation).ConfigureAwait(false);
        }
        catch (Exception error) { original = error; _output.CloseUnstartedEnds(); }
        try
        {
            await StopAsync().ConfigureAwait(false);
            var drain = await _capture.DrainAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await _lines.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (drain.DeadlineExceeded || !drain.StandardOutputEof || !drain.StandardErrorEof) throw new IOException("execution_mumu_output_pending");
            if (_root is null || _startedArtifact is null) throw new IOException("execution_mumu_start_unverified");
            var parts = _raw.Persist(_blobs);
            foreach (var part in parts) _held.Add(_blobs.OpenVerified(part.Content));
            using var current = Process.GetCurrentProcess();
            ImmutableArray<ProcessIdentity> family;
            lock (_members) family = _members.Values.OrderBy(item => item.ProcessId).ThenBy(item => item.CreatedAtUtc).ToImmutableArray();
            var document = new MuMuDiscoveryDocument(MuMuDiscoveryEvidence.Format, 1, _instance.InstanceId, _discoveryId,
                _endpoint, _manager, _startedArtifact, _creator, _job.Name, current.SessionId, _root, family, _job.GetAccounting(),
                exit, startReturned, _raw.IsComplete, null, parts, clock.ElapsedMilliseconds);
            Exception? mappingError = null;
            if (original is null && startReturned && exit == 0 && _raw.IsComplete)
            {
                try
                {
                    var index = MuMuInfoParser.Parse(MuMuDiscoveryEvidence.Stdout(_blobs, document), _endpoint);
                    document = document with { MappingIndex = long.Parse(index, System.Globalization.CultureInfo.InvariantCulture) };
                }
                catch (Exception error) when (error is IOException or JsonException or FormatException or OverflowException)
                { mappingError = error; }
            }
            var artifact = _blobs.PersistPlan(BoundedJson.Serialize(document, MuMuDiscoveryEvidence.Options));
            _held.Add(_blobs.OpenVerified(artifact));
            _document = MuMuDiscoveryEvidence.Read(_blobs, artifact); _artifact = artifact;
            if (original is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
            if (mappingError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(mappingError).Throw();
            if (!startReturned || exit != 0) throw new IOException("execution_mumu_info_failed");
            if (!_raw.IsComplete) throw new IOException("execution_mumu_output_incomplete");
            _mapping = new(_endpoint, document.MappingIndex ?? throw new IOException("execution_mumu_mapping_unproven"), artifact, _manager.Sha256);
            _resources.Dispose();
        }
        catch (Exception error)
        {
            if (original is not null && !ReferenceEquals(original, error)) throw new AggregateException(original, error);
            throw;
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    internal Task StopAsync()
    {
        lock (_gate)
        {
            _instanceLease.VerifyHeld(_instance);
            _admissionClosed = true;
            if (_nativeStopped) return Task.CompletedTask;
            if (_stopping is { IsCompleted: false }) return _stopping;
            return _stopping = StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        if (!_observer.Drain(TimeSpan.FromSeconds(5)) || !await _job.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false) ||
            !_observer.Drain(TimeSpan.FromSeconds(5))) throw new IOException("execution_mumu_stop_unconfirmed");
        var accounting = _job.GetAccounting(); ProcessIdentity[] members;
        lock (_members) members = _members.Values.ToArray();
        if (_observer.Failures.Length != 0 || accounting.ActiveProcesses != 0 || _job.ActiveProcessIds().Count != 0 ||
            accounting.TotalProcesses != members.Length || members.Any(member => WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped))
            throw new IOException("execution_mumu_family_unverified");
        _nativeStopped = true;
    }

    internal T Use<T>(string expectedEndpoint, ExecutionPlanInterpreterImage expectedManager, Func<MuMuDiscoveryMapping, T> callback)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); _instanceLease.VerifyHeld(_instance);
            if (Volatile.Read(ref _running) != 0 || !_nativeStopped || _mapping is null || _document is null || _artifact is null ||
                expectedEndpoint != _endpoint || !MuMuDiscoveryEvidence.SameImage(expectedManager, _manager))
                throw new IOException("execution_mumu_mapping_unbound");
            if (_job.GetAccounting() != _document.Accounting || _document.Family.Any(member => WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped) ||
                WriterStopEvidence.ObserveJob(_job.Name, _document.SessionId) != WriterState.Stopped)
                throw new IOException("execution_mumu_mapping_native_unverified");
            var saved = MuMuDiscoveryEvidence.Read(_blobs, _artifact);
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved, MuMuDiscoveryEvidence.Options),
                JsonSerializer.SerializeToElement(_document, MuMuDiscoveryEvidence.Options))) throw new IOException("execution_mumu_mapping_changed");
            foreach (var part in saved.Parts) _blobs.Verify(part.Content);
            return callback(_mapping);
        }
    }

    private async Task DiscardLinesAsync()
    { await foreach (var _ in _capture.Lines.ReadAllAsync().ConfigureAwait(false)) { } }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (Volatile.Read(ref _running) != 0 || _attempted != 0 && !_nativeStopped)
                throw new IOException("execution_mumu_discovery_collection_pending");
            if (_attempted == 0)
            {
                _output.CloseUnstartedEnds();
                var drain = _capture.DrainAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                _lines.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                if (drain.DeadlineExceeded) throw new IOException("execution_mumu_output_pending");
            }
            else if (!_capture.Completion.IsCompleted || !_lines.IsCompleted) throw new IOException("execution_mumu_output_pending");
            _observer.Dispose(); _job.Dispose(); _output.Dispose();
            for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
            _resources.Dispose(); _disposed = true;
        }
    }
}

internal sealed record MuMuDiscoveryStart(string Format, int Version, Guid InstanceId, Guid DiscoveryId,
    string Endpoint, ExecutionPlanInterpreterImage Manager, ProcessIdentity Creator, string JobName, ProcessIdentity Root);

internal sealed record MuMuDiscoveryDocument(string Format, int Version, Guid InstanceId, Guid DiscoveryId,
    string Endpoint, ExecutionPlanInterpreterImage Manager, BlobReference Start, ProcessIdentity Creator, string JobName,
    int SessionId, ProcessIdentity Root, ImmutableArray<ProcessIdentity> Family, WorkerJobAccounting Accounting,
    int? ExitCode, bool StartReturned, bool OutputComplete, long? MappingIndex, ImmutableArray<TargetCommandPart> Parts, long ElapsedMilliseconds);

internal static class MuMuDiscoveryEvidence
{
    internal const string Format = "nexus.execution.mumu-discovery";
    internal static readonly JsonSerializerOptions Options = StepResultCodec.Options;

    internal static MuMuDiscoveryDocument Read(ImmutableBlobStore blobs, BlobReference artifact)
        => VerifiedMuMuDiscovery.ReadSnapshot(blobs, artifact);

    internal sealed class VerifiedMuMuDiscovery : IDisposable
    {
        private readonly ImmutableBlobStore _blobs;
        private readonly BlobReference _artifact;
        private readonly FileStream _input;
        private readonly FileStream _started;
        private MuMuDiscoveryDocument Document { get; }
        internal Guid InstanceId => Document.InstanceId;

        private VerifiedMuMuDiscovery(ImmutableBlobStore blobs, BlobReference artifact, MuMuDiscoveryDocument document,
            FileStream input, FileStream started)
        {
            _blobs = blobs;
            _artifact = artifact;
            Document = document;
            _input = input;
            _started = started;
        }

        internal static VerifiedMuMuDiscovery Open(ImmutableBlobStore blobs, BlobReference artifact)
        {
            StepEvidenceCodec.Plan(artifact);
            var input = blobs.OpenVerified(artifact);
            FileStream? started = null;
            try
            {
                var document = ReadDocument(input);
                started = blobs.OpenVerified(document.Start);
                RequireStart(document, started);
                return new(blobs, artifact, document, input, started);
            }
            catch
            {
                try { started?.Dispose(); }
                finally { input.Dispose(); }
                throw;
            }
        }

        internal static MuMuDiscoveryDocument ReadSnapshot(ImmutableBlobStore blobs, BlobReference artifact)
        {
            using var verified = Open(blobs, artifact);
            return verified.Document;
        }

        internal bool BindsMapping(string endpoint, ExecutionPlanInterpreterImage manager, long index) =>
            Document.Endpoint == endpoint && SameImage(Document.Manager, manager) &&
            Document.StartReturned && Document.OutputComplete && Document.ExitCode == 0 &&
            index >= 0 && Document.MappingIndex == index;

        [SupportedOSPlatform("windows")]
        internal MuMuDiscoveryDocument ReadBoundMapping(string endpoint, ExecutionPlanInterpreterImage manager, long index)
        {
            if (!BindsMapping(endpoint, manager, index)) throw new IOException("execution_mumu_mapping_unbound");
            RequireOutputMapping();
            return Document;
        }

        [SupportedOSPlatform("windows")]
        internal void RequireOutputMapping()
        {
            if (MuMuInfoParser.Parse(Stdout(_blobs, Document), Document.Endpoint) !=
                Document.MappingIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new IOException("execution_mumu_mapping_unbound");
        }

        internal IReadOnlyList<BlobReference> References() =>
            new[] { _artifact, Document.Start }.Concat(Document.Parts.Select(part => part.Content)).Distinct().ToArray();

        public void Dispose()
        {
            try { _started.Dispose(); }
            finally { _input.Dispose(); }
        }
    }

    private static MuMuDiscoveryDocument ReadDocument(FileStream input)
    {
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var parsed = StrictJson.Parse(bytes);
        var value = parsed.RootElement.Deserialize<MuMuDiscoveryDocument>(Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(value, Options)) ||
            value.Format != Format || value.Version != 1 || value.InstanceId == Guid.Empty || value.DiscoveryId == Guid.Empty ||
            value.Endpoint != ExecutionAndroidCommands.Endpoint(value.Endpoint) || !new Uri("http://" + value.Endpoint).IsLoopback ||
            value.Root is null || value.Creator is null || value.Manager is null || value.Family.IsDefaultOrEmpty || value.Family.Any(item => item is null) ||
            value.Accounting is null || value.Accounting.ActiveProcesses != 0 || value.Accounting.TotalProcesses is < 1 or > 4096 ||
            value.Family.Length != value.Accounting.TotalProcesses ||
            value.Family.DistinctBy(item => (item.ProcessId, item.CreatedAtUtc)).Count() != value.Family.Length ||
            !value.Family.Contains(value.Root) || value.ElapsedMilliseconds < 0 || value.Parts.IsDefault ||
            value.MappingIndex is < 0 or > int.MaxValue || value.MappingIndex is not null && (!value.StartReturned || !value.OutputComplete || value.ExitCode != 0) ||
            value.Parts.Length > 4 || value.Parts.Any(part => part is null || part.Content is null || !Enum.IsDefined(part.Source) || part.Content.SizeBytes is < 1 or > 1048576) ||
            value.Parts.Sum(part => part.Content.SizeBytes) > TargetCommandOutput.MaximumBytes) throw Invalid();
        StepEvidenceCodec.Plan(value.Start);
        foreach (var part in value.Parts)
            if (!FileTreePolicy.Digest(part.Content.Sha256) || part.Content.RelativePath != $"blobs/content/{part.Content.Sha256}.bin") throw Invalid();
        foreach (var identity in value.Family.Append(value.Creator)) RequireIdentity(identity);
        if (value.Manager is null || value.Root.ImagePath != value.Manager.Path || value.Root.ImageSha256 != value.Manager.Sha256 ||
            value.JobName is null || value.JobName.Length != 50 || !value.JobName.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(value.JobName[18..], "N", out var jobId) || jobId == Guid.Empty || value.JobName[18..] != jobId.ToString("N") || value.SessionId < 0 ||
            !double.IsFinite(value.Accounting.CpuSeconds) || value.Accounting.CpuSeconds < 0) throw Invalid();
        return value;
    }

    private static void RequireStart(MuMuDiscoveryDocument value, FileStream started)
    {
        var startBytes = new byte[checked((int)started.Length)]; started.ReadExactly(startBytes);
        using var startJson = StrictJson.Parse(startBytes);
        var start = new MuMuDiscoveryStart(Format + "-start", 1, value.InstanceId, value.DiscoveryId, value.Endpoint,
            value.Manager, value.Creator, value.JobName, value.Root);
        if (!JsonElement.DeepEquals(startJson.RootElement, JsonSerializer.SerializeToElement(start, Options))) throw Invalid();
    }

    [SupportedOSPlatform("windows")]
    internal static MuMuDiscoveryDocument RequireMapping(ImmutableBlobStore blobs, BlobReference artifact, string endpoint,
        ExecutionPlanInterpreterImage manager, long index)
    {
        using var verified = VerifiedMuMuDiscovery.Open(blobs, artifact);
        return verified.ReadBoundMapping(endpoint, manager, index);
    }

    internal static string Stdout(ImmutableBlobStore blobs, MuMuDiscoveryDocument value)
    {
        using var bytes = new MemoryStream();
        foreach (var part in value.Parts.Where(part => part.Source == ProcessOutputSource.StandardOutput))
        { using var input = blobs.OpenVerified(part.Content); input.CopyTo(bytes); }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }

    internal static bool SameImage(ExecutionPlanInterpreterImage first, ExecutionPlanInterpreterImage second) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first, Options), JsonSerializer.SerializeToElement(second, Options));
    private static void RequireIdentity(ProcessIdentity value)
    {
        if (value is null || value.ProcessId < 1 || value.CreatedAtUtc.Kind != DateTimeKind.Utc || value.CreatedAtUtc.Ticks <= 0 ||
            !Path.IsPathFullyQualified(value.ImagePath) || !FileTreePolicy.Digest(value.ImageSha256) ||
            string.IsNullOrWhiteSpace(value.UserSid)) throw Invalid();
    }

    private static IOException Invalid() => new("execution_mumu_discovery_evidence_invalid");
}

using System.Runtime.Versioning;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class FileLogTailer : IDisposable
{
    private sealed class OpenFile(FileStream stream, string path, PhysicalObjectIdentity identity, PhysicalObjectIdentity parent)
    {
        internal readonly FileStream Stream = stream;
        internal readonly string Path = path;
        internal readonly PhysicalObjectIdentity Identity = identity, Parent = parent;
        internal long Offset, CheckpointOffset;
        internal byte[] Checkpoint = [];
        internal int Epoch;
        internal int Declaration;
    }
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly ImmutableBlobStore _blobs;
    private readonly StepEvidenceBinding _binding;
    private readonly FileLogBaseline _baseline;
    private readonly IReadOnlyList<ExecutionLogSelector> _selectors;
    private readonly RawOutputRecorder _raw;
    private readonly ExecutionOutputActivity _activity;
    private readonly Dictionary<(int Declaration, PhysicalObjectIdentity Identity), OpenFile> _open = [];
    private readonly List<IDisposable> _held = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _finished;
    private string? _failure;

    internal FileLogTailer(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        StepEvidenceBinding binding, RawOutputRecorder raw, ExecutionOutputActivity activity)
    {
        _instance = instance; _database = database; _contracts = contracts; _binding = binding; _raw = raw; _activity = activity;
        _blobs = new(instance, contracts);
        var context = new StepEvidenceReader(instance, database, contracts).Context(binding);
        var reference = context.Job.FileLogBaseline ?? throw new IOException("execution_log_baseline_missing");
        _selectors = ExecutionLogSelectors.Read(instance, contracts, context.Total, binding.StepId);
        _baseline = LogBaselineCodec.Read(_blobs, reference, binding.ParentJournalId, binding.StepId, binding.JobName,
            context.Total.Steps.Single(step => step.StepId == binding.StepId).LogTargets);
        try
        {
            _held.Add(_blobs.OpenVerified(reference));
            foreach (var scope in _baseline.Scopes)
            {
                _held.Add(StableDirectoryLease.Acquire(scope.AncestorPath));
                if (PhysicalObjectIdentity.Read(scope.AncestorPath) != scope.Identity) throw new IOException("execution_log_scope_identity_changed");
            }
            foreach (var checkpoint in _baseline.Candidates.Select(item => item.Checkpoint).OfType<BlobReference>().Distinct())
                _held.Add(_blobs.OpenVerified(checkpoint));
        }
        catch { foreach (var handle in _held) handle.Dispose(); throw; }
    }

    internal void Start()
    {
        if (_loop is not null || _finished) throw new IOException("execution_log_capture_already_started");
        _loop = RunAsync();
    }

    internal async Task SampleAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_finished) return;
            var sampledAt = DateTimeOffset.UtcNow;
            foreach (var selector in _selectors)
            {
                var path = FileLogCandidates.Select(selector, sampledAt);
                if (path is null) continue;
                using var stream = LogBaselineCodec.Open(path);
                var identity = PhysicalObjectIdentity.Read(stream.SafeFileHandle);
                var key = (checked((int)selector.Target.DeclarationIndex), identity);
                if (_open.ContainsKey(key)) continue;
                var parent = PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!);
                if (PhysicalObjectIdentity.Read(path) != identity) throw new IOException("execution_log_identity_changed");
                var owned = LogBaselineCodec.Open(path);
                try
                {
                    if (PhysicalObjectIdentity.Read(owned.SafeFileHandle) != identity) throw new IOException("execution_log_identity_changed");
                    var item = new OpenFile(owned, path, identity, parent) { Declaration = key.Item1 };
                    var before = _baseline.Candidates.FirstOrDefault(candidate => candidate.DeclarationIndex == item.Declaration && candidate.Identity == identity);
                    if (before is not null)
                    {
                        item.Offset = before.Length; item.CheckpointOffset = before.CheckpointOffset;
                        if (before.Checkpoint is { } content)
                        {
                            using var bytes = _blobs.OpenVerified(content); item.Checkpoint = new byte[checked((int)bytes.Length)]; bytes.ReadExactly(item.Checkpoint);
                        }
                    }
                    item.Epoch = _raw.OpenFile(new(0, item.Declaration, path, identity, parent, sampledAt, item.Offset,
                        before is null ? "created" : "baseline", null, null, true));
                    _open.Add(key, item); owned = null!;
                }
                finally { owned?.Dispose(); }
            }
            foreach (var file in _open.Values) Read(file, sampledAt);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        { _failure ??= "execution_log_read_failed"; _raw.FailFiles(_failure); }
        finally { _gate.Release(); }
    }

    private void Read(OpenFile file, DateTimeOffset sampledAt)
    {
        var input = file.Stream; var length = input.Length;
        var expected = file.Checkpoint;
        if (expected.Length != 0)
        {
            var available = checked((int)Math.Clamp(length - file.CheckpointOffset, 0, expected.Length));
            var current = new byte[available]; input.Position = Math.Min(file.CheckpointOffset, length); input.ReadExactly(current);
            var common = 0; while (common < current.Length && current[common] == expected[common]) common++;
            if (common != expected.Length)
            {
                _raw.EndFile(file.Epoch, false);
                file.Offset = length < file.CheckpointOffset ? 0 : file.CheckpointOffset + common;
                if (length < file.CheckpointOffset) { _failure ??= "execution_log_rewrite_prefix_unknown"; _raw.FailFiles(_failure); }
                file.Epoch = _raw.OpenFile(new(0, file.Declaration, file.Path, file.Identity, file.Parent, sampledAt,
                    file.Offset, "rewritten", null, null, true));
            }
        }
        else if (length < file.Offset) throw new IOException("execution_log_offset_unknown");
        input.Position = file.Offset; var buffer = new byte[8192];
        while (file.Offset < length)
        {
            var count = input.Read(buffer, 0, checked((int)Math.Min(buffer.Length, length - file.Offset)));
            if (count == 0) throw new IOException("execution_log_read_incomplete");
            _activity.FileBytes(count); _raw.WriteFile(file.Epoch, file.Offset, buffer.AsSpan(0, count)); file.Offset += count;
        }
        file.CheckpointOffset = Math.Max(0, file.Offset - LogBaselineCodec.CheckpointBytes);
        file.Checkpoint = new byte[checked((int)(file.Offset - file.CheckpointOffset))];
        input.Position = file.CheckpointOffset; input.ReadExactly(file.Checkpoint);
    }

    internal async Task FinishAsync()
    {
        _stop.Cancel(); if (_loop is not null) await _loop.ConfigureAwait(false);
        if (_finished) return;
        var context = new StepEvidenceReader(_instance, _database, _contracts).Context(_binding);
        var latest = new JournalStore(_instance, _contracts).Read(_binding.ParentJournalId);
        var jobs = WriterIntentCodec.ReadJobs(latest.Root, context.Total);
        if (context.Root is not null) ExecutionStepAdmission.RequireRequiredRoles(context.Total, jobs, _binding.StepId, "process", latest.Root);
        StepWritersEvidence.RequireStopped(_instance, _database, _contracts, _binding.ParentJournalId,
            jobs.Where(job => job.StepId == _binding.StepId));
        await SampleAsync().ConfigureAwait(false);
        _finished = true; _raw.CompleteFiles(_failure);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await SampleAsync().ConfigureAwait(false);
                await Task.Delay(200, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (!_finished) throw new IOException("execution_log_collection_pending");
        foreach (var file in _open.Values) file.Stream.Dispose();
        foreach (var handle in _held) handle.Dispose();
        _stop.Dispose(); _gate.Dispose();
    }
}

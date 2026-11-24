using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PreviewDomainSession
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ResourceOperationBorrow _borrow;
    private readonly PreviewDomainSource _source;
    private bool _released;
    internal OwnedPreviewJob Job { get; }

    internal PreviewDomainSession(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, AcceptedExecutionAdmission accepted, ResourceLeaseManager resources,
        StepEvidenceBinding binding, string totalSha256, OwnedWorkerIdentity primary)
    {
        if (!accepted.Root.IsActive || primary.InstanceId != binding.InstanceId || primary.AttemptId != binding.AttemptId ||
            accepted.Root.OperationId != accepted.Identity.OperationId) throw PreviewDomainCodec.Invalid();
        _instance = instance; _database = database; _contracts = contracts; _lease = lease;
        _borrow = accepted.Root.Borrow(resources, accepted.Root.OperationId, accepted.Root.Claims);
        _source = new(binding, totalSha256, PreviewDomainCodec.Name(binding, totalSha256),
            JournalWriterStore.CurrentWriter.Process, new(primary.ProcessId, primary.CreatedAtUtc, primary.ImagePath,
                primary.ImageSha256, primary.UserSid, primary.IsElevated), OwnedPreviewJob.LimitFlags,
            OwnedPreviewJob.MaximumActiveProcesses, OwnedPreviewJob.MaximumMemoryBytes);
        try { Job = new(_source.JobName); }
        catch { _borrow.Dispose(); throw; }
    }

    internal void Open() => _lease.PublishExecution(() => _borrow.Use(() =>
    {
        var state = ReadFresh();
        if (Current(state) is not null) throw PreviewDomainCodec.Invalid();
        _ = new JournalWriterStore(_database).RequireRegisteredExecutor(state.Journal.JournalId);
        Append(state, "open", null, null); return true;
    }));

    internal void RequireOpen() => _lease.PublishExecution(() => _borrow.Use(() =>
    {
        var state = ReadFresh(); var current = Current(state);
        if (current is null || current.Closed || current.Source != _source || state.IsDispatchClosed) throw PreviewDomainCodec.Invalid();
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = DatabaseConnections.BeginRead(connection);
        PreviewDomainEvidence.RequireSourceInside(connection, transaction, _source); transaction.Commit(); return true;
    }));

    internal Task<PreviewJobStop> StopAsync(TimeSpan timeout) => Job.StopAsync(timeout);

    internal void Close() => _lease.PublishExecution(() => _borrow.Use(() =>
    {
        var stopped = Job.RequireStopped(); var proof = PreparedPreviewClosure.FromStoppedOwner(Job, _source);
        var state = ReadFresh(proof); var current = Current(state);
        if (current is not null && current.Source != _source) throw PreviewDomainCodec.Invalid();
        if (current is { Closed: false }) Append(state, "closed", stopped.TotalProcesses,
            proof);
        // A failed durable open may leave no participant; its never-resumed empty native job still needs collection.
        Job.Dispose(); _released = true; return true;
    }));

    internal void ReleaseBorrow()
    {
        if (!_released) throw new IOException("execution_preview_domain_collection_pending");
        _borrow.Dispose();
    }

    private void Append(AttemptWriterState state, string stage, uint? total, PreparedPreviewClosure? closure)
    {
        var item = new PreviewDomainIntent(PreviewDomainCodec.Format, 1, _source, stage, state.Latest.Revision, total);
        Append(_instance, _database, _contracts, state, item, closure);
    }

    internal static void Append(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        AttemptWriterState state, PreviewDomainIntent item, PreparedPreviewClosure? closure)
    {
        var node = JsonNode.Parse(state.Latest.Export())!.AsObject(); node["revision"] = state.Latest.Revision + 1;
        node["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        node["participants"]!.AsArray().Add(PreviewDomainCodec.Participant(item,
            state.Total.Steps.Single(step => step.StepId == item.Source.Primary.StepId).ScriptId));
        new JournalStore(instance, contracts).Append(state.Journal.JournalId, state.Latest.Revision, JsonSerializer.SerializeToUtf8Bytes(node));
        ExecutionAttemptProjection.Synchronize(instance, database, contracts,
            AttemptWriterState.Read(instance, database, contracts, state.Journal.JournalId), previewClosure: closure);
    }

    private PreviewDomainState? Current(AttemptWriterState state) =>
        PreviewDomainCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(item => item.Source.Primary.StepId == _source.Primary.StepId);

    private AttemptWriterState ReadFresh(PreparedPreviewClosure? closure = null)
    {
        _lease.VerifyHeld(_instance);
        var state = AttemptWriterState.Read(_instance, _database, _contracts, _source.Primary.ParentJournalId);
        if (state.Latest.Revision != state.Journal.Revision)
        {
            ExecutionAttemptProjection.Synchronize(_instance, _database, _contracts, state, previewClosure: closure);
            state = AttemptWriterState.Read(_instance, _database, _contracts, _source.Primary.ParentJournalId);
        }
        return state;
    }
}

internal sealed record PreviewDomainSource(StepEvidenceBinding Primary, string TotalPlanSha256,
    string JobName, ProcessIdentity Creator, ProcessIdentity MainRoot, uint Flags, uint ActiveLimit, ulong MemoryLimit);
internal sealed record PreviewDomainIntent(string Format, int Version, PreviewDomainSource Source,
    string Stage, long BeforeRevision, uint? TotalProcesses);
internal sealed record PreviewDomainState(PreviewDomainSource Source, bool Closed);

internal static class PreviewDomainCodec
{
    internal const string Format = "nexus.execution.preview-domain";

    internal static string Name(StepEvidenceBinding primary, string totalSha256) =>
        "Local\\NexusWorker-" + Convert.ToHexString(SHA256.HashData(BoundedJson.Serialize(
            new { primary, totalSha256 }, StepResultCodec.Options)))[..32].ToLowerInvariant();

    internal static bool IsParticipant(JsonElement participant) =>
        participant.GetProperty("identity").TryGetProperty("format", out var format) && format.GetString() == Format;

    internal static PreviewDomainIntent Decode(JsonElement participant)
    {
        var decoded = ExecutionParticipantText.Read<PreviewDomainIntent>(participant.GetProperty("identity"),
            StepResultCodec.Options, Invalid);
        var value = decoded.Value;
        if (!decoded.MatchesOriginal || value.Format != Format || value.Version != 1 || value.Source?.Primary is null ||
            value.Source.Creator is null || value.Source.MainRoot is null || value.Stage is not ("open" or "closed") ||
            value.Stage == "open" && value.TotalProcesses is not null ||
            value.Source.Flags != 0x2208 || value.Source.ActiveLimit != 64 || value.Source.MemoryLimit != 268435456 ||
            value.Source.JobName != Name(value.Source.Primary, value.Source.TotalPlanSha256) ||
            !ExecutionCaptureCodec.JobName(value.Source.JobName)) throw Invalid();
        return value;
    }

    internal static ImmutableArray<PreviewDomainState> Read(AttemptJournalView journal, ExecutionPlan total, string totalSha256)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var states = new Dictionary<string, PreviewDomainState>(StringComparer.Ordinal);
        var jobs = WriterIntentCodec.ReadJobs(journal, total);
        long priorRevision = 0;
        foreach (var participant in journal.Participants.Where(IsParticipant))
        {
            var item = Decode(participant); var source = item.Source; var primary = source.Primary;
            var step = total.Steps.SingleOrDefault(step => step.StepId == primary.StepId) ?? throw Invalid();
            var main = jobs.SingleOrDefault(job => job.StepId == primary.StepId && job.Role == "process") ?? throw Invalid();
            if (total.CaptureRuntime is null || !ExecutionCaptureInputs.IsSupported(step) ||
                source.TotalPlanSha256 != totalSha256 || StepResultCodec.Binding(journal, main) != primary ||
                item.BeforeRevision <= priorRevision || item.BeforeRevision >= journal.Root.GetProperty("revision").GetInt64() ||
                participant.GetProperty("kind").GetString() != "process" ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            priorRevision = item.BeforeRevision;
            if (item.Stage == "open")
            {
                if (states.ContainsKey(primary.StepId) || states.Values.Any(value => value.Source.JobName == source.JobName)) throw Invalid();
                states.Add(primary.StepId, new(source, false));
            }
            else
            {
                if (!states.TryGetValue(primary.StepId, out var previous) || previous.Closed || previous.Source != source) throw Invalid();
                states[primary.StepId] = previous with { Closed = true };
            }
        }
        return total.Steps.Where(step => states.ContainsKey(step.StepId)).Select(step => states[step.StepId]).ToImmutableArray();
    }

    internal static void RequireHistory(IEnumerable<JournalDocument> history, ExecutionPlan total, string totalSha256,
        JournalDocument? previous = null)
    {
        JsonElement[] prior = previous is null ? [] : previous.Root.GetProperty("participants").EnumerateArray().Where(IsParticipant).ToArray();
        foreach (var document in history)
        {
            _ = Read(document.Root, total, totalSha256);
            var current = document.Root.GetProperty("participants").EnumerateArray().Where(IsParticipant).ToArray();
            if (current.Length < prior.Length || prior.Where((value, index) => !JsonElement.DeepEquals(value, current[index])).Any()) throw Invalid();
            foreach (var added in current.Skip(prior.Length))
                if (Decode(added).BeforeRevision != document.Revision - 1) throw Invalid();
            prior = current;
        }
    }

    internal static void RequireResolved(AttemptJournalView journal, ExecutionPlan total, string totalSha256, string? stepId = null)
    {
        if (Read(journal, total, totalSha256).Any(value => !value.Closed && (stepId is null || value.Source.Primary.StepId == stepId)))
            throw new IOException("execution_preview_domain_collection_pending");
    }

    internal static JsonObject Participant(PreviewDomainIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };

    internal static IOException Invalid() => new("execution_preview_domain_unbound");
}

internal static class PreviewDomainEvidence
{
    internal static void RequireSourceInside(SqliteConnection connection, SqliteTransaction transaction,
        PreviewDomainSource source)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT w.process_id,w.created_file_time,w.image_path,w.image_sha256,w.user_sid,w.elevated," +
            "r.process_id,r.created_file_time,r.image_path,r.image_sha256,r.user_sid,r.elevated " +
            "FROM main.journal_writer_jobs b JOIN main.journal_writers w USING(journal_id,writer_id) " +
            "JOIN main.journal_writer_job_roots r USING(journal_id,writer_id,job_name) " +
            "WHERE b.journal_id=$journal AND b.writer_id=$writer AND b.job_name=$job AND b.session_id=$session;";
        command.Parameters.AddWithValue("$journal", source.Primary.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", source.Primary.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$job", source.Primary.JobName);
        command.Parameters.AddWithValue("$session", source.Primary.SessionId);
        using var row = command.ExecuteReader();
        if (!row.Read() || ReadIdentity(row, 0) != source.Creator || ReadIdentity(row, 6) != source.MainRoot || row.Read())
            throw PreviewDomainCodec.Invalid();
    }

    private static ProcessIdentity ReadIdentity(SqliteDataReader row, int offset) => new(row.GetInt32(offset),
        DateTime.FromFileTimeUtc(row.GetInt64(offset + 1)), row.GetString(offset + 2), row.GetString(offset + 3),
        row.GetString(offset + 4), row.GetBoolean(offset + 5));

    [SupportedOSPlatform("windows")]
    internal static void RequireStopped(StateDatabase database, AttemptWriterState state, string? stepId = null)
    {
        PreviewDomainCodec.RequireHistory(state.RequireValidatedRead().History, state.Total, state.Attempt.ExecutionPlan.Sha256);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = DatabaseConnections.BeginRead(connection);
        foreach (var item in PreviewDomainCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .Where(item => stepId is null || item.Source.Primary.StepId == stepId))
        {
            RequireSourceInside(connection, transaction, item.Source);
            if (!item.Closed) RequireColdStopped(item.Source);
            else if (!OwnedPreviewJob.ObserveStopped(item.Source.JobName, item.Source.Primary.SessionId)) throw Pending();
        }
        transaction.Commit();
    }

    [SupportedOSPlatform("windows")]
    internal static void RequireColdStopped(PreviewDomainSource source)
    {
        if (WriterStopEvidence.ObserveProcess(source.Creator) != WriterState.Stopped ||
            !OwnedPreviewJob.ObserveStopped(source.JobName, source.Primary.SessionId)) throw Pending();
    }

    private static IOException Pending() => new("execution_preview_domain_stop_unconfirmed");
}

[SupportedOSPlatform("windows")]
internal sealed class PreparedPreviewClosure
{
    private readonly OwnedPreviewJob _owner;
    private readonly PreviewDomainSource _source;
    private PreparedPreviewClosure(OwnedPreviewJob owner, PreviewDomainSource source)
    { _owner = owner; _source = source; }

    internal static PreparedPreviewClosure FromStoppedOwner(OwnedPreviewJob owner, PreviewDomainSource source)
    {
        if (owner.Name != source.JobName || owner.RequireStopped().Flags != source.Flags) throw PreviewDomainCodec.Invalid();
        return new(owner, source);
    }

    internal void Require(PreviewDomainIntent item)
    {
        if (item.Stage != "closed" || item.Source != _source || _source.Creator != JournalWriterStore.CurrentWriter.Process ||
            _source.Primary.WriterId != JournalWriterStore.CurrentWriter.WriterId || _owner.Name != _source.JobName ||
            _owner.RequireStopped().TotalProcesses != item.TotalProcesses) throw PreviewDomainCodec.Invalid();
    }
}

internal static class PreviewDomainProjection
{
    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument next, PreparedPreviewClosure? closure)
    {
        _ = PreviewDomainCodec.Read(next.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var previous = before.Root.GetProperty("participants").EnumerateArray().Where(PreviewDomainCodec.IsParticipant).ToArray();
        var current = next.Root.GetProperty("participants").EnumerateArray().Where(PreviewDomainCodec.IsParticipant).ToArray();
        if (current.Length < previous.Length || previous.Where((value, index) =>
            !System.Text.Json.JsonElement.DeepEquals(value, current[index])).Any()) throw PreviewDomainCodec.Invalid();
        var added = current.Skip(previous.Length).ToArray();
        if (added.Length == 0) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (added.Length != 1 || !state.IsExecutionStart || state.Gate.State != EffectGateState.Entered ||
            before.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined)) throw PreviewDomainCodec.Invalid();
        var item = PreviewDomainCodec.Decode(added[0]); var source = item.Source;
        if (item.BeforeRevision != before.Revision || next.Revision != before.Revision + 1) throw PreviewDomainCodec.Invalid();
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Entered);
        PreviewDomainEvidence.RequireSourceInside(connection, transaction, source);
        if (item.Stage == "open")
        {
            ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, source.Primary.ParentJournalId);
            var main = WriterIntentCodec.ReadJobs(before.Root, state.Total).Single(job =>
                job.StepId == source.Primary.StepId && job.Role == "process");
            if (before.Phase != JournalPhase.Applying || main.ClosedElapsedMilliseconds is not null) throw PreviewDomainCodec.Invalid();
            // Recovery may project a durable open only after the original producer has stopped.
            if (source.Creator != JournalWriterStore.CurrentWriter.Process || source.Primary.WriterId != JournalWriterStore.CurrentWriter.WriterId)
                PreviewDomainEvidence.RequireColdStopped(source);
        }
        else if (closure is not null) closure.Require(item);
        else PreviewDomainEvidence.RequireColdStopped(source);
    }
}

[SupportedOSPlatform("windows")]
internal static class PreviewDomainRecovery
{
    internal static void Reconcile(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, Guid parentJournalId) => lease.PublishExecution(() =>
    {
        lease.VerifyHeld(instance);
        var state = AttemptWriterState.Read(instance, database, contracts, parentJournalId);
        PreviewDomainEvidence.RequireStopped(database, state);
        if (!state.IsDispatchClosed) throw PreviewDomainCodec.Invalid();
        foreach (var pending in PreviewDomainCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).Where(value => !value.Closed))
        {
            PreviewDomainEvidence.RequireColdStopped(pending.Source);
            PreviewDomainSession.Append(instance, database, contracts, state,
                new(PreviewDomainCodec.Format, 1, pending.Source, "closed", state.Latest.Revision, null), null);
            state = AttemptWriterState.Read(instance, database, contracts, parentJournalId);
        }
        return true;
    });
}

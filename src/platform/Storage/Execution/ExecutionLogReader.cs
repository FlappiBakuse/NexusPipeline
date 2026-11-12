using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Records;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Records;

namespace NexusPipeline.Platform.Storage.Execution;

public sealed class ExecutionLogReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts) : IRunLogReader
{
    public RunLogDescription Describe(RunLogKey key) => Description(key, Read(key));

    public ExecutionRunView ReadReport(Guid runId, Guid attemptId)
    {
        if (runId == Guid.Empty || attemptId == Guid.Empty || database.InstanceId != instance.InstanceId ||
            database.RuntimeRoot != instance.RuntimeRoot) throw Invalid();
        var saved = Context(runId, attemptId);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var main = saved.Main;
        var steps = saved.Total.Steps.Select(step =>
        {
            main.TryGetValue(step.StepId, out var result);
            ExecutionAdapterObservation? observed = null;
            if (result is not null)
            {
                var reader = new StepEvidenceReader(instance, database, contracts);
                var context = reader.ContextFromValidatedRead(result.Binding, saved.Registered, saved.Attempt, saved.Total, saved.Journal);
                _ = reader.ReadValidatedReferences(result.Binding, result.Latest.Artifact, context);
                var document = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
                if (document.TaskAdapter is { } reference)
                    observed = AdapterObservationCodec.Read(blobs, result.Binding, document.RawOutput ?? throw Invalid(), step, reference);
            }
            var tasks = step.Tasks.Select(task =>
            {
                var value = observed?.Observations.FirstOrDefault(item => item.TaskId == task.TaskId);
                return new ExecutionTaskView(task.TaskId, task.Title, task.IsSelected, task.IsEnabled,
                    Array.AsReadOnly(task.Dependencies.ToArray()), value?.State.ToString().ToLowerInvariant() ?? "unknown", value?.ReasonCode)
                    { WorkflowRole = task.WorkflowRole ?? "business", TitleLocales = task.TitleLocales?.ToDictionary(pair => pair.Key, pair => pair.Value.GetString()!),
                        CompletionKind = value is null ? null : NexusPipeline.Platform.Extensions.TaskAdapterProtocol.WriteObservation(value).CompletionKind };
            }).ToArray();
            return new ExecutionStepView(step.StepId, step.ScriptId, step.UserId, step.BindingId,
                Array.AsReadOnly(step.DependsOn.ToArray()), result?.IsCollected == true ? "collected" : "notExecuted", Array.AsReadOnly(tasks))
            {
                Engine = result?.Latest.Engine.ToString(), Business = result?.Latest.Business.ToString(), ReasonCode = result?.Latest.ReasonCode,
                MaximumInvocations = checked((int)(step.MaximumAttempts ?? 1)),
                Invocations = ExecutionInvocationCodec.Views(saved.Journal.Root).SelectMany(view => StepResultCodec.Read(view, saved.Total))
                    .Where(item => item.Binding.StepId == step.StepId && item.IsCollected)
                    .GroupBy(item => item.Binding.JobName).Select(group => group.Last()).Select(item =>
                        Invocation(item, ExecutionInvocationCodec.OrdinalForJob(saved.Journal.Root, step.StepId, item.Binding.JobName), blobs))
                    .OrderBy(item => item.Ordinal).ToArray()
            };
        }).ToArray();
        return new(saved.Receipt.OperationId, runId, attemptId, saved.Attempt.AttemptOrdinal,
            "committed", false, Array.AsReadOnly(steps))
        {
            Accounts = steps.Where(step => step.UserId is not null).GroupBy(step => step.UserId).Select(group =>
                new ExecutionAccountView(Guid.Parse(group.Key!), AccountRecordProjection.Facts(saved.Receipt.Facts, group))).ToArray()
        };
    }

    private ExecutionInvocationView Invocation(StepResultState item, int ordinal, ImmutableBlobStore blobs)
    {
        var binding = item.Binding;
        var document = StepEvidenceCodec.Read(blobs, binding, item.Latest.Artifact);
        var closure = new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(
            binding.ParentJournalId, binding.WriterId, binding.JobName, binding.SessionId, document.Closure ?? throw Invalid()) ?? throw Invalid();
        if (closure.Root != document.Root) throw Invalid();
        return new(ordinal, item.Latest.Engine.ToString(), item.Latest.Business.ToString(), item.Latest.ReasonCode)
        {
            StartedAt = closure.Root is { } root ? new DateTimeOffset(root.CreatedAtUtc) : null,
            EndedAt = closure.ClosedAt,
            DurationMilliseconds = closure.WasExecuted ? closure.RunElapsedMilliseconds : null
        };
    }

    public RunLogTail ReadTail(RunLogKey key, int maximumBytes = 65536)
    {
        if (maximumBytes is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var source = Read(key);
        var raw = source;
        var slices = new List<RunLogSlice>();
        var retirement = new RecordRetirementContents(instance, database, contracts, key.RunId);
        var remaining = maximumBytes;
        for (var index = raw.Parts.Length - 1; index >= 0 && remaining > 0 && slices.Count < 32; index--)
        {
            var part = raw.Parts[index];
            var size = (int)Math.Min(remaining, part.Content.SizeBytes);
            retirement.RequireAvailable([part.Content]);
            using var input = new ImmutableBlobStore(instance, contracts).OpenVerified(part.Content);
            input.Position = input.Length - size;
            var bytes = new byte[size]; input.ReadExactly(bytes);
            slices.Add(new(part.Offset + part.Content.SizeBytes - size, Source(part), part.FileSourceId, bytes,
                part.FileOffset + part.Content.SizeBytes - size));
            remaining -= size;
        }
        slices.Reverse();
        var recorded = raw.Parts.Sum(part => part.Content.SizeBytes);
        return new(Description(key, source), slices.AsReadOnly(), slices.Sum(slice => (long)slice.Bytes.Length) < recorded);
    }

    public Stream OpenPart(RunLogKey key, string snapshotSha256, int partIndex)
    {
        var source = Read(key);
        if (source.Reference.Sha256 != snapshotSha256 || partIndex < 0 || partIndex >= source.Parts.Length)
            throw new IOException("record_log_snapshot_unbound");
        new RecordRetirementContents(instance, database, contracts, key.RunId).RequireAvailable([source.Parts[partIndex].Content]);
        return new ImmutableBlobStore(instance, contracts).OpenVerified(source.Parts[partIndex].Content);
    }

    public Stream OpenOriginal(RunLogKey key, string snapshotSha256)
    {
        var source = Read(key);
        if (source.Reference.Sha256 != snapshotSha256) throw new IOException("record_log_snapshot_unbound");
        new RecordRetirementContents(instance, database, contracts, key.RunId).RequireAvailable(source.Parts.Select(part => part.Content).ToArray());
        var held = new List<Stream>();
        try
        {
            foreach (var part in source.Parts)
                held.Add(new ImmutableBlobStore(instance, contracts).OpenVerified(part.Content));
            return new VerifiedLogStream(held);
        }
        catch { foreach (var stream in held) stream.Dispose(); throw; }
    }

    private ExecutionLogContent Read(RunLogKey key)
    {
        if (key.RunId == Guid.Empty || key.AttemptId == Guid.Empty || !Enum.IsDefined(key.Role) || key.InvocationOrdinal is < 1 or > 99 ||
            key.StepId is not { Length: > 0 and <= 120 } || key.StepId.Any(character =>
                character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')))
            throw new ArgumentException("record_log_query_invalid");
        if (database.InstanceId != instance.InstanceId || !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath,
            Path.Combine(instance.RuntimeRoot, "state", "nexus.db"))) throw Invalid();
        return ReadSource(key, Context(key.RunId, key.AttemptId)) ?? throw new IOException("record_log_not_captured");
    }

    private SavedContext Context(Guid runId, Guid attemptId)
    {
        RegisteredJournal registered;
        NexusPipeline.Core.Execution.AttemptReceipt receipt;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            receipt = AttemptReceiptStore.ReadInside(connection, transaction, attemptId) ?? throw Invalid();
            if (receipt.Facts.RunId != runId) throw Invalid();
            registered = RecoveryJournalRegistry.ReadInside(connection, transaction, receipt.JournalId);
            var source = StartSourceRows.Read(connection, transaction, receipt.OperationId);
            if (source.RunId != runId || source.JournalId != registered.JournalId || registered.Phase != JournalPhase.Committed)
                throw Invalid();
            transaction.Commit();
        }
        using var history = JournalReadScope.HoldAttempt(instance, contracts, database, registered.JournalId);
        var metadata = database.AttemptReads?.Find(registered.JournalId)?
            .ReadPlanMetadata(registered, AttemptPreparedBinding.Reference(instance, registered));
        var attempt = metadata?.Plan ?? AttemptPreparedBinding.ReadPlan(instance, contracts, registered);
        var total = metadata?.Total ?? new ExecutionPlanCodec(instance, contracts, new(instance, contracts))
            .Read(AttemptPlanCodec.Reference(attempt.ExecutionPlan)).ReadWire();
        var journal = new JournalStore(instance, contracts).Read(registered.JournalId);
        if (journal.Revision != registered.Revision || journal.Phase != registered.Phase ||
            AttemptPreparedBinding.ExpectedReceipt(attempt, journal) != receipt) throw Invalid();
        return new(receipt, registered, attempt, total, journal);
    }

    private ExecutionLogContent? ReadSource(RunLogKey key, SavedContext savedContext, bool materialize = false)
    {
        var sources = new List<(int Ordinal, BlobReference Reference, RawOutputManifest Raw)>();
        var views = ExecutionInvocationCodec.Views(savedContext.Journal.Root);
        if (key.Role == RunLogRole.Main)
        {
            foreach (var result in views.SelectMany(view => StepResultCodec.Read(view, savedContext.Total))
                .Where(item => item.Binding.StepId == key.StepId && item.IsCollected).GroupBy(item => item.Binding.JobName).Select(group => group.Last()))
                Add(result.Binding, ReadPhysicalSource(key, savedContext, result, null));
        }
        else
        {
            var role = key.Role == RunLogRole.PreHook ? "preHook" : "postHook";
            foreach (var hook in views.SelectMany(view => HookResultCodec.Read(view, savedContext.Total))
                .Where(item => item.StepId == key.StepId && item.Role == role).GroupBy(item => item.Binding.JobName).Select(group => group.Last()))
                Add(hook.Binding, ReadPhysicalSource(key, savedContext, null, hook));
        }
        return sources.Count == 0 ? null : ExecutionLogAggregation.Create(new(instance, contracts), key, sources, materialize);

        void Add(StepEvidenceBinding binding, (BlobReference Reference, RawOutputManifest Raw)? source)
        {
            if (source is not { } item) return;
            var ordinal = ExecutionInvocationCodec.Ordinal(ExecutionInvocationCodec.ForJob(savedContext.Journal.Root, savedContext.Total, binding.JobName), key.StepId);
            if (key.InvocationOrdinal is null || key.InvocationOrdinal == ordinal)
                sources.Add((ordinal, item.Reference, item.Raw));
        }
    }

    private (BlobReference Reference, RawOutputManifest Raw)? ReadPhysicalSource(RunLogKey key, SavedContext savedContext,
        StepResultState? physicalMain, HookResultIntent? physicalHook)
    {
        var (receipt, registered, attempt, total, journal) = savedContext;
        BlobReference? output;
        StepEvidenceBinding binding;
        if (key.Role == RunLogRole.Main)
        {
            var result = physicalMain;
            if (result is null || !result.IsCollected) throw Invalid();
            binding = result.Binding;
            var reader = new StepEvidenceReader(instance, database, contracts);
            var context = reader.ContextFromValidatedRead(binding, registered, attempt, total, journal);
            _ = reader.ReadValidatedReferences(binding, result.Latest.Artifact, context);
            var document = StepEvidenceCodec.Read(new(instance, contracts), binding, result.Latest.Artifact);
            for (var depth = 0; document.RawOutput is null && document.PreviousObservation is { } previous; depth++)
            {
                if (depth >= 2) throw Invalid();
                document = StepEvidenceCodec.Read(new(instance, contracts), binding, previous);
                if (document.Stage != "observed") throw Invalid();
            }
            output = document.RawOutput;
        }
        else
        {
            var role = key.Role == RunLogRole.PreHook ? "preHook" : "postHook";
            var hook = physicalHook ?? throw Invalid();
            binding = hook.Binding;
            var context = new StepEvidenceReader(instance, database, contracts)
                .ContextFromValidatedRead(binding, registered, attempt, total, journal, role);
            output = new HookEvidenceReader(instance, database, contracts)
                .ReadFromValidatedContext(binding, role, hook.Artifact, context, verifyContents: false).RawOutput;
        }
        if (output is null) return null;
        var raw = RawOutputCodec.Read(new(instance, contracts), binding, output);
        if (raw.Binding.RunId != key.RunId || raw.Binding.AttemptId != key.AttemptId) throw Invalid();
        return (output, raw);
    }


    internal LogCatalogSnapshot CaptureCatalog(NexusPipeline.Core.Execution.AttemptReceipt expected, bool materialize = false)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot) throw Invalid();
        var context = Context(expected.Facts.RunId, expected.Facts.AttemptId);
        if (context.Receipt != expected) throw Invalid();
        return CaptureCatalog(expected, context, materialize);
    }

    internal LogCatalogSnapshot CaptureValidatedCatalog(NexusPipeline.Core.Execution.AttemptReceipt expected, AttemptWriterState state)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot) throw Invalid();
        _ = state.RequireValidatedRead();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, state.Gate.State);
        var source = StartSourceRows.Read(connection, transaction, expected.OperationId);
        StartSourceRows.RequireAttempt(source, state.Attempt);
        if (!state.IsExecutionStart || source.RunId != expected.Facts.RunId || source.JournalId != state.Journal.JournalId ||
            expected.JournalId != state.Journal.JournalId || state.Journal.Phase != JournalPhase.Committed ||
            state.Latest.Revision != state.Journal.Revision || AttemptPreparedBinding.ExpectedReceipt(state.Attempt, state.Latest) != expected ||
            AttemptReceiptStore.ReadInside(connection, transaction, expected.Facts.AttemptId) != expected) throw Invalid();
        transaction.Commit();
        return CaptureCatalog(expected, new(expected, state.Journal, state.Attempt, state.Total, state.Latest), materialize: false);
    }

    internal LogCatalogSnapshot CaptureReferenceCatalog(NexusPipeline.Core.Execution.AttemptReceipt expected,
        RegisteredJournal registered, RunAttemptPlan attempt, ExecutionPlan total, JournalDocument journal)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot) throw Invalid();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var current = RecoveryJournalRegistry.ReadInside(connection, transaction, registered.JournalId);
        AttemptPreparedBinding.RequireRegistration(current, attempt);
        var source = StartSourceRows.Read(connection, transaction, expected.OperationId);
        StartSourceRows.RequireAttempt(source, attempt);
        if (journal.JournalId != current.JournalId || journal.Kind != JournalKind.RunAttempt ||
            current.Phase != JournalPhase.Committed || current.Revision != journal.Revision || journal.Phase != current.Phase ||
            current.PlanDigest != registered.PlanDigest || current.PlanPath != registered.PlanPath ||
            journal.PlanDigest != current.PlanDigest || journal.PlanPath != current.PlanPath ||
            expected.JournalId != current.JournalId || source.RunId != expected.Facts.RunId || source.JournalId != current.JournalId ||
            AttemptPreparedBinding.ExpectedReceipt(attempt, journal) != expected ||
            AttemptReceiptStore.ReadInside(connection, transaction, expected.Facts.AttemptId) != expected) throw Invalid();
        transaction.Commit();
        // GC already holds the bound plans and journal; opening their opaque content could conflict with its deletion guard.
        return CaptureCatalog(expected, new(expected, current, attempt, total, journal), materialize: false);
    }

    private LogCatalogSnapshot CaptureCatalog(NexusPipeline.Core.Execution.AttemptReceipt expected, SavedContext context, bool materialize)
    {
        var main = context.Main;
        var hooks = context.Hooks;
        var entries = new List<LogCatalogEntry>();
        var retirement = new RecordRetirementContents(instance, database, contracts, expected.Facts.RunId);
        var availability = new Dictionary<int, RecordContentAvailability>();
        foreach (var step in context.Total.Steps)
            foreach (var role in new[] { RunLogRole.PreHook, RunLogRole.Main, RunLogRole.PostHook })
            {
                if (role == RunLogRole.Main ? !main.TryGetValue(step.StepId, out var current) || !current.IsCollected :
                    !hooks.ContainsKey((step.StepId, role == RunLogRole.PreHook ? "preHook" : "postHook"))) continue;
                var key = new RunLogKey(expected.Facts.RunId, expected.Facts.AttemptId, step.StepId, role);
                if (ReadSource(key, context, materialize) is not { } saved) continue;
                var raw = saved;
                var candidates = raw.Parts.Select(part => part.Content).Distinct().ToArray();
                var retired = retirement.Read(candidates).Count;
                availability.Add(entries.Count, retired == 0 ? RecordContentAvailability.Available :
                    retired == candidates.Length ? RecordContentAvailability.Retired : RecordContentAvailability.PartiallyRetired);
                entries.Add(new(new(entries.Count, key, saved.Reference.Sha256, raw.ObservedBytes,
                    raw.Parts.Sum(item => item.Content.SizeBytes), raw.DiscardedBytes, raw.IsComplete, raw.Parts.Length), saved.Reference));
            }
        if (entries.Count > 4096) throw Invalid();
        return new(expected, context.Journal.Revision,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(context.Journal.Export())), entries.AsReadOnly()) { ContentAvailability = availability };
    }

    private sealed record SavedContext(NexusPipeline.Core.Execution.AttemptReceipt Receipt, RegisteredJournal Registered,
        RunAttemptPlan Attempt, ExecutionPlan Total, JournalDocument Journal)
    {
        internal IReadOnlyDictionary<string, StepResultState> Main { get; } =
            ExecutionInvocationCodec.Views(Journal.Root).SelectMany(view => StepResultCodec.Read(view, Total))
                .GroupBy(item => item.Binding.StepId).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        internal IReadOnlyDictionary<(string, string), HookResultIntent> Hooks { get; } =
            ExecutionInvocationCodec.Views(Journal.Root).SelectMany(view => HookResultCodec.Read(view, Total))
                .GroupBy(item => (item.StepId, item.Role)).ToDictionary(group => group.Key, group => group.Last());
    }

    private RunLogDescription Description(RunLogKey key, ExecutionLogContent source)
    {
        var raw = source;
        var retirement = new RecordRetirementContents(instance, database, contracts, key.RunId);
        var retired = retirement.Read(raw.Parts.Select(part => part.Content).ToArray());
        return new(key, source.Reference.Sha256, raw.ObservedBytes, raw.Parts.Sum(part => part.Content.SizeBytes),
            raw.DiscardedBytes, raw.IsComplete, Array.AsReadOnly(raw.Parts.Select((part, index) =>
                new RunLogPart(index, part.Offset, Source(part), part.FileSourceId, part.FileOffset, part.Content.SizeBytes,
                    part.Content.Sha256) { ContentAvailability = retired.Contains(part.Content) ? RecordContentAvailability.Retired : RecordContentAvailability.Available }).ToArray()))
            { ContentAvailability = retired.Count == 0 ? RecordContentAvailability.Available :
                retired.Count == raw.Parts.Select(part => part.Content).Distinct().Count() ? RecordContentAvailability.Retired : RecordContentAvailability.PartiallyRetired };
    }

    private static string Source(RawOutputPart part) => part.FileSourceId is not null ? "file" :
        part.Source == NexusPipeline.Platform.Processes.ProcessOutputSource.StandardOutput ? "standardOutput" : "standardError";
    private static IOException Invalid() => new("record_log_execution_unbound");
}

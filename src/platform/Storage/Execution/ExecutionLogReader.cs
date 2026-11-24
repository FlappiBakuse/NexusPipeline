using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
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
        using var saved = Context(runId, attemptId);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var main = saved.Results.Main;
        var lifecycle = StepResultCodec.Coverage(saved.Journal.Root, saved.Total).ToDictionary(item => item.StepId);
        var skipped = BindingSkipCodec.Read(saved.Journal.Root, saved.Total).Select(item => item.StepId).ToHashSet(StringComparer.Ordinal);
        var steps = saved.Total.Steps.Select(step =>
        {
            main.TryGetValue(step.StepId, out var history);
            var result = history?.Latest;
            StepEvidenceDocument? evidence = null;
            IReadOnlyList<AdapterTaskObservation> observations = [];
            if (result is not null)
            {
                var reader = new StepEvidenceReader(instance, database, contracts);
                var context = reader.ContextFromValidatedRead(result.Binding, saved.Registered, saved.Attempt, saved.Total, saved.Journal);
                evidence = reader.ReadValidatedEvidence(result.Binding, result.Latest.Artifact, context, saved.Terminal).Document;
                observations = DailyAdapterEvidence.Project(blobs, step, evidence);
            }
            var tasks = step.Tasks.Select(task =>
            {
                var value = observations.FirstOrDefault(item => item.TaskId == task.TaskId);
                return new ExecutionTaskView(task.TaskId, task.Title, task.IsSelected, task.IsEnabled,
                    Array.AsReadOnly(task.Dependencies.ToArray()), value?.State.ToString().ToLowerInvariant() ?? "unknown", value?.ReasonCode)
                    { WorkflowRole = task.WorkflowRole ?? "business", TitleLocales = task.TitleLocales?.ToDictionary(pair => pair.Key, pair => pair.Value.GetString()!),
                        CompletionKind = value is null ? null : NexusPipeline.Platform.Extensions.TaskAdapterProtocol.WriteObservation(value).CompletionKind };
            }).ToArray();
            return new ExecutionStepView(step.StepId, step.ScriptId, step.UserId, step.BindingId,
                Array.AsReadOnly(step.DependsOn.ToArray()), skipped.Contains(step.StepId) ? "skipped" :
                    result is { IsCollected: true, Latest.Engine: not EngineOutcome.NotStarted } ? "collected" : "notExecuted", Array.AsReadOnly(tasks))
            {
                Engine = result?.Latest.Engine.ToString(), Business = result?.Latest.Business.ToString(),
                ReasonCode = lifecycle[step.StepId].NotDispatchedReason ??
                    (result?.Latest.Business == BusinessOutcome.Unverified && saved.Receipt.Facts.Lifecycle == LifecycleOutcome.Cancelled
                        ? DispatchIntentCodec.Reason : result?.Latest.ReasonCode),
                MaximumInvocations = checked((int)(step.MaximumAttempts ?? 1)),
                Invocations = history is null ? [] : history.Collected.Select(item =>
                        Invocation(item, ExecutionInvocationCodec.OrdinalForJob(saved.Journal.Root, step.StepId, item.Binding.JobName),
                            blobs, item == result ? evidence : null))
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

    private ExecutionInvocationView Invocation(StepResultState item, int ordinal, ImmutableBlobStore blobs, StepEvidenceDocument? validated)
    {
        var binding = item.Binding;
        var document = validated ?? StepEvidenceCodec.Read(blobs, binding, item.Latest.Artifact);
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
            Path.Combine(instance.RuntimeRoot, "state", "runtime.db"))) throw Invalid();
        using var saved = Context(key.RunId, key.AttemptId);
        return ReadSource(key, saved) ?? throw new IOException("record_log_not_captured");
    }

    private SavedContext Context(Guid runId, Guid attemptId)
    {
        RegisteredJournal registered;
        NexusPipeline.Core.Execution.AttemptReceipt receipt;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records))
        using (var transaction = DatabaseConnections.BeginRead(connection))
        {
            receipt = AttemptReceiptStore.ReadInside(connection, transaction, attemptId) ?? throw Invalid();
            if (receipt.Facts.RunId != runId) throw Invalid();
            registered = RecoveryJournalRegistry.ReadInside(connection, transaction, receipt.JournalId);
            var source = StartSourceRows.Read(connection, transaction, receipt.OperationId);
            if (source.RunId != runId || source.JournalId != registered.JournalId || registered.Phase != JournalPhase.Committed)
                throw Invalid();
            transaction.Commit();
        }
        var history = JournalReadScope.HoldAttempt(instance, contracts, database, registered.JournalId);
        try
        {
            var metadata = database.AttemptReads?.Find(registered.JournalId)?
                .ReadPlanMetadata(registered, AttemptPreparedBinding.Reference(instance, registered));
            var attempt = metadata?.Plan ?? AttemptPreparedBinding.ReadPlan(instance, contracts, registered);
            var total = metadata?.Total ?? new ExecutionPlanCodec(instance, contracts, new(instance, contracts))
                .Read(AttemptPlanCodec.Reference(attempt.ExecutionPlan)).ReadWire();
            var journal = new JournalStore(instance, contracts).Read(registered.JournalId);
            if (journal.Revision != registered.Revision || journal.Phase != registered.Phase ||
                AttemptPreparedBinding.ExpectedReceipt(attempt, journal) != receipt) throw Invalid();
            return new(receipt, registered, attempt, total, journal) { History = history, Terminal = () => history.ReadTerminal(journal) };
        }
        catch { history.Dispose(); throw; }
    }

    private ExecutionLogContent? ReadSource(RunLogKey key, SavedContext savedContext, bool materialize = false)
    {
        var sources = new List<(int Ordinal, BlobReference Reference, RawOutputManifest Raw)>();
        if (key.Role == RunLogRole.Main)
        {
            if (savedContext.Results.Main.TryGetValue(key.StepId, out var history))
                foreach (var result in history.Collected)
                    Add(result.Binding, result, null);
        }
        else
        {
            var role = key.Role == RunLogRole.PreHook ? "preHook" : "postHook";
            if (savedContext.Results.Hooks.TryGetValue((key.StepId, role), out var invocations))
                foreach (var hook in invocations)
                    Add(hook.Binding, null, hook);
        }
        return sources.Count == 0 ? null : ExecutionLogAggregation.Create(new(instance, contracts), key, sources, materialize);

        void Add(StepEvidenceBinding binding, StepResultState? result, HookResultIntent? hook)
        {
            int? ordinal = null;
            if (key.InvocationOrdinal is { } requested)
            {
                ordinal = Ordinal(binding);
                if (ordinal != requested) return;
            }
            if (ReadPhysicalSource(key, savedContext, result, hook) is { } item)
                sources.Add((ordinal ?? Ordinal(binding), item.Reference, item.Raw));
        }

        int Ordinal(StepEvidenceBinding binding) =>
            ExecutionInvocationCodec.Ordinal(
                ExecutionInvocationCodec.ForJob(savedContext.Journal.Root, savedContext.Total, binding.JobName), key.StepId);
    }

    private (BlobReference Reference, RawOutputManifest Raw)? ReadPhysicalSource(RunLogKey key, SavedContext savedContext,
        StepResultState? physicalMain, HookResultIntent? physicalHook)
    {
        var (receipt, registered, attempt, total, journal) = savedContext;
        (BlobReference Reference, RawOutputManifest Raw)? output;
        if (key.Role == RunLogRole.Main)
        {
            var result = physicalMain;
            if (result is null || !result.IsCollected) throw Invalid();
            var binding = result.Binding;
            var reader = new StepEvidenceReader(instance, database, contracts);
            var context = reader.ContextFromValidatedRead(binding, registered, attempt, total, journal);
            output = reader.ReadValidatedEvidence(binding, result.Latest.Artifact, context, savedContext.Terminal).Output;
        }
        else
        {
            var role = key.Role == RunLogRole.PreHook ? "preHook" : "postHook";
            var hook = physicalHook ?? throw Invalid();
            var binding = hook.Binding;
            var context = new StepEvidenceReader(instance, database, contracts)
                .ContextFromValidatedRead(binding, registered, attempt, total, journal, role);
            output = new HookEvidenceReader(instance, database, contracts)
                .ReadValidatedOutput(binding, role, hook.Artifact, context);
        }
        if (output is { } resolved && (resolved.Raw.Binding.RunId != key.RunId || resolved.Raw.Binding.AttemptId != key.AttemptId)) throw Invalid();
        return output;
    }


    internal LogCatalogSnapshot CaptureCatalog(NexusPipeline.Core.Execution.AttemptReceipt expected, bool materialize = false,
        LogCatalogQuery? page = null)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot) throw Invalid();
        using var context = Context(expected.Facts.RunId, expected.Facts.AttemptId);
        if (context.Receipt != expected) throw Invalid();
        return CaptureCatalog(expected, context, materialize, page);
    }

    internal LogCatalogSnapshot CaptureValidatedCatalog(NexusPipeline.Core.Execution.AttemptReceipt expected, AttemptWriterState state)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot) throw Invalid();
        _ = state.RequireValidatedRead();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = DatabaseConnections.BeginRead(connection);
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
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = DatabaseConnections.BeginRead(connection);
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

    private LogCatalogSnapshot CaptureCatalog(NexusPipeline.Core.Execution.AttemptReceipt expected, SavedContext context, bool materialize,
        LogCatalogQuery? page = null)
    {
        var main = context.Results.Main;
        var hooks = context.Results.Hooks;
        var entries = new List<LogCatalogEntry>();
        var retirement = new RecordRetirementContents(instance, database, contracts, expected.Facts.RunId);
        var availability = new Dictionary<int, RecordContentAvailability>();
        foreach (var step in context.Total.Steps)
            foreach (var role in new[] { RunLogRole.PreHook, RunLogRole.Main, RunLogRole.PostHook })
            {
                if (role == RunLogRole.Main ? !main.TryGetValue(step.StepId, out var current) || !current.Latest.IsCollected :
                    !hooks.ContainsKey((step.StepId, role == RunLogRole.PreHook ? "preHook" : "postHook"))) continue;
                var key = new RunLogKey(expected.Facts.RunId, expected.Facts.AttemptId, step.StepId, role);
                if (ReadSource(key, context, materialize) is not { } saved) continue;
                var position = entries.Count;
                if (page is null || position > page.AfterPosition && position <= page.AfterPosition + page.Limit)
                {
                    var candidates = saved.Parts.Select(part => part.Content).Distinct().ToArray();
                    var retired = retirement.Read(candidates).Count;
                    availability.Add(position, retired == 0 ? RecordContentAvailability.Available :
                        retired == candidates.Length ? RecordContentAvailability.Retired : RecordContentAvailability.PartiallyRetired);
                }
                entries.Add(new(new(position, key, saved.Reference.Sha256, saved.ObservedBytes,
                    saved.Parts.Sum(item => item.Content.SizeBytes), saved.DiscardedBytes, saved.IsComplete, saved.Parts.Length), saved.Reference));
            }
        if (entries.Count > 4096) throw Invalid();
        return new(expected, context.Journal.Revision,
            context.Journal.Sha256, entries.AsReadOnly()) { ContentAvailability = availability };
    }

    private sealed record SavedContext(NexusPipeline.Core.Execution.AttemptReceipt Receipt, RegisteredJournal Registered,
        RunAttemptPlan Attempt, ExecutionPlan Total, JournalDocument Journal) : IDisposable
    {
        internal JournalReadScope? History { get; init; }
        internal Func<JournalDocument?>? Terminal { get; init; }
        public void Dispose() => History?.Dispose();

        internal SavedResults Results { get; } = SavedResults.Read(Journal.Root, Total);
    }

    private sealed record SavedStepResults(StepResultState Latest, StepResultState[] Collected);

    private sealed record SavedResults(IReadOnlyDictionary<string, SavedStepResults> Main,
        IReadOnlyDictionary<(string StepId, string Role), HookResultIntent[]> Hooks)
    {
        internal static SavedResults Read(AttemptJournalView journal, ExecutionPlan total)
        {
            var views = ExecutionInvocationCodec.Views(journal);
            var main = views.SelectMany(view => StepResultCodec.Read(view, total)).GroupBy(item => item.Binding.StepId)
                .ToDictionary(group => group.Key, group => new SavedStepResults(group.Last(), group.Where(item => item.IsCollected)
                    .GroupBy(item => item.Binding.JobName).Select(invocation => invocation.Last()).ToArray()), StringComparer.Ordinal);
            var hooks = views.SelectMany(view => HookResultCodec.Read(view, total)).GroupBy(item => (item.StepId, item.Role))
                .ToDictionary(group => group.Key, group => group.GroupBy(item => item.Binding.JobName).Select(invocation => invocation.Last()).ToArray());
            return new(main, hooks);
        }
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

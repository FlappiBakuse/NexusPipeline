using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class AndroidControlEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    AndroidEvidenceScope? readScope = null)
{
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);

    internal AndroidCommandRequest Request(AttemptWriterState state, AndroidCommandRegistration registration)
    {
        return Request(state.Total, registration);
    }

    internal AndroidCommandRequest Request(ExecutionPlan total, AndroidCommandRegistration registration)
    {
        var request = Read<AndroidCommandRequest>(registration.Request);
        var subject = Subject(registration.Binding.ParentJournalId, registration.Binding.ParentOperationId);
        var expected = readScope?.Derive(total, registration, subject) ?? AndroidCommandIdentity.Derive(total,
            registration.Binding, subject, registration.Sequence, registration.Purpose, registration.Action);
        if (Digest(request) != registration.RequestDigest || Digest(expected) != registration.RequestDigest) throw Invalid();
        var plan = Read<AndroidControlPlan>(registration.ControlPlan);
        if (plan != new AndroidControlPlan(AndroidControlCodec.ControlPlanFormat, 1, registration.Binding.InstanceId,
            registration.Binding.ParentJournalId, registration.Binding.ParentOperationId, registration.Binding.AttemptId,
            registration.Binding.StepId, registration.Binding.TotalPlanSha256, request.Limits)) throw Invalid();
        return request;
    }

    internal IReadOnlyList<BlobReference> References(AttemptWriterState state) =>
        ReadReferences(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);

    [SupportedOSPlatform("windows")]
    internal void RequireStopped(JsonElement journal, ExecutionPlan total, string totalSha256, string stepId)
    {
        AndroidCollectedCodec.RequireResolved(journal, total, totalSha256, stepId);
        var registrations = AndroidControlCodec.Read(journal, total, totalSha256)
            .Where(item => item.Binding.StepId == stepId).ToArray();
        foreach (var intent in AndroidCollectedCodec.Read(journal, total, totalSha256).Where(item => item.Binding.StepId == stepId))
        {
            _ = new AndroidResultEvidence(instance, database, contracts)
                .RequireCollected(journal, total, totalSha256, intent, verifyContent: true);
            var document = Read<AndroidCollectedDocument>(intent.Artifact);
            for (var index = 0; index < registrations.Length; index++)
            {
                var registration = registrations[index];
                var collection = Collection(total, registration, document.Commands[index].Collection, verifyContent: false);
                if (collection.Retention is not null) continue;
                var creator = TargetRootFacts.Creator(database, registration.Binding);
                if (creator != JournalWriterStore.CurrentWriter.Process && WriterStopEvidence.ObserveProcess(creator) != WriterState.Stopped ||
                    WriterStopEvidence.ObserveJob(registration.Binding.JobName, registration.Binding.SessionId) == WriterState.Running ||
                    collection.Inventory.AllFamily.Any(member => WriterStopEvidence.ObserveProcess(member) != WriterState.Stopped))
                    throw new IOException("execution_android_writer_stop_unconfirmed");
            }
        }
    }

    internal IReadOnlyList<BlobReference> ReadReferences(System.Text.Json.JsonElement journal, ExecutionPlan total, string totalSha256) =>
        ExecutionInvocationCodec.Views(journal).Select(ExecutionInvocationCodec.Current)
            .SelectMany(view => ReadCurrentReferences(view, total, totalSha256)).Distinct().ToArray();

    private IReadOnlyList<BlobReference> ReadCurrentReferences(System.Text.Json.JsonElement journal, ExecutionPlan total, string totalSha256)
    {
        var references = new List<BlobReference>();
        var prior = new Dictionary<string, AndroidCommandRegistration>(StringComparer.Ordinal);
        foreach (var registration in AndroidControlCodec.Read(journal, total, totalSha256))
        {
            _ = Request(total, registration);
            references.Add(registration.Request); references.Add(registration.ControlPlan);
            if (prior.TryGetValue(registration.Binding.StepId, out var previous))
            {
                var observation = Observation(total, previous, registration.PreviousObservation!, verifyContent: false);
                references.Add(registration.PreviousObservation!);
                references.AddRange(observation.Parts.Select(part => part.Content));
            }
            prior[registration.Binding.StepId] = registration;
        }
        var results = new AndroidResultEvidence(instance, database, contracts, this);
        foreach (var startup in AndroidStartupCodec.Read(journal, total, totalSha256))
            references.AddRange(results.RequireStartup(journal, total, totalSha256, startup, verifyContent: false));
        foreach (var collected in AndroidCollectedCodec.Read(journal, total, totalSha256))
            references.AddRange(results.RequireCollected(journal, total, totalSha256, collected, verifyContent: false));
        return references.Distinct().ToArray();
    }

    internal long OutputBytes(System.Text.Json.JsonElement journal, ExecutionPlan total, string totalSha256, string stepId)
    {
        var registrations = AndroidControlCodec.Read(journal, total, totalSha256)
            .Where(item => item.Binding.StepId == stepId).ToArray();
        var observations = new Dictionary<Guid, BlobReference>();
        for (var index = 1; index < registrations.Length; index++)
            Add(registrations[index - 1].Binding.TargetId, registrations[index].PreviousObservation!);
        foreach (var startup in AndroidStartupCodec.Read(journal, total, totalSha256).Where(item => item.Binding.StepId == stepId))
            foreach (var command in Read<AndroidStartupDocument>(startup.Artifact).Commands)
                Add(command.Registration.Binding.TargetId, command.Observation);
        foreach (var collected in AndroidCollectedCodec.Read(journal, total, totalSha256).Where(item => item.Binding.StepId == stepId))
        {
            var document = Read<AndroidCollectedDocument>(collected.Artifact);
            foreach (var command in document.Commands)
                Add(command.Binding.TargetId, Collection(total, registrations.Single(item => item.Binding == command.Binding),
                    command.Collection, verifyContent: false).Observation);
        }
        long bytes = 0;
        foreach (var (id, reference) in observations)
            bytes = checked(bytes + Observation(total, registrations.Single(item => item.Binding.TargetId == id), reference,
                verifyContent: false).Parts.Sum(part => part.Content.SizeBytes));
        return bytes;

        void Add(Guid id, BlobReference reference)
        {
            if (observations.TryGetValue(id, out var previous) && previous != reference) throw Invalid();
            observations[id] = reference;
        }
    }

    internal AndroidCommandObservation Observation(AttemptWriterState state,
        AndroidCommandRegistration registration, BlobReference reference, bool verifyContent)
    {
        return Observation(state.Total, registration, reference, verifyContent);
    }

    internal AndroidCommandObservation Observation(ExecutionPlan total,
        AndroidCommandRegistration registration, BlobReference reference, bool verifyContent)
    {
        var request = Request(total, registration);
        var value = Read<AndroidCommandObservation>(reference);
        if (value.Format != AndroidControlCodec.ObservationFormat || value.Version != 1 ||
            value.Binding != registration.Binding || value.RequestDigest != registration.RequestDigest || value.Parts.IsDefault ||
            !Enum.IsDefined(value.Completion) || value.Completion == AndroidResponseCompletion.NaturalRootExitAndEofBeforeStop &&
                (!value.StartReturned || value.Root is null || value.ExitCode is null || !value.OutputComplete) ||
            value.Completion == AndroidResponseCompletion.OwnedLaunchHandoff && (!request.OwnedHandoff ||
                request.Action != ExecutionAndroidAction.LaunchDevice || request.Purpose != AndroidCommandPurpose.Startup ||
                !value.StartReturned || value.Root is null || value.ExitCode is not null || !value.OutputComplete ||
                value.Parts.Length != 0 || value.Engine != EngineOutcome.Unknown) ||
            value.Parts.Length > 4 || value.Parts.Any(part => part is null || !Enum.IsDefined(part.Source)) ||
            value.StartReturned && (value.Root is null || value.ElapsedMilliseconds is null) ||
            !value.StartReturned && value.ElapsedMilliseconds is not null || value.ElapsedMilliseconds is < 0 ||
            value.Root is null && (value.ExitCode is not null || value.Parts.Length != 0) ||
            value.Root is not null && (!StringComparer.OrdinalIgnoreCase.Equals(value.Root.ImagePath, request.Tool.Path) ||
                value.Root.ImageSha256 != request.Tool.Sha256) ||
            (value.Root is null ? value.Engine is not (EngineOutcome.NotStarted or EngineOutcome.Unknown) :
                value.Engine != Engine(value.Root, value.ExitCode, value.StartReturned))) throw Invalid();
        var root = readScope is null ? TargetRootFacts.ReadCommand(database, registration) :
            readScope.ReadSql(database, connection => TargetRootFacts.ReadCommandInside(connection, null, registration));
        if (value.Root != root) throw Invalid();
        if (value.Root is not null) TargetJobEvidence.RequireIdentity(value.Root);
        long length = 0;
        foreach (var part in value.Parts)
        {
            Content(part.Content); length = checked(length + part.Content.SizeBytes);
            if (verifyContent) _blobs.Verify(part.Content);
        }
        if (length > request.Limits.MaximumCommandRawBytes || value.Parts.GroupBy(part => part.Source).Any(group =>
                group.SkipLast(1).Any(part => part.Content.SizeBytes != 1048576))) throw Invalid();
        return value;
    }

    internal AndroidCommandCollection Collection(AttemptWriterState state,
        AndroidCommandRegistration registration, BlobReference reference, bool verifyContent)
    {
        return Collection(state.Total, registration, reference, verifyContent);
    }

    internal AndroidCommandCollection Collection(ExecutionPlan total,
        AndroidCommandRegistration registration, BlobReference reference, bool verifyContent)
    {
        var value = Read<AndroidCommandCollection>(reference);
        if (value.Format != AndroidControlCodec.CollectionFormat || value.Version != 1 ||
            value.Binding != registration.Binding || value.RequestDigest != registration.RequestDigest) throw Invalid();
        var observation = Observation(total, registration, value.Observation, verifyContent);
        TargetJobEvidence.RequireInventory(value.Inventory);
        if (value.Retention is not null)
        {
            _ = AndroidRetentionEvidence.Require(this, total, registration, value);
            return value;
        }
        if (value.Inventory.ActiveProcesses != 0 || value.Inventory.LivingFamily.Length != 0 ||
            value.Inventory.LimitFlags != 0x00002000 ||
            (observation.Root is null ? observation.Completion != AndroidResponseCompletion.Unverified ||
                observation.Engine != (value.Inventory.TotalProcesses == 0 ? EngineOutcome.NotStarted : EngineOutcome.Unknown) :
                !value.Inventory.AllFamily.Contains(observation.Root))) throw Invalid();
        return value;
    }

    internal (string StandardOutput, string StandardError) ReadText(AndroidCommandObservation observation)
    {
        if (!observation.OutputComplete) throw new IOException("execution_android_command_output_incomplete");
        string Text(ProcessOutputSource source)
        {
            using var bytes = new MemoryStream();
            foreach (var part in observation.Parts.Where(part => part.Source == source))
            { using var input = _blobs.OpenVerified(part.Content); input.CopyTo(bytes); }
            return new UTF8Encoding(false, true).GetString(bytes.ToArray());
        }
        return (Text(ProcessOutputSource.StandardOutput), Text(ProcessOutputSource.StandardError));
    }

    internal T Read<T>(BlobReference reference) where T : class
    {
        StepEvidenceCodec.Plan(reference);
        using var input = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        return readScope is null ? AndroidControlCodec.Decode<T>(bytes) : readScope.Decode<T>(reference, bytes);
    }

    internal string Digest(AndroidCommandRequest request) =>
        readScope?.Digest(request) ?? AndroidCommandIdentity.Digest(request);

    internal string Subject(AttemptWriterState state) => Subject(state.Journal.JournalId, state.Journal.OperationId);

    private string Subject(Guid journalId, Guid operationId)
    {
        if (readScope is not null) return readScope.ReadSql(database, connection => SubjectInside(connection, journalId, operationId));
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        return SubjectInside(connection, journalId, operationId);
    }

    private static string SubjectInside(SqliteConnection connection, Guid journalId, Guid operationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT subject_id FROM main.operations WHERE operation_id=$operation AND operation_kind='execution.start' AND initial_journal_id=$journal;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        return command.ExecuteScalar() as string ?? throw Invalid();
    }

    private static EngineOutcome Engine(ProcessIdentity? root, int? code, bool returned) => root is null
        ? EngineOutcome.NotStarted : !returned || code is null ? EngineOutcome.Unknown :
            code == 0 ? EngineOutcome.Succeeded : EngineOutcome.Failed;

    private static void Content(BlobReference reference)
    {
        if (reference.SizeBytes is < 1 or > 1048576 || !NexusPipeline.Core.Configuration.FileTreePolicy.Digest(reference.Sha256) ||
            reference.RelativePath != "blobs/content/" + reference.Sha256 + ".bin") throw Invalid();
    }
    private static IOException Invalid() => new("execution_android_command_evidence_unbound");
}

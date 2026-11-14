using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Records;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class PlanReferenceClosure(RuntimeInstance instance, ContractReader contracts, ImmutableBlobStore blobs, StateDatabase database)
{
    internal IReadOnlyList<BlobReference> Read(RegisteredJournal registered, BlobReference reference,
        bool? retiredContent = null, ReferenceReadBudget? budget = null)
    {
        var cacheable = registered.Phase == JournalPhase.Committed && !(retiredContent ?? HasRetiredContent(registered.JournalId));
        if (cacheable && database.ReferenceFacts.Find(registered) is { } saved) return saved;
        using var measured = budget?.Enter();
        var result = ReadCurrent(registered, reference).Where(item => item.RelativePath.StartsWith("blobs/content/", StringComparison.Ordinal)).ToArray();
        if (cacheable) database.ReferenceFacts.Store(registered, result);
        return result;
    }

    private bool HasRetiredContent(Guid journal)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var command = DatabaseConnections.Command(connection, null,
            "SELECT EXISTS(SELECT 1 FROM records.record_content_retirements r JOIN records.attempt_receipts a USING(run_id) WHERE a.journal_id=$0);",
            journal.ToString("D"));
        return (long)command.ExecuteScalar()! != 0;
    }

    private IReadOnlyList<BlobReference> ReadCurrent(RegisteredJournal registered, BlobReference reference)
    {
        using var reads = new HistoryReadScope(instance, database, contracts);
        using var parents = JournalReadScope.HoldParents(instance, contracts);
        if (registered.Phase != JournalPhase.Committed &&
            Directory.Exists(Path.Combine(instance.RuntimeRoot, "journals", registered.JournalId.ToString("D"))))
            JournalReadScope.SelectParent(instance, contracts, registered.JournalId);
        // GC holds a DELETE-access guard on its candidate; reference discovery must read plans without reopening content.
        var attempt = ReadPlan<RunAttemptPlan>(reference, "run-attempt-plan.schema.json");
        if (registered.Kind != JournalKind.RunAttempt || attempt.InstanceId != instance.InstanceId.ToString("D") ||
            attempt.JournalId != registered.JournalId.ToString("D") || attempt.OperationId != registered.OperationId.ToString("D") ||
            !attempt.ResourceKeys.SequenceEqual(registered.ResourceKeys)) throw new IOException("execution_reference_registration_unbound");
        var total = ReadPlan<ExecutionPlan>(AttemptPlanCodec.Reference(attempt.ExecutionPlan), "execution-plan.schema.json");
        if (total.InstanceId != attempt.InstanceId || total.RunId != attempt.RunId || !total.ResourceKeys.SequenceEqual(attempt.ResourceKeys))
            throw new IOException("execution_reference_total_unbound");
        if (!ExecutionPlanResources.Derive(total).SequenceEqual(total.ResourceKeys)) throw new IOException("execution_reference_total_unbound");
        var definition = total.DefinitionBundle;
        if (definition.RelativePath != $"blobs/content/{definition.Sha256}.bin") throw new IOException("execution_reference_definition_unbound");
        var contents = new List<BlobReference> { new(definition.RelativePath, definition.SizeBytes, definition.Sha256) };
        contents.AddRange(ExecutionTargetInputs.MappingReferences(total, blobs, verifyContent: false));
        contents.AddRange(ExecutionProviderBinding.References(total));
        if (registered.Phase != JournalPhase.Committed)
            foreach (var archive in total.Steps.Select(step => step.Provider?.PackageArchive).OfType<ExecutionPlanInterpreterImage>())
            {
                var relative = $"blobs/content/{archive.Sha256}.bin";
                if (string.Equals(Path.GetFullPath(Path.Combine(instance.RuntimeRoot, relative)), archive.Path, StringComparison.OrdinalIgnoreCase))
                    contents.Add(new(relative, archive.SizeBytes, archive.Sha256));
            }
        contents.AddRange(ExecutionAdapterBinding.References(total, blobs));
        var expected = total.Steps.SelectMany(step => step.ConfigurationTargets.Select(path => (step.StepId, Path: path))).ToHashSet();
        var actual = new HashSet<(string, string)>();
        foreach (var item in attempt.ConfigurationPlans)
        {
            var codec = new ConfigurationPlanCodec(instance, contracts, blobs);
            var child = codec.ReadStructure(AttemptPlanCodec.Reference(item.Plan));
            if (child.Kind != JournalKind.ConfigExchange || !actual.Add((item.StepId, child.TargetPath))) throw new IOException("execution_reference_configuration_unbound");
            if (child.RunState is null) throw new IOException("run_attempt_configuration_state_required");
            RunConfigurationBinding.Require(new RunStateCodec(instance, contracts, blobs).ReadStructure(child.RunState),
                item.StepId, child.TargetPath, attempt, total, blobs, contracts);
            contents.AddRange(codec.References(child));
        }
        if (!expected.SetEquals(actual) || !total.Steps.SelectMany(step => step.LogTargets).Select(log => log.ScopePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(attempt.InitialLogs.Select(log => log.Path))) throw new IOException("execution_reference_coverage_invalid");
        foreach (var log in attempt.InitialLogs)
        {
            var objects = Array.AsReadOnly(log.OriginalObjects.Select(item => new PhysicalTreeObject(item.RelativePath, FileTreeProtocol.FromWire(item.Identity))).ToArray());
            var tree = FileTreeProtocol.FromWire(log.Before, objects);
            FileTreeArchive.ValidateArchive(tree);
            contents.AddRange(tree.Contents);
        }
        var directory = Path.Combine(instance.RuntimeRoot, "journals", registered.JournalId.ToString("D"));
        if (registered.Phase == JournalPhase.Committed && !Directory.Exists(directory))
            throw new IOException("execution_reference_history_missing");
        if (Directory.Exists(directory))
        {
            var terminalOnly = registered.Phase == JournalPhase.Committed && total.CaptureRuntime is null;
            if (!terminalOnly) JournalReadScope.SelectParent(instance, contracts, registered.JournalId);
            var document = terminalOnly
                ? CommittedAttemptJournal.Read(instance, database, contracts, registered, attempt)
                : new JournalStore(instance, contracts).Read(registered.JournalId);
            var captures = document.Root.GetProperty("participants").EnumerateArray().Any(ExecutionCaptureCodec.IsParticipant);
            foreach (var invocation in ExecutionInvocationCodec.Read(document.Root, total))
            foreach (var referenceToChild in invocation.ConfigurationPlans)
            {
                var codec = new ConfigurationPlanCodec(instance, contracts, blobs);
                var child = codec.ReadStructure(referenceToChild);
                if (child.RunState is null) throw new IOException("run_attempt_configuration_state_required");
                RunConfigurationBinding.Require(new RunStateCodec(instance, contracts, blobs).ReadStructure(child.RunState),
                    invocation.StepId, child.TargetPath, attempt, total, blobs, contracts, laterInvocation: true, taskRetry: invocation.TaskRetry);
                contents.Add(referenceToChild); contents.AddRange(codec.References(child));
            }
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                if (LogCatalogRows.Exists(connection, transaction, Guid.Parse(attempt.AttemptId)))
                {
                    var receipt = AttemptReceiptStore.ReadInside(connection, transaction, Guid.Parse(attempt.AttemptId)) ?? throw new IOException("record_log_catalog_unbound");
                    contents.AddRange(new ExecutionLogReader(instance, database, contracts)
                        .CaptureReferenceCatalog(receipt, registered, attempt, total, document).Entries.Select(entry => entry.Raw));
                }
                transaction.Commit();
            }
            ExecutionCaptureInputs.Require(total);
            if (captures)
            {
                JournalReadScope.SelectParent(instance, contracts, registered.JournalId);
                var history = new JournalStore(instance, contracts).ReadAllRevisions(registered.JournalId);
                if (!history[^1].HasSameBytes(document)) throw new IOException("execution_reference_history_changed");
                contents.AddRange(new CaptureEvidenceReader(instance, database, contracts).ReadReferences(registered, attempt, total, history));
            }
            foreach (var job in WriterIntentCodec.ReadAllJobs(document.Root, total).Where(job => job.Role == "process"))
                if (job.FileLogBaseline is { } baseline)
                {
                    contents.Add(baseline);
                    contents.AddRange(LogBaselineCodec.Read(blobs, baseline, registered.JournalId, job.StepId, job.JobName,
                        total.Steps.Single(step => step.StepId == job.StepId).LogTargets).Candidates
                        .Where(candidate => candidate.Checkpoint is not null).Select(candidate => candidate.Checkpoint!));
                }
            contents.AddRange(new TargetJobEvidence(instance, database, contracts).RequireAll(document.Root, total, attempt.ExecutionPlan.Sha256));
            contents.AddRange(new ProviderLauncherEvidence(instance, database, contracts).RequireAll(document.Root, total, attempt.ExecutionPlan.Sha256, verifyContents: false));
            contents.AddRange(new PcStageEvidence(instance, database, contracts).RequireAll(document.Root, total, attempt.ExecutionPlan.Sha256));
            contents.AddRange(new AndroidControlEvidence(instance, database, contracts).ReadReferences(document.Root, total, attempt.ExecutionPlan.Sha256));
            _ = StepResultCodec.Coverage(document.Root, total);
            foreach (var provider in ProviderTerminalCodec.ReadHistory(document.Root, total))
                contents.AddRange(new ProviderControlReader(instance, database, contracts).ReadValidatedReferences(provider.Binding, provider.Artifact,
                    new StepEvidenceReader(instance, database, contracts).ContextFromValidatedRead(provider.Binding, registered, attempt, total, document),
                    attempt.AttemptOrdinal));
            contents.AddRange(new RetryHookEvidence(instance, database, contracts).RequireAll(attempt, total, document.Root, verifyContents: false));
            foreach (var hook in HookResultCodec.ReadHistory(document.Root, total))
                contents.AddRange(RecordRetirementContents.RetainedReferences(instance, database, contracts, hook.Binding, hook.Role, hook.Artifact,
                    new HookEvidenceReader(instance, database, contracts).ReadValidatedReferences(hook.Binding, hook.Role, hook.Artifact,
                        new StepEvidenceReader(instance, database, contracts)
                            .ContextFromValidatedRead(hook.Binding, registered, attempt, total, document, hook.Role))));
            foreach (var result in StepResultCodec.ReadAll(document.Root))
                contents.AddRange(RecordRetirementContents.RetainedReferences(instance, database, contracts, result.Binding, "process", result.Artifact,
                    new StepEvidenceReader(instance, database, contracts).ReadValidatedReferences(result.Binding, result.Artifact,
                        new StepEvidenceReader(instance, database, contracts)
                            .ContextFromValidatedRead(result.Binding, registered, attempt, total, document))));
        }
        return Array.AsReadOnly(contents.Distinct().ToArray());
    }

    private T ReadPlan<T>(BlobReference reference, string schema)
    {
        if (reference.SizeBytes is < 1 or > StrictJson.MaximumBytes || reference.RelativePath != $"blobs/plans/{reference.Sha256}.json")
            throw new IOException("execution_reference_plan_invalid");
        using var input = blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return contracts.Read<T>(bytes, schema);
    }
}

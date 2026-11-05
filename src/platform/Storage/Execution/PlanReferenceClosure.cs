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
    internal IReadOnlyList<BlobReference> Read(RegisteredJournal registered, BlobReference reference)
    {
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
        if (Directory.Exists(directory))
        {
            var history = new JournalStore(instance, contracts).ReadAllRevisions(registered.JournalId);
            var document = history[^1];
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
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                if (LogCatalogRows.Exists(connection, transaction, Guid.Parse(attempt.AttemptId)))
                {
                    var receipt = AttemptReceiptStore.ReadInside(connection, transaction, Guid.Parse(attempt.AttemptId)) ?? throw new IOException("record_log_catalog_unbound");
                    contents.AddRange(new ExecutionLogReader(instance, database, contracts).CaptureCatalog(receipt).Entries.Select(entry => entry.Raw));
                }
                transaction.Commit();
            }
            contents.AddRange(new CaptureEvidenceReader(instance, database, contracts).ReadReferences(registered, attempt, total, history));
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
                contents.AddRange(new ProviderControlReader(instance, database, contracts).ReadReferences(provider.Binding, provider.Artifact));
            contents.AddRange(new RetryHookEvidence(instance, database, contracts).RequireAll(attempt, total, document.Root, verifyContents: false));
            foreach (var hook in HookResultCodec.ReadHistory(document.Root, total))
                contents.AddRange(RecordRetirementContents.RetainedReferences(instance, database, contracts, hook.Binding, hook.Role, hook.Artifact,
                    new HookEvidenceReader(instance, database, contracts).ReadReferences(hook.Binding, hook.Role, hook.Artifact)));
            foreach (var result in StepResultCodec.ReadAll(document.Root))
                contents.AddRange(RecordRetirementContents.RetainedReferences(instance, database, contracts, result.Binding, "process", result.Artifact,
                    new StepEvidenceReader(instance, database, contracts).ReadReferences(result.Binding, result.Artifact)));
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

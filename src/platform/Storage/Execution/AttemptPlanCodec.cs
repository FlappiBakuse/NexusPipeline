using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record AttemptMetadataPlan(RunAttemptPlan Plan, ExecutionPlan Total);

public sealed class AttemptPlanCodec(RuntimeInstance instance, ContractReader contracts, ImmutableBlobStore blobs)
{
    public PreparedAttemptPlan Freeze(RunAttemptPlan wire)
    {
        var held = new List<IDisposable>();
        try
        {
            var frozen = Decode(JsonSerializer.SerializeToUtf8Bytes(wire), held, out var total);
            PlanInputLease.Acquire(total, held);
            ExecutionPlanResources.RequireCurrent(total);
            var reference = blobs.PersistPlan(frozen.Export());
            held.Add(blobs.OpenVerified(reference));
            return new(frozen, reference, held);
        }
        catch { Release(held); throw; }
    }

    public FrozenAttemptPlan Read(BlobReference reference)
    {
        var held = new List<IDisposable>();
        try { return ReadHeld(reference, held, out _); }
        finally { Release(held); }
    }

    internal AttemptMetadataPlan ReadMetadata(BlobReference reference)
    {
        var held = new List<IDisposable>();
        try
        {
            var plan = ReadHeld(reference, held, out var total, metadataOnly: true).ReadWire();
            return new(plan, total);
        }
        finally { Release(held); }
    }

    internal AttemptBoundInputs ReadBound(BlobReference reference)
    {
        var held = new List<IDisposable>();
        try
        {
            var plan = ReadHeld(reference, held, out var total).ReadWire();
            return new(plan, total, held);
        }
        catch { Release(held); throw; }
    }

    private FrozenAttemptPlan ReadHeld(BlobReference reference, List<IDisposable> held, out ExecutionPlan total, bool metadataOnly = false)
    {
        if (reference.SizeBytes is < 1 or > StrictJson.MaximumBytes || reference.RelativePath != $"blobs/plans/{reference.Sha256}.json")
            throw new IOException("run_attempt_reference_invalid");
        var input = blobs.OpenVerified(reference); held.Add(input);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return Decode(bytes, held, out total, metadataOnly);
    }

    private FrozenAttemptPlan Decode(byte[] bytes, List<IDisposable> held, out ExecutionPlan total, bool metadataOnly = false)
    {
        var plan = contracts.Read<RunAttemptPlan>(bytes, "run-attempt-plan.schema.json");
        foreach (var id in new[] { plan.RunId, plan.AttemptId, plan.OperationId, plan.JournalId }) ExecutionPlanSources.RequireCanonicalId(id);
        if (plan.InstanceId != instance.InstanceId.ToString("D") || Guid.Parse(plan.OperationId) == Guid.Empty || Guid.Parse(plan.JournalId) == Guid.Empty ||
            Guid.Parse(plan.AttemptId) == Guid.Empty || plan.OutputDirectory != $"runs/{plan.RunId}/{plan.AttemptId}" ||
            plan.LogDirectory != $"logs/{plan.RunId}/{plan.AttemptId}") throw new IOException("run_attempt_identity_unbound");
        if (plan.StartRequest is { } source)
        {
            var reference = Reference(source);
            if (reference.RelativePath != $"blobs/plans/{reference.Sha256}.json" || reference.SizeBytes is < 1 or > StrictJson.MaximumBytes)
                throw StartRequestCodec.Invalid();
            var input = blobs.OpenVerified(reference); held.Add(input);
            var contents = new byte[checked((int)input.Length)]; input.ReadExactly(contents);
            var saved = StartRequestCodec.Decode(contents);
            if (saved.Document.RunId.ToString("D") != plan.RunId || saved.Document.JournalId.ToString("D") != plan.JournalId ||
                saved.Document.OperationId.ToString("D") != plan.OperationId ||
                (saved.Document.RetrySourceOperationId is null) != (plan.AttemptOrdinal == 1))
                throw StartRequestCodec.Invalid();
        }
        var totalReference = Reference(plan.ExecutionPlan);
        var totalCodec = new ExecutionPlanCodec(instance, contracts, blobs);
        total = metadataOnly ? totalCodec.ReadMetadataHeld(totalReference, held) : totalCodec.ReadHeld(totalReference, held).ReadWire();
        if (plan.RunId != total.RunId || plan.AttemptOrdinal > total.RetryPolicy.MaximumAttempts || !plan.ResourceKeys.SequenceEqual(total.ResourceKeys))
            throw new IOException("run_attempt_total_plan_unbound");
        if (plan.AttemptOrdinal > 1 && (total.RetryPolicy.Safety != "safe" || !total.RetryPolicy.HostApproved))
            throw new IOException("execution_retry_not_authorized");
        ValidateConfigurations(plan, total, held, metadataOnly);
        ValidateLogs(plan, total, held, metadataOnly);
        return new(bytes);
    }

    private void ValidateConfigurations(RunAttemptPlan plan, ExecutionPlan total, List<IDisposable> held, bool metadataOnly)
    {
        var expected = total.Steps.SelectMany(step => step.ConfigurationTargets.Select(path => (step.StepId, Path: path))).ToHashSet();
        var actual = new HashSet<(string, string)>();
        var journals = new HashSet<Guid> { Guid.Parse(plan.JournalId) };
        var operations = new HashSet<Guid> { Guid.Parse(plan.OperationId) };
        var codec = new ConfigurationPlanCodec(instance, contracts, blobs);
        foreach (var configuration in plan.ConfigurationPlans)
        {
            var reference = Reference(configuration.Plan);
            var child = codec.ReadHeld(reference, held, metadataOnly, out var state);
            if (child.Kind != JournalKind.ConfigExchange || !actual.Add((configuration.StepId, child.TargetPath)) ||
                !journals.Add(child.JournalId) || !operations.Add(child.OperationId)) throw new IOException("run_attempt_configuration_unbound");
            if (state is null) throw new IOException("run_attempt_configuration_state_required");
            RunConfigurationBinding.Require(state,
                configuration.StepId, child.TargetPath, plan, total, blobs, contracts);
        }
        if (!expected.SetEquals(actual)) throw new IOException("run_attempt_configuration_coverage_invalid");
    }

    private void ValidateLogs(RunAttemptPlan plan, ExecutionPlan total, List<IDisposable> held, bool metadataOnly)
    {
        var expected = total.Steps.SelectMany(step => step.LogTargets).Select(log => log.ScopePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in plan.InitialLogs)
        {
            if (PhysicalPathPolicy.NormalizeSyntax(log.Path) != log.Path || !actual.Add(log.Path)) throw new IOException("run_attempt_log_unbound");
            var objects = Array.AsReadOnly(log.OriginalObjects.Select(item => new PhysicalTreeObject(item.RelativePath, FileTreeProtocol.FromWire(item.Identity))).ToArray());
            var tree = FileTreeProtocol.FromWire(log.Before, objects);
            FileTreeArchive.ValidateArchive(tree);
            foreach (var target in total.Steps.SelectMany(step => step.LogTargets).Where(target => target.ScopePath == log.Path))
                if (target.Selector == "file" && tree.Snapshot.Shape == FileObjectKind.Directory ||
                    target.Selector != "file" && tree.Snapshot.Shape == FileObjectKind.File ||
                    target.Selector == "latestFile" && tree.Snapshot.Shape == FileObjectKind.Missing)
                    throw new IOException("run_attempt_log_scope_shape_unbound");
            if (objects.Count != tree.Snapshot.Entries.Count || objects.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != objects.Count ||
                objects.Any(item => item.Identity.VolumeSerial != log.ParentIdentity.VolumeSerial || !tree.Snapshot.Entries.Any(entry => entry.RelativePath == item.RelativePath)))
                throw new IOException("run_attempt_log_identity_incomplete");
            if (!metadataOnly) foreach (var content in tree.Contents) held.Add(blobs.OpenVerified(content));
        }
        if (!expected.SetEquals(actual)) throw new IOException("run_attempt_log_coverage_invalid");
    }

    internal static BlobReference Reference(ExecutionPlanPlanReference value) => new(value.RelativePath, value.SizeBytes, value.Sha256);
    private static void Release(List<IDisposable> held)
    {
        for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
    }
}

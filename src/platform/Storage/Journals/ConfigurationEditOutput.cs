using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Text.Json;

namespace NexusPipeline.Platform.Storage.Journals;

internal static class ConfigurationOutputEvidence
{
    internal static void Require(PreparedConfigurationExchange source, ArchivedFileTree output)
    {
        var plan = source.Plan;
        var policy = plan.EditPolicy ?? throw new IOException("configuration_editor_write_policy_required");
        var objects = output.OriginalObjects;
        FileTreeArchive.ValidateArchive(output); var canonical = FileTreePolicy.Freeze(output.Snapshot);
        var originalIdentities = plan.Before.OriginalObjects.Select(item => item.Identity).ToHashSet();
        if (!canonical.Entries.SequenceEqual(output.Snapshot.Entries) || objects.Count != canonical.Entries.Count ||
            !objects.Select(item => item.RelativePath).SequenceEqual(canonical.Entries.Select(item => item.RelativePath)) ||
            objects.Select(item => item.Identity).Distinct().Count() != objects.Count || objects.Any(item => item.Identity == source.WorkIdentity ||
                item.Identity.VolumeSerial != plan.ParentIdentity.VolumeSerial || originalIdentities.Contains(item.Identity)))
            throw new IOException("configuration_edit_output_objects_invalid");
        EditWriteRules.RequireOutput(policy, plan.Candidate.Snapshot, output.Snapshot);
        var outputObjects = objects.ToHashSet();
        foreach (var original in source.CandidateObjects.Where(item => !EditWriteRules.Allows(policy, item.RelativePath)))
            if (!outputObjects.Contains(original)) throw new IOException("configuration_edit_output_identity_scope_exceeded");
    }
}

internal sealed class EditOutputCodec(RuntimeInstance instance, ContractReader contracts, ImmutableBlobStore blobs)
{
    internal BlobReference Write(PreparedConfigurationExchange source, string sourceDigest, ArchivedFileTree output, BlobReference writerStop,
        StateDatabase? database = null, AttemptWriterState? verifiedParent = null)
    {
        var wire = new ConfigurationEditOutput
        {
            Format = "nexus.configuration.edit-output", Version = 1, InstanceId = instance.InstanceId.ToString("D"),
            OperationId = source.Plan.OperationId.ToString("D"), JournalId = source.Plan.JournalId.ToString("D"), SourcePlanDigest = sourceDigest,
            WorkIdentity = FileTreeProtocol.ToWire(source.WorkIdentity), Output = FileTreeProtocol.ToWire(output.Snapshot),
            Objects = output.OriginalObjects.Select(item => new FileTreePhysicalObject { RelativePath = item.RelativePath, Identity = FileTreeProtocol.ToWire(item.Identity) }).ToArray(),
            WriterStop = new() { Path = writerStop.RelativePath, SizeBytes = writerStop.SizeBytes, Sha256 = writerStop.Sha256 }
        };
        var bytes = BoundedJson.Serialize(wire); _ = Decode(bytes, source, sourceDigest, database, verifiedParent); return blobs.PersistPlan(bytes);
    }

    internal PreparedConfigurationExchange Read(BlobReference reference, PreparedConfigurationExchange source, string sourceDigest,
        StateDatabase? database = null, bool verifyContents = true)
    {
        if (reference.RelativePath != $"blobs/plans/{reference.Sha256}.json" || reference.SizeBytes > StrictJson.MaximumBytes)
            throw new IOException("configuration_edit_output_reference_invalid");
        using var input = blobs.OpenVerified(reference); var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var result = Decode(bytes, source, sourceDigest, database);
        if (verifyContents) foreach (var content in result.Plan.Candidate.Contents) blobs.Verify(content);
        return result;
    }

    internal JsonElement Reference(BlobReference reference)
    {
        var bytes = BoundedJson.Serialize(new ConfigurationEditOutputReference { Format = "nexus.configuration.edit-output-reference", Version = 1,
            Path = reference.RelativePath, SizeBytes = reference.SizeBytes, Sha256 = reference.Sha256 });
        using var wire = contracts.Read(bytes, "configuration-edit-output-reference.schema.json"); return wire.RootElement.Clone();
    }

    internal BlobReference ReadReference(JsonElement value)
    {
        var reference = contracts.Read<ConfigurationEditOutputReference>(BoundedJson.Serialize(value), "configuration-edit-output-reference.schema.json");
        if (reference.Path != $"blobs/plans/{reference.Sha256}.json") throw new IOException("configuration_edit_output_reference_invalid");
        return new(reference.Path, reference.SizeBytes, reference.Sha256);
    }

    private PreparedConfigurationExchange Decode(ReadOnlyMemory<byte> bytes, PreparedConfigurationExchange source, string sourceDigest, StateDatabase? database = null, AttemptWriterState? verifiedParent = null)
    {
        var wire = contracts.Read<ConfigurationEditOutput>(bytes, "configuration-edit-output.schema.json");
        var plan = source.Plan;
        var run = plan.Kind == JournalKind.ConfigExchange && plan.RunState is not null && !plan.PhysicalClaims.IsDefaultOrEmpty;
        if (plan.Kind != JournalKind.ConfigEdit && !run || plan.EditPolicy is null || run && database is null || wire.InstanceId != instance.InstanceId.ToString("D") ||
            wire.OperationId != plan.OperationId.ToString("D") || wire.JournalId != plan.JournalId.ToString("D") || wire.SourcePlanDigest != sourceDigest ||
            FileTreeProtocol.FromWire(wire.WorkIdentity) != source.WorkIdentity) throw new IOException("configuration_edit_output_unbound");
        var objects = Array.AsReadOnly(wire.Objects.Select(item => new PhysicalTreeObject(item.RelativePath, FileTreeProtocol.FromWire(item.Identity))).ToArray());
        var output = FileTreeProtocol.FromWire(wire.Output, objects);
        ConfigurationOutputEvidence.Require(source, output);
        var stop = new BlobReference(wire.WriterStop.Path, wire.WriterStop.SizeBytes, wire.WriterStop.Sha256);
        var closure = new JobClosureCodec(instance, contracts, blobs).Read(stop);
        if (!run && closure.OperationId != plan.OperationId || !closure.JournalIds.Contains(plan.JournalId)) throw new IOException("configuration_edit_output_writer_unbound");
        if (database is not null) new JobClosureStore(instance, database, contracts).RequireReference(plan.JournalId, closure, stop, verifiedParent);
        if (!closure.WasExecuted && (plan.Candidate.Snapshot.Shape != output.Snapshot.Shape || !plan.Candidate.Snapshot.Entries.SequenceEqual(output.Snapshot.Entries) ||
            !source.CandidateObjects.SequenceEqual(objects))) throw new IOException("configuration_edit_unexecuted_writer_changed_scene");
        return source with { Plan = plan with { Candidate = output }, CandidateObjects = objects };
    }
}

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Security.Principal;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed record FrozenJobClosure(Guid InstanceId, Guid OperationId, Guid WriterId, ProcessIdentity Creator,
    string JobName, int SessionId, ImmutableArray<Guid> JournalIds, ProcessIdentity? Root, ImmutableArray<ProcessIdentity> Family,
    bool WasExecuted, DateTimeOffset ClosedAt)
{
    internal long? RunElapsedMilliseconds { get; init; }
}

internal sealed class JobClosureCodec(RuntimeInstance instance, ContractReader contracts, ImmutableBlobStore blobs)
{
    internal BlobReference Write(FrozenJobClosure value)
    {
        var wire = new WriterJobClosure
        {
            Format = "nexus.writer.job-closure", Version = 1, InstanceId = value.InstanceId.ToString("D"),
            OperationId = value.OperationId.ToString("D"), WriterId = value.WriterId.ToString("D"), JobName = value.JobName, SessionId = value.SessionId,
            JournalIds = value.JournalIds.Select(id => id.ToString("D")).ToArray(), Creator = ToWire(value.Creator),
            Root = value.Root is null ? null : ToWire(value.Root), Family = value.Family.Select(ToWire).ToArray(),
            TotalProcesses = value.Family.Length, ActiveProcesses = 0, Disposition = value.WasExecuted ? "stopped" : "notExecuted",
            ClosedAt = value.ClosedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), RunElapsedMilliseconds = value.RunElapsedMilliseconds
        };
        var bytes = BoundedJson.Serialize(wire); _ = Decode(bytes); return blobs.PersistPlan(bytes);
    }

    internal FrozenJobClosure Read(BlobReference reference)
    {
        if (reference.RelativePath != $"blobs/plans/{reference.Sha256}.json" || reference.SizeBytes > StrictJson.MaximumBytes)
            throw new IOException("writer_job_closure_reference_invalid");
        using var input = blobs.OpenVerified(reference); var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return Decode(bytes);
    }

    private FrozenJobClosure Decode(ReadOnlyMemory<byte> bytes)
    {
        var wire = contracts.Read<WriterJobClosure>(bytes, "writer-job-closure.schema.json");
        var result = new FrozenJobClosure(Guid.Parse(wire.InstanceId), Guid.Parse(wire.OperationId), Guid.Parse(wire.WriterId), FromWire(wire.Creator),
            wire.JobName, checked((int)wire.SessionId), wire.JournalIds.Select(Guid.Parse).ToImmutableArray(),
            wire.Root is null ? null : FromWire(wire.Root), wire.Family.Select(FromWire).ToImmutableArray(), wire.Disposition == "stopped",
            DateTimeOffset.Parse(wire.ClosedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            { RunElapsedMilliseconds = wire.RunElapsedMilliseconds };
        if (result.InstanceId != instance.InstanceId || result.OperationId == Guid.Empty || result.WriterId == Guid.Empty ||
            result.JournalIds.Any(id => id == Guid.Empty) || result.JournalIds.Distinct().Count() != result.JournalIds.Length ||
            wire.TotalProcesses != result.Family.Length || result.Family.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count() != result.Family.Length ||
            result.Family.Contains(result.Creator) || result.Family.IsEmpty != (result.Root is null) ||
            result.Root is not null && !result.Family.Contains(result.Root) || result.WasExecuted && result.Root is null ||
            !result.WasExecuted && (result.Family.Length > 1 || result.RunElapsedMilliseconds is > 0) ||
            result.RunElapsedMilliseconds is < 0 or > 922337203685477)
            throw new IOException("writer_job_closure_unrecognized");
        return result;
    }

    private static WriterJobClosureProcess ToWire(ProcessIdentity value) => new()
    {
        ProcessId = value.ProcessId, CreatedAtUtc = value.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture), ImagePath = value.ImagePath,
        ImageSha256 = value.ImageSha256, UserSid = value.UserSid, Elevated = value.IsElevated
    };
    private static ProcessIdentity FromWire(WriterJobClosureProcess value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var created = DateTime.Parse(value.CreatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (created.Kind != DateTimeKind.Utc || PhysicalPathPolicy.NormalizeSyntax(value.ImagePath) != value.ImagePath ||
            new SecurityIdentifier(value.UserSid).Value != value.UserSid)
            throw new IOException("writer_job_process_identity_invalid");
        return new(checked((int)value.ProcessId), created, value.ImagePath, value.ImageSha256, value.UserSid, value.Elevated);
    }
}

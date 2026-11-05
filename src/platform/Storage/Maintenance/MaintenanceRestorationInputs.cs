using System.Globalization;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed class MaintenanceRestorationInputs : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly BlobReference _plan;
    private readonly UpdateFeedVerifier _verifier;
    private List<IDisposable>? _held;

    private MaintenanceRestorationInputs(RuntimeInstance instance, BlobReference plan, UpdateFeedVerifier verifier,
        List<IDisposable> held)
    { _instance = instance; _plan = plan; _verifier = verifier; _held = held; }

    internal static MaintenanceRestorationInputs Hold(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, UpdateFeedVerifier verifier, BoundMaintenancePayload bound)
    {
        var held = new List<IDisposable>();
        try
        {
            var references = bound.Plan.Before.Contents.Concat(bound.Plan.Candidate.Contents)
                .Concat(UpdateSourceCodec.References(bound.Source))
                .Concat(bound.Physical.Select(item => item.Intent.Artifact))
                .Concat(bound.Restoration.Select(item => item.Intent.Artifact))
                .Prepend(bound.PlanReference).Prepend(bound.Plan.MaintenanceSource!).Distinct().ToArray();
            if (references.Length > checked(2 * 4096 + 32)) throw new IOException("maintenance_restoration_content_limit");
            var blobs = new ImmutableBlobStore(instance, contracts);
            foreach (var directory in references.Select(reference => Path.GetDirectoryName(
                Path.Combine(instance.RuntimeRoot, reference.RelativePath))!).Distinct(StringComparer.OrdinalIgnoreCase))
                held.Add(StableDirectoryLease.Acquire(directory));
            foreach (var reference in references) held.Add(blobs.OpenVerified(reference));
            _ = new UpdateSourceCodec(instance, contracts).RequireTrustedContents(bound.Source, bound.Plan,
                database, verifier, CancellationToken.None);
            foreach (var document in bound.History)
            {
                var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals",
                    document.JournalId.ToString("D"), document.Revision.ToString("D10", CultureInfo.InvariantCulture) + ".json"));
                var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                held.Add(input); var expected = document.Export();
                if (input.Length != expected.Length || !SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(expected)))
                    throw new IOException("maintenance_restoration_history_changed");
            }
            return new(instance, bound.PlanReference, verifier, held);
        }
        catch { for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose(); throw; }
    }

    internal void Require(RuntimeInstance instance, BlobReference plan, UpdateFeedVerifier verifier)
    {
        // Locked files and their ancestors keep the verified bytes bound to these exact paths for this scope.
        if (_held is null || instance != _instance || plan != _plan || !ReferenceEquals(verifier, _verifier))
            throw new IOException("maintenance_restoration_inputs_unbound");
    }

    public void Dispose()
    {
        var actual = Interlocked.Exchange(ref _held, null);
        if (actual is not null) for (var i = actual.Count - 1; i >= 0; i--) actual[i].Dispose();
    }
}

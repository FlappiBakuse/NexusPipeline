using Microsoft.Data.Sqlite;
using System.Runtime.ExceptionServices;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class JournalResourceClaims
{
    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, IReadOnlyList<ResourceClaim> claims)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO journal_resource_claims VALUES($journal,$resource,$mode);";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        var key = command.Parameters.Add("$resource", SqliteType.Text); var mode = command.Parameters.Add("$mode", SqliteType.Integer);
        foreach (var claim in claims)
        {
            if (!Enum.IsDefined(claim.Mode)) throw new IOException("journal_resource_claim_mode_invalid");
            key.Value = claim.ResourceKey; mode.Value = (int)claim.Mode; command.ExecuteNonQuery();
        }
    }

    internal static IReadOnlyDictionary<string, ResourceClaimMode> Read(StateDatabase database, Guid journalId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        return ReadBatch(connection, transaction, [journalId])[journalId];
    }

    private static Dictionary<Guid, Dictionary<string, ResourceClaimMode>> ReadBatch(SqliteConnection connection,
        SqliteTransaction transaction, IReadOnlyList<Guid> journals)
    {
        if (journals.Count is < 1 or > 128 || journals.Distinct().Count() != journals.Count)
            throw new IOException("journal_resource_claim_batch_invalid");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var parameters = journals.Select((id, index) =>
        {
            var name = "$journal" + index;
            command.Parameters.AddWithValue(name, id.ToString("D"));
            return name;
        });
        command.CommandText = "SELECT journal_id,resource_key,mode FROM journal_resource_claims WHERE journal_id IN (" +
            string.Join(',', parameters) + ") ORDER BY journal_id,resource_key;";
        var result = journals.ToDictionary(id => id, _ => new Dictionary<string, ResourceClaimMode>(StringComparer.Ordinal));
        using var row = command.ExecuteReader();
        while (row.Read())
        {
            var claims = result[Guid.ParseExact(row.GetString(0), "D")];
            var mode = (ResourceClaimMode)row.GetInt32(2);
            if (!Enum.IsDefined(mode) || claims.Count >= 4096) throw new IOException("journal_resource_claims_invalid");
            claims.Add(row.GetString(1), mode);
        }
        return result;
    }

    internal static void VerifyInventory(RuntimeInstance instance, StateDatabase database, ContractReader contracts, PayloadApplyRoots? payloadRoots = null)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        VerifyScope(instance, database, contracts, RecoveryJournalRegistry.ReadHistoryInside(connection, transaction), payloadRoots);
    }

    internal static void VerifyScope(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        IEnumerable<RegisteredJournal> registered, PayloadApplyRoots? payloadRoots = null)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var batch in registered.Chunk(128))
        {
            var claims = ReadBatch(connection, transaction, batch.Select(item => item.JournalId).ToArray());
            VerifyBatch(instance, database, contracts, batch, journal => Verify(journal, claims[journal.JournalId]));
        }

        void Verify(RegisteredJournal journal, IReadOnlyDictionary<string, ResourceClaimMode> actual)
        {
            if (journal.Kind == JournalKind.RunAttempt)
            {
                VerifyAttempt(instance, database, contracts, journal, actual);
                return;
            }
            if (journal.Kind == JournalKind.ExtensionApply)
            {
                var roots = payloadRoots ?? new PayloadApplyRoots(PhysicalPathPolicy.RuntimeApplicationDirectory(), Path.Combine(instance.RuntimeRoot, "extensions"));
                var reference = new BlobReference(journal.PlanPath, new FileInfo(Path.Combine(instance.RuntimeRoot, journal.PlanPath)).Length, journal.PlanDigest);
                var payload = new PayloadPlanCodec(instance, contracts, new(instance, contracts), roots).ReadStructure(reference, out var source);
                if (payload.ExtensionSource is null || source is null) throw new IOException("extension_payload_source_required");
                if (source.Claims.Length != actual.Count || source.Claims.Any(claim =>
                    !actual.TryGetValue(claim.ResourceKey, out var mode) || mode != claim.Mode))
                    throw new IOException("journal_resource_claims_unbound");
                return;
            }
            if (journal.Kind == JournalKind.FileCleanup)
            {
                var roots = payloadRoots ?? new PayloadApplyRoots(PhysicalPathPolicy.RuntimeApplicationDirectory(), Path.Combine(instance.RuntimeRoot, "extensions"));
                var cleanup = CleanupJournalBinding.ReadPlan(instance, contracts, journal, database, roots);
                if (cleanup.ExtensionWork is null && actual.Count != 0) throw new IOException("journal_resource_claim_kind_invalid");
                return;
            }
            if (journal.Kind is not (JournalKind.ConfigEdit or JournalKind.ConfigExchange))
            {
                if (actual.Count != 0) throw new IOException("journal_resource_claim_kind_invalid");
                return;
            }
            var plan = new ConfigurationPlanCodec(instance, contracts, new(instance, contracts)).ReadStructure(
                new(journal.PlanPath, new FileInfo(Path.Combine(instance.RuntimeRoot, journal.PlanPath)).Length, journal.PlanDigest));
            var controlled = plan.Editor is not null || plan.Kind == JournalKind.ConfigExchange && plan.RunState is not null && plan.EditPolicy is not null && !plan.PhysicalClaims.IsDefaultOrEmpty;
            var expected = !controlled ? new Dictionary<string, ResourceClaimMode>(StringComparer.Ordinal) :
                journal.ResourceKeys.ToDictionary(key => key, key => key is "automation:definitions" or "blobs:content" ? ResourceClaimMode.Reference : ResourceClaimMode.Mutation, StringComparer.Ordinal);
            foreach (var claim in plan.PhysicalClaims)
            {
                if (!expected.ContainsKey(claim.ResourceKey)) throw new IOException("configuration_physical_claims_unregistered");
                expected[claim.ResourceKey] = claim.Mode;
            }
            if (expected.Count != actual.Count || expected.Any(item => !actual.TryGetValue(item.Key, out var mode) || mode != item.Value))
                throw new IOException("journal_resource_claims_unbound");
        }
    }

    private static void VerifyBatch(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        RegisteredJournal[] batch, Action<RegisteredJournal> verify)
    {
        ExceptionDispatchInfo? failure = null;
        Parallel.ForEach(batch, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            () => new HistoryReadScope(instance, database, contracts),
            (journal, loop, scope) =>
            {
                try { verify(journal); }
                catch (Exception error)
                {
                    Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(error), null);
                    loop.Stop();
                }
                return scope;
            }, scope => scope.Dispose());
        failure?.Throw();
    }

    internal static void VerifyAttempt(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        RegisteredJournal journal, IReadOnlyDictionary<string, ResourceClaimMode>? actual = null)
    {
        actual ??= Read(database, journal.JournalId);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var reference = new BlobReference(journal.PlanPath, new FileInfo(Path.Combine(instance.RuntimeRoot, journal.PlanPath)).Length, journal.PlanDigest);
        using var bound = new AttemptPlanCodec(instance, contracts, blobs).ReadBound(reference);
        VerifyAttempt(journal, bound.Plan, bound.Total, actual);
    }

    internal static void VerifyAttempt(RegisteredJournal journal, RunAttemptPlan attempt, ExecutionPlan total,
        IReadOnlyDictionary<string, ResourceClaimMode> actual)
    {
        var claims = ExecutionPlanResources.Claims(total);
        if (journal.Kind != JournalKind.RunAttempt || attempt.OperationId != journal.OperationId.ToString("D") ||
            attempt.JournalId != journal.JournalId.ToString("D") || !claims.Select(claim => claim.ResourceKey).SequenceEqual(journal.ResourceKeys) ||
            claims.Length != actual.Count || claims.Any(claim => !actual.TryGetValue(claim.ResourceKey, out var mode) || mode != claim.Mode))
            throw new IOException("journal_resource_claims_unbound");
    }
}


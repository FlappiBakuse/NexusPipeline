using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class ScopedConfigurationRecovery(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal IReadOnlyList<ResourceIsolation> Project(RecoveryRegistrySnapshot verified)
    {
        var scopes = new Dictionary<Guid, IReadOnlyDictionary<string, Claim>>();
        foreach (var parent in verified.Journals.Where(journal => journal.Kind == JournalKind.RunAttempt &&
                     journal.Phase is JournalPhase.Quarantined or JournalPhase.Applied))
        {
            var scope = ReadRun(parent);
            if (scope is null) continue;
            foreach (var (id, claims) in scope) scopes.Add(id, claims);
        }
        if (scopes.Count == 0) return verified.Isolations;
        var result = new List<ResourceIsolation>();
        foreach (var isolation in verified.Isolations)
        {
            if (!scopes.TryGetValue(isolation.JournalId, out var scope)) result.Add(isolation);
            else if (scope.TryGetValue(isolation.ResourceKey, out var claim))
                result.Add(isolation with { ReasonCode = claim.Reason, Mode = claim.Mode, CanOrdinaryExit = true });
        }
        return result;
    }

    internal bool IsStoppedConfigurationFault(AttemptWriterState state)
    {
        if (state.Latest.Phase is not (JournalPhase.Quarantined or JournalPhase.Applied) || state.Journal.Phase != state.Latest.Phase ||
            state.Journal.Revision != state.Latest.Revision || !HasReason(state.Latest, "attempt_configuration_not_restored") ||
            state.Attempt.ConfigurationPlans.Count == 0)
            return false;
        try
        {
            AttemptTerminalEvidence.RequireStopped(database, state.Journal.JournalId);
            AttemptTerminalEvidence.RequireCollected(database, state);
            return true;
        }
        catch (IOException) { return false; }
    }

    internal static bool CanDeferScene(Exception error) => error is FileNotFoundException or DirectoryNotFoundException or
        UnauthorizedAccessException or System.ComponentModel.Win32Exception ||
        error is IOException io && (io.Message.StartsWith("file_tree_lease_", StringComparison.Ordinal) ||
            io.Message.StartsWith("directory_lease_", StringComparison.Ordinal) ||
            io.Message is "configuration_tree_or_identity_changed" or "configuration_work_identity_changed" or
                "configuration_work_unknown_entry" or "configuration_destination_not_empty" or
                "configuration_edit_journal_not_settled" or "configuration_parent_identity_changed");

    private Dictionary<Guid, IReadOnlyDictionary<string, Claim>>? ReadRun(RegisteredJournal parent)
    {
        var journals = new JournalStore(instance, contracts);
        if (!HasReason(journals.Read(parent.JournalId), "attempt_configuration_not_restored")) return null;
        var state = AttemptWriterState.Read(instance, database, contracts, parent.JournalId);
        if (!IsStoppedConfigurationFault(state)) return null;

        var references = ExecutionInvocationCodec.Plans(state.Latest.Root, state.Total, state.Attempt)
            .Select(item => AttemptPlanCodec.Reference(item.Plan)).ToHashSet();
        var plans = new ConfigurationPlanCodec(instance, contracts, new(instance, contracts));
        var registry = new RecoveryJournalRegistry(database);
        var scopes = new Dictionary<Guid, IReadOnlyDictionary<string, Claim>>();
        var parentClaims = new Dictionary<string, Claim>(StringComparer.Ordinal);
        foreach (var (childId, address) in ReadMembers(parent))
        {
            var child = registry.ReadJournal(childId);
            var reference = references.SingleOrDefault(item => item.RelativePath == child.PlanPath && item.Sha256 == child.PlanDigest);
            var slot = ConfigurationRevisionService.ResourceKey(address);
            if (reference is null || child.Kind != JournalKind.ConfigExchange || !child.ResourceKeys.Contains(slot, StringComparer.Ordinal) ||
                !parent.ResourceKeys.Contains(slot, StringComparer.Ordinal)) return null;
            var plan = plans.ReadStructure(reference, out var input);
            if (plan.JournalId != childId || plan.OperationId != child.OperationId || input?.Source.Address != address) return null;
            var detail = new Dictionary<string, Claim>(StringComparer.Ordinal);
            var reason = child.Phase == JournalPhase.Quarantined && HasReason(journals.Read(childId), ConfigurationRecoveryCodes.Lost)
                ? ConfigurationRecoveryCodes.Lost : ConfigurationRecoveryCodes.Pending;
            detail.Add(slot, new(reason, ResourceClaimMode.Mutation));
            foreach (var claim in plan.PhysicalClaims)
            {
                if (!child.ResourceKeys.Contains(claim.ResourceKey, StringComparer.Ordinal) ||
                    !parent.ResourceKeys.Contains(claim.ResourceKey, StringComparer.Ordinal)) return null;
                detail.Add(claim.ResourceKey, new(reason, claim.Mode));
            }
            foreach (var (key, claim) in detail)
            {
                var effective = claim;
                if (parentClaims.TryGetValue(key, out var prior))
                    effective = new(prior.Reason == ConfigurationRecoveryCodes.Lost ? prior.Reason : claim.Reason,
                        prior.Mode > claim.Mode ? prior.Mode : claim.Mode);
                parentClaims[key] = effective;
            }
            scopes.Add(childId, detail);
        }
        if (parentClaims.Count == 0) return null;
        scopes.Add(parent.JournalId, parentClaims);
        return scopes;
    }

    private IReadOnlyList<(Guid JournalId, ConfigurationAddress Address)> ReadMembers(RegisteredJournal parent)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true,
            domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT r.journal_id,s.binding_id,r.resource_id,s.writer_gate,s.owner_operation_id " +
            "FROM configuration.configuration_run_sessions s JOIN configuration.configuration_run_resources r USING(session_id) " +
            "WHERE s.parent_journal_id=$parent AND s.phase IN (0,1) ORDER BY r.journal_id LIMIT 4097;";
        command.Parameters.AddWithValue("$parent", parent.JournalId.ToString("D"));
        using var rows = command.ExecuteReader();
        var result = new List<(Guid, ConfigurationAddress)>();
        while (rows.Read())
        {
            if (!rows.GetBoolean(3) || rows.GetGuid(4) != parent.OperationId || result.Count == 4096) return [];
            result.Add((rows.GetGuid(0), new(rows.GetGuid(1), rows.GetString(2))));
        }
        return result;
    }

    private static bool HasReason(JournalDocument journal, string reason) =>
        journal.Root.GetProperty("receipts").EnumerateArray().Any(item =>
            item.TryGetProperty("detailCode", out var code) && code.GetString() == reason);

    private sealed record Claim(string Reason, ResourceClaimMode Mode);
}

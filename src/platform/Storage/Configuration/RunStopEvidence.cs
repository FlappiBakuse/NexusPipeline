using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed record RunStoppedFacts(TimeSpan Elapsed, bool WasExecuted);

internal sealed class RunStopEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    [SupportedOSPlatform("windows")]
    internal RunStoppedFacts ReadRestored(ConfigurationRunStatus session) => ReadClosed(session, requireRestored: true);

    [SupportedOSPlatform("windows")]
    internal RunStoppedFacts ReadClosed(ConfigurationRunStatus session, bool requireRestored = false)
    {
        if (!session.IsWriterAdmissionClosed || session.ClosedElapsedMilliseconds is not { } elapsed ||
            elapsed is < 0 or > long.MaxValue / TimeSpan.TicksPerMillisecond)
            throw new IOException("configuration_run_stop_duration_missing");
        if (ConfigurationRunBinding.Read(instance, database, contracts, session).Evidence.HasHooks)
            return ReadGroup(session, requireRestored);
        var allJobs = Jobs(session);
        var groupJobs = session.Resources.Select(resource => (Resource: resource, Jobs: allJobs[resource.JournalId])).ToArray();
        var noJobs = groupJobs.All(item => item.Jobs.Count == 0);
        if (noJobs && (session.EnteredRunning || elapsed != 0 || session.SyncOrdinal != 0)) throw new IOException("configuration_run_unstarted_evidence_unbound");
        FrozenJobClosure? common = null;
        foreach (var item in groupJobs)
        {
            StepWritersEvidence.RequireConfigurationStopped(instance, database, contracts, item.Resource.JournalId);
            var physical = new JournalStore(instance, contracts).Read(item.Resource.JournalId);
            if (requireRestored && physical.Phase != JournalPhase.Committed) throw new IOException("configuration_run_restoration_required");
            var observed = new JournalWriterStore(database).ObserveForCurrentExecutor(item.Resource.JournalId);
            if (!observed.IsComplete || observed.State != WriterState.Stopped) throw new IOException("configuration_writer_stop_unconfirmed");
            if (noJobs)
            {
                RequireNoEffect(physical);
                continue;
            }
            if (item.Jobs.Count != 1) throw new IOException("configuration_run_writer_membership_unbound");
            var job = item.Jobs[0];
            if (session.PrimaryJobName != job.Name) throw new IOException("configuration_primary_job_unbound");
            var closure = new JobClosureStore(instance, database, contracts).Read(item.Resource.JournalId, job.Writer, job.Name, job.Session)
                ?? throw new IOException("writer_job_closure_missing");
            if (closure.OperationId != session.SessionId || closure.RunElapsedMilliseconds != elapsed || common is not null &&
                (common.WriterId != closure.WriterId || common.JobName != closure.JobName || common.SessionId != closure.SessionId ||
                    common.WasExecuted != closure.WasExecuted))
                throw new IOException("configuration_run_stop_duration_unbound");
            common = closure;
            if (session.EnteredRunning && closure.WasExecuted && physical.Root.GetProperty("participants").GetArrayLength() != 2)
                throw new IOException("configuration_run_output_not_sealed");
            if (!session.EnteredRunning && requireRestored) RequireSafeRestoration(physical, closure);
            if (!session.EnteredRunning && closure.WasExecuted) throw new IOException("configuration_run_unstarted_evidence_unbound");
            _ = ConfigurationJournalBinding.Read(instance, physical, contracts, database);
        }
        return new(TimeSpan.FromTicks(checked(elapsed * TimeSpan.TicksPerMillisecond)), common?.WasExecuted == true);
    }

    internal static bool HasExecutedWriter(SqliteConnection connection, SqliteTransaction transaction, ConfigurationRunStatus session)
    {
        using var command = ConfigurationRunRows.Command(connection, transaction, session.SessionId);
        command.CommandText = "SELECT count(*) FROM configuration.configuration_run_resources r JOIN main.journal_writer_job_roots w ON w.journal_id=r.journal_id WHERE r.session_id=$session AND w.job_name=$primary;";
        command.Parameters.AddWithValue("$primary", (object?)session.PrimaryJobName ?? DBNull.Value);
        var count = (long)command.ExecuteScalar()!;
        if (count != 0 && (count != session.Resources.Length || !session.EnteredRunning))
            throw new IOException("configuration_run_writer_membership_unbound");
        return count != 0;
    }

    internal bool HasNoJobs(ConfigurationRunStatus session) => Jobs(session).Values.All(jobs => jobs.Count == 0);

    private Dictionary<Guid, List<(Guid Writer, string Name, int Session)>> Jobs(ConfigurationRunStatus session)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        var parameters = session.Resources.Select((resource, index) =>
        {
            var name = "$journal" + index; command.Parameters.AddWithValue(name, resource.JournalId.ToString("D")); return name;
        }).ToArray();
        command.CommandText = "SELECT journal_id,writer_id,job_name,session_id FROM main.journal_writer_jobs WHERE journal_id IN (" +
            (parameters.Length == 0 ? "NULL" : string.Join(',', parameters)) + ") ORDER BY journal_id,job_name LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", checked(session.Resources.Length * 5 + 1));
        var result = session.Resources.ToDictionary(resource => resource.JournalId, _ => new List<(Guid Writer, string Name, int Session)>());
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            if (!result.TryGetValue(Guid.Parse(rows.GetString(0)), out var jobs) || jobs.Count == 4)
                throw new IOException("configuration_run_writer_membership_unbound");
            jobs.Add((Guid.Parse(rows.GetString(1)), rows.GetString(2), rows.GetInt32(3)));
        }
        return result;
    }

    internal static void RequireNoEffect(JournalDocument physical)
    {
        if (physical.Phase != JournalPhase.Committed || physical.Root.GetProperty("participants").GetArrayLength() != 0 ||
            !physical.Root.GetProperty("receipts").EnumerateArray().Any(receipt =>
            receipt.GetProperty("stepId").GetString() == "verify-no-effect" && receipt.GetProperty("effect").GetString() == "notExecuted"))
            throw new IOException("configuration_run_unstarted_evidence_unbound");
    }

    private static void RequireSafeRestoration(JournalDocument physical, FrozenJobClosure closure)
    {
        if (closure.WasExecuted || closure.RunElapsedMilliseconds != 0) throw new IOException("configuration_run_unstarted_evidence_unbound");
        if (!physical.Root.GetProperty("receipts").EnumerateArray().Any(receipt =>
            receipt.GetProperty("stepId").GetString() is "verify-no-effect" or "interruption-recovered"))
            throw new IOException("configuration_run_unstarted_evidence_unbound");
    }

    [SupportedOSPlatform("windows")]
    private RunStoppedFacts ReadGroup(ConfigurationRunStatus session, bool requireRestored)
    {
        var jobs = StepWritersEvidence.ReadStoppedGroup(instance, database, contracts, session.ParentJournalId, session.StepId);
        var closures = new JobClosureStore(instance, database, contracts);
        var allJobs = Jobs(session);
        var parent = jobs.ToDictionary(job => job.JobName, job => closures.ReadForExecutionEvidence(session.ParentJournalId,
            job.WriterId, job.JobName, job.SessionId) ?? throw new IOException("writer_job_closure_missing"), StringComparer.Ordinal);
        var childJobs = new HashSet<string>(StringComparer.Ordinal);
        var executed = false;
        foreach (var resource in session.Resources)
        {
            var actual = allJobs[resource.JournalId];
            var names = actual.Select(job => job.Name).ToHashSet(StringComparer.Ordinal);
            if (resource == session.Resources[0]) childJobs = names;
            else if (!childJobs.SetEquals(names)) throw new IOException("configuration_group_job_unbound");
            var physical = new JournalStore(instance, contracts).Read(resource.JournalId);
            if (requireRestored && physical.Phase != JournalPhase.Committed) throw new IOException("configuration_run_restoration_required");
            foreach (var job in actual)
            {
                var role = jobs.SingleOrDefault(item => item.JobName == job.Name && item.WriterId == job.Writer && item.SessionId == job.Session);
                if (role is null || role.Role == "judgement") throw new IOException("configuration_group_job_unbound");
                var closure = closures.Read(resource.JournalId, job.Writer, job.Name, job.Session) ?? throw new IOException("writer_job_closure_missing");
                var expected = parent[job.Name];
                if (closure.OperationId != session.SessionId || closure.Root != expected.Root || closure.Creator != expected.Creator ||
                    closure.WasExecuted != expected.WasExecuted || closure.RunElapsedMilliseconds != expected.RunElapsedMilliseconds ||
                    !closure.Family.SequenceEqual(expected.Family)) throw new IOException("configuration_group_closure_unbound");
            }
            var hasEffect = actual.Any(job => parent[job.Name].WasExecuted);
            if (hasEffect && (!session.EnteredRunning || physical.Root.GetProperty("participants").GetArrayLength() != 2))
                throw new IOException("configuration_run_output_not_sealed");
            if (!hasEffect && requireRestored && !session.EnteredRunning) RequireNoEffect(physical);
            _ = ConfigurationJournalBinding.Read(instance, physical, contracts, database);
        }
        foreach (var job in jobs.Where(job => job.Role != "judgement" && !childJobs.Contains(job.JobName)))
        {
            var closure = parent[job.JobName];
            if (closure.WasExecuted || !closure.Family.IsEmpty || closure.RunElapsedMilliseconds != 0)
                throw new IOException("configuration_unregistered_writer_effect_unverified");
        }
        var primary = jobs.SingleOrDefault(job => job.Role == "process");
        var primaryName = primary is not null && childJobs.Contains(primary.JobName) ? primary.JobName : null;
        if (session.PrimaryJobName != primaryName || session.ClosedElapsedMilliseconds != (primary?.ClosedElapsedMilliseconds ?? 0))
            throw new IOException("configuration_primary_runtime_unbound");
        if (primary is not null) executed = parent[primary.JobName].WasExecuted;
        return new(TimeSpan.FromMilliseconds(session.ClosedElapsedMilliseconds!.Value), executed);
    }
}

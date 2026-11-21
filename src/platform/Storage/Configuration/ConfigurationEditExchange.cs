using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public enum EditFileBoundary { WorkspaceCreated, WorkspaceRecorded, PreserveStarted, OriginalPreserved,
    PreserveRecorded, InstallStarted, CandidateInstalled, InstallRecorded }
public interface IEditFileObserver { void Reached(EditFileBoundary boundary, Guid journalId); }
internal sealed class ConfigurationRestorationFailure(Exception error) : IOException("configuration_restore_failed", error) { }

public sealed class ConfigurationEditExchange
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly JournalWriterStore _writers;
    private readonly JournalStore _journals;
    private readonly RecoveryJournalRegistry _registry;
    private readonly FileEffectOutbox _effects;
    private readonly IEditFileObserver? _observer;

    public ConfigurationEditExchange(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        IEditFileObserver? observer = null)
    {
        if (instance.InstanceId != database.InstanceId || instance.RuntimeRoot != database.RuntimeRoot)
            throw new IOException("configuration_store_instance_unbound");
        _instance = instance; _database = database; _contracts = contracts; _observer = observer;
        _writers = new(database); _journals = new(instance, contracts); _registry = new(database); _effects = new(database);
    }

    public void Materialize(Guid journalId) => MaterializeRegistered(journalId);

    private RegisteredJournal MaterializeRegistered(Guid journalId)
    {
        var registered = _registry.ReadJournal(journalId);
        if (registered.Kind is not (JournalKind.ConfigEdit or JournalKind.ConfigExchange)) throw new IOException("configuration_edit_journal_unbound");
        new InitialConfigurationJournal(_instance, _contracts, _database).MaterializeIfAbsent(registered);
        return registered;
    }

    public void Install(Guid journalId, CancellationToken cancellationToken = default)
    {
        if (!_writers.IsRegistered(journalId, JournalWriterStore.CurrentWriter.WriterId))
            throw new IOException("configuration_edit_executor_unbound");
        Materialize(journalId);
        using var history = _journals.ProtectReadHistory(journalId);
        var physical = _journals.Read(journalId);
        var binding = ConfigurationJournalBinding.Read(_instance, physical, _contracts, _database);
        if (physical.Phase != JournalPhase.Prepared || binding.Prepared is not null)
            throw new IOException("configuration_edit_install_requires_reconciliation");
        var scanner = new FileTreeScanner(SnapshotBudget.Default);
        var swap = new ConfigurationSwap(scanner, new(scanner, new(_instance, _contracts)));
        var prepared = swap.Prepare(binding.Plan, Stopped(journalId), cancellationToken);
        Reached(EditFileBoundary.WorkspaceCreated, journalId);
        var document = binding.Document;
        document["participants"]!.AsArray().Add(new JsonObject
        {
            ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "directory",
            ["resourceKey"] = document["resourceKeys"]!.AsArray().Select(item => item!.GetValue<string>())
                .Single(key => key.StartsWith("configuration:", StringComparison.Ordinal) && !key.StartsWith("configuration:script:", StringComparison.Ordinal)),
            ["identity"] = JsonSerializer.SerializeToNode(new ConfigurationWorkspaceCodec(_contracts).Encode(prepared, physical.PlanDigest))
        });
        Save(document, JournalPhase.Prepared, "prepare-workspace", "completed");
        Reached(EditFileBoundary.WorkspaceRecorded, journalId);
        Save(document, JournalPhase.Applying, "preserve-original", "started");
        Reached(EditFileBoundary.PreserveStarted, journalId);
        swap.PreserveOriginal(prepared, Stopped(journalId));
        Reached(EditFileBoundary.OriginalPreserved, journalId);
        Save(document, JournalPhase.Applying, "preserve-original", "completed");
        Reached(EditFileBoundary.PreserveRecorded, journalId);
        Save(document, JournalPhase.Applying, "install-candidate", "started");
        Reached(EditFileBoundary.InstallStarted, journalId);
        swap.InstallCandidate(prepared, Stopped(journalId));
        Reached(EditFileBoundary.CandidateInstalled, journalId);
        Save(document, JournalPhase.Applied, "install-candidate", "completed");
        Reached(EditFileBoundary.InstallRecorded, journalId);
    }

    public ConfigurationRecoveryResult Restore(Guid journalId) => Restore(journalId, null);

    internal ConfigurationRecoveryResult Restore(Guid journalId, IConfigurationPersistenceObserver? observer, Action<string>? phase = null)
    {
        var registered = MaterializeRegistered(journalId);
        using var history = registered.Phase is JournalPhase.Committed or JournalPhase.Quarantined
            ? null : _journals.ProtectReadHistory(journalId);
        return new ConfigurationRecovery(new ConfigurationRecoverySession(_instance, journalId, _contracts, _database,
            () => Observation(journalId), observer, _journals, phase)).Recover();
    }

    internal async Task RestoreCancelledRunAsync(ConfigurationRunStatus session, Guid journalId, IConfigurationPersistenceObserver? observer)
    {
        var phase = "configuration-read";
        Exception initial;
        try { RestoreCurrent(); return; }
        catch (Exception error) when (IsRetryableRestoreError(error))
        {
            initial = error;
            RecordFailure("configuration_restore_retry", error);
        }
        catch (Exception error)
        {
            RecordFailure("configuration_restore_failed", error);
            throw new ConfigurationRestorationFailure(error);
        }
        await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        phase = "configuration-read";
        try { RestoreCurrent(); }
        catch (Exception error)
        {
            RecordFailure("configuration_restore_failed", error);
            throw new ConfigurationRestorationFailure(new AggregateException(initial, error));
        }

        void RestoreCurrent()
        {
            if (Restore(journalId, observer, next => phase = next) == ConfigurationRecoveryResult.Quarantined)
                throw new IOException("configuration_run_restoration_unverified");
        }

        void RecordFailure(string reason, Exception error)
        {
            try { new RunDiagnosticStore(_instance, _database).Record(session.ParentJournalId, session.StepId, null, phase, reason, error, journalId); }
            catch (Exception diagnostic) when (diagnostic is IOException or UnauthorizedAccessException or JsonException or SqliteException)
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { kind = "execution.configuration.restore", session.ParentJournalId,
                    journalId, phase, reason, exceptionType = error.GetType().Name, diagnosticExceptionType = diagnostic.GetType().Name }));
            }
        }
    }

    private static bool IsRetryableRestoreError(Exception error) => error switch
    {
        SqliteException { SqliteErrorCode: 5 or 6 } => true,
        IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) } => true,
        IOException { Message: "journal_publication_busy" or "journal_revision_conflict" or "journal_protected_history_changed" } => true,
        System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 } => true,
        IOException { InnerException: { } cause } => IsRetryableRestoreError(cause),
        _ => false
    };

    private ConfigurationWriterObservation Observation(Guid journalId)
    {
        var observed = _writers.ObserveForCurrentExecutor(journalId);
        return new(_instance.InstanceId, journalId, observed.IsComplete, observed.State);
    }
    private ConfigurationWriterObservation Stopped(Guid journalId)
    {
        var observed = Observation(journalId);
        if (!observed.IsComplete || observed.State != WriterState.Stopped) throw new IOException("configuration_writer_stop_unconfirmed");
        return observed;
    }
    private void Reached(EditFileBoundary boundary, Guid journalId) => _observer?.Reached(boundary, journalId);

    private void Save(JsonObject document, JournalPhase phase, string step, string effect)
    {
        var previous = document["revision"]!.GetValue<long>();
        var journalId = document["journalId"]!.GetValue<Guid>();
        document["revision"] = previous + 1; document["phase"] = phase.ToString().ToLowerInvariant();
        document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        document["receipts"]!.AsArray().Add(new JsonObject
        {
            ["receiptId"] = Guid.NewGuid().ToString("D"), ["recordedAt"] = document["updatedAt"]!.GetValue<string>(),
            ["stepId"] = step, ["effect"] = effect, ["detailCode"] = "configuration_edit_exchange_observed"
        });
        var physical = _journals.Append(journalId, previous, JsonSerializer.SerializeToUtf8Bytes(document));
        var effectRow = _effects.Read(journalId).SingleOrDefault(item => item.StepId == step);
        if (effectRow is not null)
        {
            var next = effect == "started" ? FileEffectState.Started : FileEffectState.Completed;
            _effects.Record(journalId, step, effectRow.State, next, Convert.ToHexStringLower(SHA256.HashData(physical.Export())));
        }
        var registered = _registry.ReadJournal(journalId);
        _registry.Advance(registered with { Revision = physical.Revision, Phase = physical.Phase }, previous);
    }
}

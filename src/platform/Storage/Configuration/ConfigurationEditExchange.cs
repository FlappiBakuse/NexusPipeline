using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public enum EditFileBoundary { WorkspaceCreated, WorkspaceRecorded, PreserveStarted, OriginalPreserved,
    PreserveRecorded, InstallStarted, CandidateInstalled, InstallRecorded }
public interface IEditFileObserver { void Reached(EditFileBoundary boundary, Guid journalId); }

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

    public void Materialize(Guid journalId)
    {
        var registered = _registry.ReadJournal(journalId);
        if (registered.Kind is not (JournalKind.ConfigEdit or JournalKind.ConfigExchange)) throw new IOException("configuration_edit_journal_unbound");
        new InitialConfigurationJournal(_instance, _contracts, _database).MaterializeIfAbsent(registered);
    }

    public void Install(Guid journalId, CancellationToken cancellationToken = default)
    {
        if (!_writers.IsRegistered(journalId, JournalWriterStore.CurrentWriter.WriterId))
            throw new IOException("configuration_edit_executor_unbound");
        Materialize(journalId);
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

    public ConfigurationRecoveryResult Restore(Guid journalId)
    {
        Materialize(journalId);
        return new ConfigurationRecovery(new ConfigurationRecoverySession(_instance, journalId, _contracts, _database,
            () => Observation(journalId))).Recover();
    }

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

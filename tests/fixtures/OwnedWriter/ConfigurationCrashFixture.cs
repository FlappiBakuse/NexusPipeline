using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

internal sealed class ConfigurationCrashFixture
{
    private readonly string _faultPoint;
    private readonly string _report;
    private readonly RuntimeInstance _instance;
    private readonly FrozenConfigurationExchange _plan;
    private readonly JournalStore _store;
    private readonly RecoveryJournalRegistry _registry;
    private readonly FileEffectOutbox _outbox;
    private readonly StateDatabase _database;
    private readonly ConfigurationSwap _swap;
    private readonly ConfigurationWorkspaceCodec _workspace;
    private readonly ConfigurationWriterObservation _stopped;
    private JsonObject _journal;

    private ConfigurationCrashFixture(string faultPoint, string report, RuntimeInstance instance, FrozenConfigurationExchange plan,
        JournalStore store, StateDatabase database, ConfigurationSwap swap, ConfigurationWorkspaceCodec workspace)
    {
        _faultPoint = faultPoint;
        _report = report;
        _instance = instance;
        _plan = plan;
        _store = store;
        _database = database;
        _registry = new(database);
        _outbox = new(database);
        _swap = swap;
        _workspace = workspace;
        _stopped = new(instance.InstanceId, plan.JournalId, true, WriterState.Stopped);
        _journal = JsonNode.Parse(store.Read(plan.JournalId).Export())!.AsObject();
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var mode, var inputPath, var faultPoint, var report]) return 2;
        var reportValidated = false;
        try
        {
            OwnedFixtureEvidence.RequirePath(inputPath);
            OwnedFixtureEvidence.RequirePath(report);
            reportValidated = true;
            using var document = StrictJson.Parse(File.ReadAllBytes(inputPath));
            var input = document.RootElement;
            var runtime = input.GetProperty("runtimeRoot").GetString()!;
            OwnedFixtureEvidence.RequirePath(runtime);
            var contracts = new ContractReader();
            var instance = new InstanceRoot(contracts).OpenExisting(runtime);
            using var lease = InstanceLease.Acquire(instance, contracts);
            var blobs = new ImmutableBlobStore(instance, contracts);
            var reference = input.GetProperty("plan").Deserialize<BlobReference>()!;
            var plan = new ConfigurationPlanCodec(instance, contracts, blobs).Read(reference);
            OwnedFixtureEvidence.RequirePath(plan.TargetPath);
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(inputPath)!, "diagnostics"));
            if (mode is "accept-initial" or "recover-initial")
                return await InitialCrashFixture.RunAsync(mode, input, instance, plan, database, report);
            var scanner = new FileTreeScanner(SnapshotBudget.Default);
            var fixture = new ConfigurationCrashFixture(faultPoint, report, instance, plan, new(instance, contracts), database,
                new(scanner, new(scanner, blobs)), new(contracts));
            if (mode == "execute") await fixture.ExecuteAsync();
            else if (mode == "recover") fixture.Recover(input);
            else return 2;
            return 0;
        }
        catch (Exception error)
        {
            if (reportValidated && !File.Exists(report))
                OwnedFixtureEvidence.WriteReport(report, new { status = "refused", errorType = error.GetType().FullName, code = error.Message });
            return 1;
        }
    }

    private async Task ExecuteAsync()
    {
        await PauseAsync("before-prepare");
        var prepared = _swap.Prepare(_plan, _stopped, CancellationToken.None);
        await PauseAsync("prepared-before-workspace-receipt");
        _journal["participants"]!.AsArray().Add(new JsonObject
        {
            ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "directory", ["resourceKey"] = "configuration:fixture",
            ["identity"] = JsonSerializer.SerializeToNode(_workspace.Encode(prepared, _journal["planDigest"]!.GetValue<string>()))
        });
        Save(JournalPhase.Prepared, "prepare-workspace", "completed");
        await PauseAsync("after-workspace-receipt");
        Save(JournalPhase.Applying, "preserve-original", "started");
        await PauseAsync("before-preserve-original");
        _swap.PreserveOriginal(prepared, _stopped);
        await PauseAsync("preserved-before-receipt");
        Save(JournalPhase.Applying, "preserve-original", "completed");
        await PauseAsync("after-preserve-receipt");
        Save(JournalPhase.Applying, "install-candidate", "started");
        await PauseAsync("before-install-candidate");
        _swap.InstallCandidate(prepared, _stopped);
        await PauseAsync("installed-before-receipt");
        Save(JournalPhase.Applied, "install-candidate", "completed");
        await PauseAsync("after-install-receipt");
        OwnedFixtureEvidence.WriteReport(_report, new { status = "installed", processId = Environment.ProcessId });
    }

    private void Recover(JsonElement input)
    {
        OwnedFixtureEvidence.VerifyStopped(input, _instance.InstanceId, _plan.JournalId);
        var session = new ConfigurationRecoverySession(_instance, _plan.JournalId, new ContractReader(), _database, () =>
        {
            OwnedFixtureEvidence.VerifyStopped(input, _instance.InstanceId, _plan.JournalId);
            return _stopped;
        }, new FixtureObserver(_faultPoint, _report));
        var result = new ConfigurationRecovery(session).Recover();
        var status = result switch
        {
            ConfigurationRecoveryResult.NotExecuted => "notExecuted",
            ConfigurationRecoveryResult.AlreadyCompleted => "alreadyCompleted",
            _ => result.ToString().ToLowerInvariant()
        };
        OwnedFixtureEvidence.WriteReport(_report, new { status, phase = _store.Read(_plan.JournalId).Phase.ToString().ToLowerInvariant(), originalObjects = _plan.Before.OriginalObjects });
    }

    private void Save(JournalPhase phase, string step, string effect, NoEffectEvidence? noEffect = null, bool recoveryVerified = false)
    {
        var previous = _journal["revision"]!.GetValue<long>();
        _journal["revision"] = previous + 1;
        _journal["phase"] = phase.ToString().ToLowerInvariant();
        _journal["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        _journal["receipts"]!.AsArray().Add(new JsonObject
        {
            ["receiptId"] = Guid.NewGuid().ToString("D"), ["recordedAt"] = _journal["updatedAt"]!.GetValue<string>(),
            ["stepId"] = step, ["effect"] = effect, ["detailCode"] = "controlled_fixture_observation"
        });
        var next = _store.Append(_plan.JournalId, previous, JsonSerializer.SerializeToUtf8Bytes(_journal), noEffect, recoveryVerified);
        var pending = _outbox.Read(_plan.JournalId).SingleOrDefault(item => item.StepId == step);
        if (pending is not null)
        {
            var nextState = effect == "started" ? FileEffectState.Started : FileEffectState.Completed;
            _outbox.Record(_plan.JournalId, step, pending.State, nextState, Convert.ToHexStringLower(SHA256.HashData(next.Export())));
        }
        var registered = _registry.ReadSnapshot().Journals.Single(item => item.JournalId == _plan.JournalId);
        _registry.Advance(registered with { Revision = next.Revision, Phase = next.Phase }, previous, noEffect, recoveryVerified);
    }

    private sealed class FixtureObserver(string requested, string report) : IConfigurationPersistenceObserver
    {
        public void Reached(ConfigurationPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            var suffix = point == ConfigurationPersistencePoint.JournalFlushed ? "journal-flushed" : "sql-projected";
            var name = "recover-" + phase.ToString().ToLowerInvariant() + "-" + suffix;
            if (requested != name) return;
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", faultPoint = name, journalId, revision, processId = Environment.ProcessId });
            Thread.Sleep(Timeout.Infinite);
        }
    }

    private async Task PauseAsync(string point)
    {
        if (_faultPoint != point) return;
        OwnedFixtureEvidence.WriteReport(_report, new { status = "faultReady", faultPoint = point, processId = Environment.ProcessId });
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

}

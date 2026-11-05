using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PreparedHookResult
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ConfigurationWriterProcess? _writer;
    private readonly ConfigurationRunController? _controller;
    private readonly StoppedConfigurationEditor? _stopped;
    internal StepEvidenceBinding Binding { get; }
    internal string Role { get; }
    private readonly BlobReference _artifact;

    private PreparedHookResult(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        StepEvidenceBinding binding, string role, BlobReference artifact, ConfigurationWriterProcess? writer, StoppedConfigurationEditor? stopped, ConfigurationRunController? controller = null)
    {
        _instance = instance; _database = database; _contracts = contracts; _lease = lease;
        Binding = binding; Role = role; _artifact = artifact; _writer = writer; _stopped = stopped; _controller = controller;
    }

    internal static PreparedHookResult Live(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        StepEvidenceBinding binding, string role, BlobReference raw, ConfigurationWriterProcess writer, StoppedConfigurationEditor stopped)
    {
        lease.VerifyHeld(instance);
        return writer.UseStepEvidence<PreparedHookResult>(binding, stopped, (root, code) =>
        {
            var engine = root is null ? EngineOutcome.NotStarted : code is null ? EngineOutcome.Unknown : code == 0 ? EngineOutcome.Succeeded : EngineOutcome.Failed;
            var closure = new StepEvidenceReader(instance, database, contracts).ClosureReference(binding);
            var document = new HookEvidenceDocument(HookEvidenceReader.Format, 1, binding, role, raw, closure,
                root, code, engine, HookEvidenceReader.Reason(engine));
            var artifact = new ImmutableBlobStore(instance, contracts).PersistPlan(JsonSerializer.SerializeToUtf8Bytes(document, StepResultCodec.Options));
            return new(instance, database, contracts, lease, binding, role, artifact, writer, stopped);
        });
    }

    internal static PreparedHookResult Live(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        StepEvidenceBinding binding, string role, BlobReference raw, ConfigurationRunController controller, StoppedConfigurationEditor stopped)
    {
        lease.VerifyHeld(instance);
        return controller.UseStepEvidence<PreparedHookResult>(binding, stopped, (root, code) =>
        {
            var engine = root is null ? EngineOutcome.NotStarted : code is null ? EngineOutcome.Unknown : code == 0 ? EngineOutcome.Succeeded : EngineOutcome.Failed;
            var closure = new StepEvidenceReader(instance, database, contracts).ClosureReference(binding);
            var document = new HookEvidenceDocument(HookEvidenceReader.Format, 1, binding, role, raw, closure,
                root, code, engine, HookEvidenceReader.Reason(engine));
            var artifact = new ImmutableBlobStore(instance, contracts).PersistPlan(JsonSerializer.SerializeToUtf8Bytes(document, StepResultCodec.Options));
            return new(instance, database, contracts, lease, binding, role, artifact, null, stopped, controller);
        });
    }

    internal static PreparedHookResult Interrupted(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        StepEvidenceBinding binding, string role)
    {
        lease.VerifyHeld(instance);
        var reader = new StepEvidenceReader(instance, database, contracts);
        var context = reader.Context(binding, role);
        StepWritersEvidence.RequireStopped(instance, database, contracts, binding.ParentJournalId, [context.Job]);
        var closure = reader.ClosureReference(binding);
        var engine = context.Root is null ? EngineOutcome.NotStarted : EngineOutcome.Unknown;
        var document = new HookEvidenceDocument(HookEvidenceReader.Format, 1, binding, role, null, closure,
            context.Root, null, engine, HookEvidenceReader.Reason(engine));
        var artifact = new ImmutableBlobStore(instance, contracts).PersistPlan(JsonSerializer.SerializeToUtf8Bytes(document, StepResultCodec.Options));
        return new(instance, database, contracts, lease, binding, role, artifact, null, null);
    }

    internal T Use<T>(StepEvidenceBinding expected, string role, Func<BlobReference, HookEvidenceDocument, T> callback)
    {
        _lease.VerifyHeld(_instance);
        if (expected != Binding || role != Role) throw new IOException("execution_hook_native_owner_unbound");
        return _writer is null && _controller is null ? UseRecovery() : UseNative((root, code) =>
        {
            var value = Read();
            if (value.Root != root || value.ExitCode != code) throw new IOException("execution_hook_native_owner_changed");
            return Hold(value);
        });

        T UseNative(Func<ProcessIdentity?, int?, T> read) => _writer is not null
            ? _writer.UseStepEvidence(Binding, _stopped, read) : _controller!.UseStepEvidence(Binding, _stopped, read);
        T UseRecovery()
        {
            var context = new StepEvidenceReader(_instance, _database, _contracts).Context(Binding, Role);
            StepWritersEvidence.RequireStopped(_instance, _database, _contracts, Binding.ParentJournalId, [context.Job]);
            return Hold(Read());
        }
        HookEvidenceDocument Read() => new HookEvidenceReader(_instance, _database, _contracts).Read(Binding, Role, _artifact);
        T Hold(HookEvidenceDocument value)
        {
            var held = new List<IDisposable>();
            try
            {
                var blobs = new ImmutableBlobStore(_instance, _contracts);
                foreach (var reference in new HookEvidenceReader(_instance, _database, _contracts).ReadReferences(Binding, Role, _artifact))
                    held.Add(blobs.OpenVerified(reference));
                return callback(_artifact, value);
            }
            finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
        }
    }
}

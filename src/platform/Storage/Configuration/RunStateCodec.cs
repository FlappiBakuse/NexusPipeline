using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed record FrozenRunState(Guid InstanceId, Guid OwnerOperationId, Guid ConfigurationOperationId,
    ConfigurationRevision Source, long BindingRevision, bool AutoSyncEnabled, ImmutableArray<ConfigurationCapturedTasks> Tasks)
{
    public bool SyncInitially { get; init; } = AutoSyncEnabled;
    public bool SyncAtFinish { get; init; } = AutoSyncEnabled;
    public long InitialPublicationRevision { get; init; } = Source?.Revision ?? 0;
    internal BlobReference? SnapshotReference { get; init; }
    internal FileTreeSnapshot? InputTree { get; init; }
    internal BlobReference? InputReference { get; init; }
}

public sealed class RunStateCodec(RuntimeInstance instance, ContractReader contracts, ImmutableBlobStore blobs)
{
    public PreparedRunState Freeze(FrozenRunState state)
    {
        state = Validate(state);
        var held = new List<FileStream>();
        try
        {
            foreach (var content in Contents(state)) held.Add(blobs.OpenVerified(content));
            new ConfigurationTaskTransformation(blobs).ValidateInitial(state.ConfigurationOperationId, state.Source, state.Tasks);
            var snapshot = new ConfigurationSnapshotCodec(blobs).Write(state.Source.Tree);
            held.Add(blobs.OpenVerified(snapshot));
            if (state.InputTree is not null)
            {
                var input = new ConfigurationSnapshotCodec(blobs).Write(state.InputTree);
                held.Add(blobs.OpenVerified(input)); state = state with { InputReference = input };
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(ToWire(state, snapshot));
            var frozen = Decode(bytes);
            var reference = blobs.PersistPlan(bytes);
            held.Add(blobs.OpenVerified(reference));
            return new(frozen, reference, held);
        }
        catch { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); throw; }
    }

    public FrozenRunState Read(BlobReference reference)
    {
        var state = ReadStructure(reference);
        foreach (var content in Contents(state)) blobs.Verify(content);
        new ConfigurationTaskTransformation(blobs).ValidateInitial(state.ConfigurationOperationId, state.Source, state.Tasks);
        return state;
    }

    internal FrozenRunState ReadStructure(BlobReference reference)
    {
        if (reference.SizeBytes is < 1 or > StrictJson.MaximumBytes || reference.RelativePath != $"blobs/plans/{reference.Sha256}.json")
            throw new IOException("configuration_run_state_reference_invalid");
        using var input = blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        return Decode(bytes);
    }

    internal FrozenRunState ReadHeld(BlobReference reference, List<IDisposable> held)
    {
        if (reference.SizeBytes is < 1 or > StrictJson.MaximumBytes || reference.RelativePath != $"blobs/plans/{reference.Sha256}.json")
            throw new IOException("configuration_run_state_reference_invalid");
        var input = blobs.OpenVerified(reference);
        held.Add(input);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return Decode(bytes, held);
    }

    internal static IReadOnlyList<BlobReference> Contents(FrozenRunState state) => state.Source.Tree.Entries.Concat(state.InputTree?.Entries ?? [])
        .Where(entry => entry.Kind == FileObjectKind.File).Select(entry => new BlobReference($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!))
        .Distinct().OrderBy(item => item.Sha256, StringComparer.Ordinal).ToArray();

    internal static IEnumerable<BlobReference> References(FrozenRunState state) => Contents(state)
        .Append(state.SnapshotReference ?? throw new IOException("configuration_run_snapshot_unbound"))
        .Concat(state.InputReference is { } input ? [input] : []);

    private FrozenRunState Decode(byte[] bytes, List<IDisposable>? held = null)
    {
        var wire = contracts.Read<ConfigurationRunState>(bytes, "configuration-run-state.schema.json");
        var value = wire.Source; var stamp = value.Contract;
        var snapshot = new BlobReference(value.Snapshot.RelativePath, value.Snapshot.SizeBytes, value.Snapshot.Sha256);
        var source = new ConfigurationRevision(new(Guid.Parse(value.BindingId), value.ResourceId), value.Revision, Guid.Parse(value.UserId), Guid.Parse(value.ScriptId),
            new(stamp.Revision, stamp.DefinitionSha256, stamp.InstanceKey, stamp.RequiresInstance), new ConfigurationSnapshotCodec(blobs).Read(snapshot, held));
        var operation = Guid.Parse(wire.ConfigurationOperationId);
        var tasks = wire.Tasks.Select(item => ReadTask(item, source, operation)).ToImmutableArray();
        var input = wire.InputSnapshot is { } selected ? new BlobReference(selected.RelativePath, selected.SizeBytes, selected.Sha256) : null;
        var state = Validate(new(Guid.Parse(wire.InstanceId), Guid.Parse(wire.OwnerOperationId), operation, source, value.BindingRevision, wire.AutoSyncEnabled, tasks)
        { SyncInitially = wire.SyncInitially, SyncAtFinish = wire.SyncAtFinish, InitialPublicationRevision = wire.InitialPublicationRevision,
            InputReference = input, InputTree = input is null ? null : new ConfigurationSnapshotCodec(blobs).Read(input, held) });
        if (!JsonSerializer.SerializeToUtf8Bytes(ToWire(state, snapshot)).AsSpan().SequenceEqual(bytes))
            throw new IOException("configuration_run_state_encoding_unknown");
        return state with { SnapshotReference = snapshot };
    }

    private FrozenRunState Validate(FrozenRunState state)
    {
        if (blobs.Instance.InstanceId != instance.InstanceId || blobs.Instance.RuntimeRoot != instance.RuntimeRoot || state is null ||
            state.InstanceId != instance.InstanceId || state.OwnerOperationId == Guid.Empty || state.ConfigurationOperationId == Guid.Empty ||
            state.OwnerOperationId == state.ConfigurationOperationId || state.Source is null || state.Source.Address is null ||
            state.Source.Revision is < 1 or > 9007199254740991 || state.InitialPublicationRevision < state.Source.Revision || state.InitialPublicationRevision > 9007199254740991 || state.BindingRevision is < 1 or > 9007199254740991 ||
            state.Source.UserId == Guid.Empty || state.Source.ScriptId == Guid.Empty || state.Tasks.IsDefault || state.Tasks.Any(item => item is null))
            throw new IOException("configuration_run_state_identity_invalid");
        if (!state.AutoSyncEnabled && (state.SyncInitially || state.SyncAtFinish)) throw new IOException("configuration_run_synchronization_invalid");
        ConfigurationRevisionPolicy.ValidateContract(state.Source.Contract);
        var source = state.Source with { Tree = FileTreePolicy.Freeze(state.Source.Tree) };
        var inputTree = state.InputTree is null ? null : FileTreePolicy.Freeze(state.InputTree);
        if (inputTree is null ? state.InputReference is not null : state.SyncInitially || inputTree.Shape != source.Tree.Shape)
            throw new IOException("configuration_run_input_unbound");
        var rules = RunTaskPolicy.Freeze(state.Tasks.Select(item => item.Rule).ToImmutableArray());
        var byPath = state.Tasks.ToDictionary(item => item.Rule.RelativePath, StringComparer.Ordinal);
        var tasks = rules.Select(rule => byPath[rule.RelativePath] with { Rule = rule }).ToImmutableArray();
        foreach (var captured in tasks) RunTaskPolicy.RequireInitial(captured, state.ConfigurationOperationId, source);
        return state with { Source = source, Tasks = tasks, InputTree = inputTree };
    }

    private static ConfigurationCapturedTasks ReadTask(ConfigurationRunStateTasksItem value, ConfigurationRevision source, Guid operation)
    {
        var rule = RunTaskPolicy.Freeze(new RunTaskRule(value.RelativePath, Enum.Parse<ConfigurationSyntax>(value.Syntax, true),
            ConfigurationSelectorProtocol.Read(value.Access), new(ConfigurationSelectorProtocol.ReadTaskSource(value.Declaration.Source),
                value.Declaration.AuthorizedIds.ToImmutableArray()), value.SelectedIds.ToImmutableArray()));
        var entry = source.Tree.Entries.SingleOrDefault(item => item.RelativePath == rule.RelativePath && item.Kind == FileObjectKind.File)
            ?? throw new IOException("configuration_run_task_source_missing");
        if (value.InitialValues.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).Count() != value.InitialValues.Count)
            throw new IOException("configuration_run_initial_tasks_duplicate");
        var initial = new InitialTaskSelection(new(operation, source.Address.ResourceId, source.Revision, entry.Sha256!), rule.Declaration,
            value.InitialValues.ToImmutableDictionary(item => item.TaskId, item => item.Enabled, StringComparer.Ordinal));
        return new(rule, initial);
    }

    private static ConfigurationRunState ToWire(FrozenRunState state, BlobReference snapshot) => new()
    {
        Format = "nexus.configuration.run-state", Version = 1, InstanceId = state.InstanceId.ToString("D"),
        OwnerOperationId = state.OwnerOperationId.ToString("D"), ConfigurationOperationId = state.ConfigurationOperationId.ToString("D"), AutoSyncEnabled = state.AutoSyncEnabled,
        SyncInitially = state.SyncInitially, SyncAtFinish = state.SyncAtFinish, InitialPublicationRevision = state.InitialPublicationRevision,
        InputSnapshot = state.InputReference is { } input ? new() { RelativePath = input.RelativePath, SizeBytes = input.SizeBytes, Sha256 = input.Sha256 } : null,
        Source = new() { BindingId = state.Source.Address.BindingId.ToString("D"), ResourceId = state.Source.Address.ResourceId, Revision = state.Source.Revision,
            BindingRevision = state.BindingRevision, UserId = state.Source.UserId.ToString("D"), ScriptId = state.Source.ScriptId.ToString("D"),
            Snapshot = new() { RelativePath = snapshot.RelativePath, SizeBytes = snapshot.SizeBytes, Sha256 = snapshot.Sha256 },
            Contract = new() { Revision = state.Source.Contract.Revision, DefinitionSha256 = state.Source.Contract.DefinitionSha256,
                InstanceKey = state.Source.Contract.InstanceKey, RequiresInstance = state.Source.Contract.RequiresInstance } },
        Tasks = state.Tasks.Select(item => new ConfigurationRunStateTasksItem { RelativePath = item.Rule.RelativePath, Syntax = WireSyntax(item.Rule.Syntax),
            Access = ConfigurationSelectorProtocol.Write(item.Rule.Access), SelectedIds = item.Rule.SelectedIds,
            Declaration = new() { Source = ConfigurationSelectorProtocol.WriteTaskSource(item.Rule.Declaration.Source), AuthorizedIds = item.Rule.Declaration.AuthorizedIds },
            InitialValues = item.Initial.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                new ConfigurationRunStateTasksItemInitialValuesItem { TaskId = pair.Key, Enabled = pair.Value }).ToArray() }).ToArray()
    };

    internal static void RequireRetrySource(FrozenRunState state, FrozenRunState previous, ExecutionTaskRetry? retry = null,
        StoredConfigurationRevision? publication = null, RetryConfigurationInput? input = null)
    {
        if (state.SyncInitially || state.SnapshotReference is null || previous.SnapshotReference is null) throw new IOException("configuration_retry_source_unbound");
        var expected = previous with { OwnerOperationId = state.OwnerOperationId, ConfigurationOperationId = state.ConfigurationOperationId,
            InitialPublicationRevision = state.InitialPublicationRevision, SyncInitially = false,
            InputReference = retry is null ? input?.Reference : (publication ?? throw new IOException("configuration_retry_input_missing")).Manifest,
            InputTree = retry is null ? input?.Tree : publication!.Revision.Tree };
        if (retry is not null) expected = expected with { Tasks = expected.Tasks.Select(item => item with
            { Rule = RunTaskPolicy.Freeze(item.Rule with { SelectedIds = retry.Selection.SelectedIds.Where(id =>
                item.Rule.Declaration.AuthorizedIds.Contains(id, StringComparer.Ordinal)).ToImmutableArray() }) }).ToImmutableArray() };
        if (!JsonSerializer.SerializeToUtf8Bytes(ToWire(expected, previous.SnapshotReference)).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(ToWire(state, state.SnapshotReference))))
            throw new IOException("configuration_retry_source_unbound");
    }

    private static string WireSyntax(ConfigurationSyntax syntax)
    {
        var name = syntax.ToString(); return char.ToLowerInvariant(name[0]) + name[1..];
    }
}

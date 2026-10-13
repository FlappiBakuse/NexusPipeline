using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal interface IUpdateStoreOwner
{
    string Id { get; }
    string ActiveRoot(string installation);
    string DescribeSchema(string root);
    void Validate(string root, string schema);
}

internal interface IUpdateDataMigrator
{
    string Id { get; }
    string StoreId { get; }
    string InputSchema { get; }
    string OutputSchema { get; }
    Task ConvertAsync(string input, string output, CancellationToken token);
}

internal sealed record UpdateDataStep(string StoreId, string InputSchema, string OutputSchema, string MigratorId);

internal sealed class UpdateMigrationGraph
{
    private readonly IReadOnlyDictionary<string, IUpdateDataMigrator> _migrators;

    internal UpdateMigrationGraph(IEnumerable<IUpdateDataMigrator> migrators)
    {
        var entries = migrators.ToArray();
        if (entries.Length > 64 || entries.Any(entry => string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.StoreId)
            || string.IsNullOrWhiteSpace(entry.InputSchema) || string.IsNullOrWhiteSpace(entry.OutputSchema) || entry.InputSchema == entry.OutputSchema)
            || entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new InvalidDataException("update.migration_registry");
        _migrators = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        foreach (var store in entries.GroupBy(entry => entry.StoreId))
        {
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string schema)
            {
                if (visited.Contains(schema)) return;
                if (!visiting.Add(schema)) throw new InvalidDataException("update.migration_cycle");
                foreach (var edge in store.Where(edge => edge.InputSchema == schema)) Visit(edge.OutputSchema);
                visiting.Remove(schema); visited.Add(schema);
            }
            foreach (var schema in store.Select(edge => edge.InputSchema)) Visit(schema);
        }
    }

    internal IReadOnlyList<UpdateDataStep> Plan(string store, string input, string output)
    {
        if (!_migrators.Values.Any(edge => edge.StoreId == store && (edge.InputSchema == input || edge.OutputSchema == input)))
            throw new InvalidDataException("update.migration_source_schema_unknown");
        if (input == output) return [];
        var pending = new Queue<(string Schema, UpdateDataStep[] Steps)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { input };
        pending.Enqueue((input, []));
        while (pending.TryDequeue(out var item))
        {
            if (item.Steps.Length >= 8) continue;
            foreach (var edge in _migrators.Values.Where(edge => edge.StoreId == store && edge.InputSchema == item.Schema).OrderBy(edge => edge.Id, StringComparer.Ordinal))
            {
                var steps = item.Steps.Append(new UpdateDataStep(store, edge.InputSchema, edge.OutputSchema, edge.Id)).ToArray();
                if (edge.OutputSchema == output) return steps;
                if (visited.Add(edge.OutputSchema)) pending.Enqueue((edge.OutputSchema, steps));
            }
        }
        throw new InvalidDataException("update.migration_route_unavailable");
    }

    internal Task ConvertAsync(UpdateDataStep step, string input, string output, CancellationToken token)
    {
        if (!_migrators.TryGetValue(step.MigratorId, out var migrator) || migrator.StoreId != step.StoreId
            || migrator.InputSchema != step.InputSchema || migrator.OutputSchema != step.OutputSchema)
            throw new InvalidDataException("update.migration_step_unavailable");
        return migrator.ConvertAsync(input, output, token);
    }
}

internal sealed record UpdateDataStoreReceipt(string StoreId, string ActiveRoot, string InputRoot, string OutputRoot,
    string BackupRoot, string InputSchema, string OutputSchema, bool OriginallyPresent,
    UpdateInventory Input, UpdateInventory Output, int SwitchStep);
internal sealed record UpdateDataState(string TransactionRoot, IReadOnlyList<UpdateDataStoreReceipt> Stores, bool Committed)
{
    public UpdateInventory Workspace { get; init; } = new([]);
    public bool Equals(UpdateDataState? other) => other is not null && TransactionRoot == other.TransactionRoot
        && Committed == other.Committed && Stores.SequenceEqual(other.Stores) && Workspace == other.Workspace;
    public override int GetHashCode() => HashCode.Combine(TransactionRoot, Committed);

    internal void Validate()
    {
        if (string.IsNullOrEmpty(TransactionRoot) || !Path.IsPathFullyQualified(TransactionRoot) || Stores is null || Stores.Count > 32
            || Stores.Any(store => store is null)
            || Stores.Select(store => store.StoreId).Distinct(StringComparer.Ordinal).Count() != Stores.Count)
            throw new InvalidDataException("update.data_journal");
        PayloadPathSafety.RequireLinkFree(TransactionRoot);
        if (Workspace is null || Stores.Count != 0 && Workspace.Entries.Count == 0) throw new InvalidDataException("update.data_creation_receipts_missing");
        Workspace.Validate();
        var active = new List<string>();
        foreach (var store in Stores)
        {
            if (store is null || string.IsNullOrWhiteSpace(store.StoreId) || store.SwitchStep is < 0 or > 2
                || string.IsNullOrWhiteSpace(store.InputSchema) || string.IsNullOrWhiteSpace(store.OutputSchema))
                throw new InvalidDataException("update.data_store_journal");
            if (store.Input is null || store.Output is null) throw new InvalidDataException("update.data_snapshot_missing");
            store.Input.Validate(); store.Output.Validate();
            string source = Canonical(store.ActiveRoot);
            if (Overlaps(source, TransactionRoot) || active.Any(path => Overlaps(source, path)))
                throw new InvalidDataException("update.data_store_overlap");
            active.Add(source);
            foreach (string path in new[] { store.InputRoot, store.OutputRoot, store.BackupRoot })
            {
                string owned = Canonical(path);
                if (!owned.StartsWith(Canonical(TransactionRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || Overlaps(source, owned)) throw new InvalidDataException("update.data_snapshot_path");
            }
        }
        if (Committed && Stores.Any(store => store.SwitchStep != 2)) throw new InvalidDataException("update.data_commit_incomplete");
    }

    internal static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("update.data_path");
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        PayloadPathSafety.RequireLinkFree(full);
        return full;
    }
    internal static bool Overlaps(string left, string right)
    {
        left = Canonical(left); right = Canonical(right);
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class UpdateDataMigration
{
    internal static async Task<UpdateDataState> PrepareAsync(string installation, string transactionRoot,
        IReadOnlyList<(IUpdateStoreOwner Owner, string TargetSchema)> stores, UpdateMigrationGraph graph, CancellationToken token)
    {
        if (stores.Count is < 1 or > 32 || stores.Select(store => store.Owner.Id).Distinct(StringComparer.Ordinal).Count() != stores.Count)
            throw new InvalidDataException("update.data_store_set");
        transactionRoot = UpdateDataState.Canonical(transactionRoot);
        if (File.Exists(transactionRoot) || Directory.Exists(transactionRoot)) throw new IOException("update.data_transaction_exists");
        var locations = stores.Select((store, index) =>
        {
            string source = UpdateDataState.Canonical(store.Owner.ActiveRoot(installation));
            foreach (string reserved in new[] { ".nxp-update", ".nxp-backup", ".nxp/state/updates", "resources", "NexusPipeline.exe" })
                if (UpdateDataState.Overlaps(source, Path.Combine(installation, reserved)))
                    throw new InvalidDataException("update.data_store_reserved");
            if (UpdateDataState.Overlaps(source, transactionRoot)) throw new InvalidDataException("update.data_store_overlap");
            return (store.Owner, source, target: store.TargetSchema, index);
        }).ToArray();
        for (int i = 0; i < locations.Length; i++)
        for (int j = i + 1; j < locations.Length; j++)
            if (UpdateDataState.Overlaps(locations[i].source, locations[j].source)) throw new InvalidDataException("update.data_store_overlap");
        var declarations = locations.Select(location =>
        {
            string schema = location.Owner.DescribeSchema(location.source);
            location.Owner.Validate(location.source, schema);
            return (location.Owner, location.source, schema, plan: graph.Plan(location.Owner.Id, schema, location.target), location.target, location.index);
        }).ToArray();
        Directory.CreateDirectory(transactionRoot);
        var receipts = new List<UpdateDataStoreReceipt>();
        foreach (var declaration in declarations)
        {
            token.ThrowIfCancellationRequested();
            string storeRoot = Path.Combine(transactionRoot, declaration.index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string input = Path.Combine(storeRoot, "input"), output = Path.Combine(storeRoot, "output"), backup = Path.Combine(storeRoot, "backup");
            if (File.Exists(declaration.source)) throw new InvalidDataException("update.data_store_directory_required");
            bool present = Directory.Exists(declaration.source);
            var inventory = UpdateInventory.Current(declaration.source, null);
            Copy(declaration.source, input, inventory, token);
            string previous = input;
            for (int step = 0; step < declaration.plan.Count; step++)
            {
                string destination = step == declaration.plan.Count - 1 ? output : Path.Combine(storeRoot, "step-" + step);
                Directory.CreateDirectory(destination);
                await graph.ConvertAsync(declaration.plan[step], previous, destination, token).ConfigureAwait(false);
                declaration.Owner.Validate(destination, declaration.plan[step].OutputSchema);
                _ = UpdateInventory.Capture(destination);
                previous = destination;
            }
            if (declaration.plan.Count == 0) Copy(input, output, inventory, token);
            declaration.Owner.Validate(output, declaration.target);
            RequireExact(declaration.source, inventory, present);
            var result = UpdateInventory.Capture(output);
            receipts.Add(new(declaration.Owner.Id, declaration.source, input, output, backup, declaration.schema,
                declaration.target, present, inventory, result, 0));
        }
        var owned = UpdateInventory.Capture(transactionRoot).Entries.ToList();
        foreach (var receipt in receipts.Where(receipt => receipt.OriginallyPresent))
        {
            string relative = Path.GetRelativePath(transactionRoot, receipt.BackupRoot).Replace('\\', '/');
            owned.Add(new(relative, true, 0, null));
            owned.AddRange(receipt.Input.Entries.Select(entry => entry with { Path = relative + '/' + entry.Path }));
        }
        var state = new UpdateDataState(transactionRoot, receipts.ToArray(), false)
        { Workspace = new(owned.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray()) };
        state.Validate();
        return state;
    }

    internal static UpdateDataState Apply(UpdateDataState state, Action<UpdateDataState> persist)
    {
        state.Validate();
        if (state.Committed) throw new InvalidDataException("update.data_already_committed");
        persist(state);
        for (int index = 0; index < state.Stores.Count; index++)
        {
            var store = state.Stores[index];
            RequireExact(store.InputRoot, store.Input, true);
            if (store.SwitchStep == 0)
            {
                if (Directory.Exists(store.BackupRoot))
                {
                    RequireExact(store.BackupRoot, store.Input, store.OriginallyPresent);
                    if (Directory.Exists(store.ActiveRoot) || File.Exists(store.ActiveRoot)) throw new IOException("update.data_swap_ambiguous");
                }
                else
                {
                    RequireExact(store.ActiveRoot, store.Input, store.OriginallyPresent);
                    if (store.OriginallyPresent) Directory.Move(store.ActiveRoot, store.BackupRoot);
                }
                store = store with { SwitchStep = 1 };
                state = Replace(state, index, store); persist(state);
            }
            if (store.SwitchStep == 1)
            {
                if (Directory.Exists(store.OutputRoot))
                {
                    RequireExact(store.OutputRoot, store.Output, true);
                    if (Directory.Exists(store.ActiveRoot) || File.Exists(store.ActiveRoot)) throw new IOException("update.data_swap_ambiguous");
                    Directory.Move(store.OutputRoot, store.ActiveRoot);
                }
                RequireExact(store.ActiveRoot, store.Output, true);
                state = Replace(state, index, store with { SwitchStep = 2 }); persist(state);
            }
            RequireExact(store.ActiveRoot, store.Output, true);
        }
        return state;
    }

    internal static void Rollback(UpdateDataState state)
    {
        state.Validate();
        if (state.Committed) throw new InvalidDataException("update.data_committed_rollback_forbidden");
        foreach (var store in state.Stores.Reverse())
        {
            RequireExact(store.InputRoot, store.Input, true);
            if (Directory.Exists(store.BackupRoot))
            {
                RequireExact(store.BackupRoot, store.Input, true);
                if (Directory.Exists(store.ActiveRoot))
                {
                    RequireExact(store.ActiveRoot, store.Output, true);
                    store.Output.DeleteOwned(store.ActiveRoot, () => { });
                    Directory.Delete(store.ActiveRoot, false);
                }
                else if (File.Exists(store.ActiveRoot)) throw new IOException("update.data_unknown_active");
                Directory.Move(store.BackupRoot, store.ActiveRoot);
            }
            else if (!store.OriginallyPresent && Directory.Exists(store.ActiveRoot))
            {
                RequireExact(store.ActiveRoot, store.Output, true);
                store.Output.DeleteOwned(store.ActiveRoot, () => { });
                Directory.Delete(store.ActiveRoot, false);
            }
            RequireExact(store.ActiveRoot, store.Input, store.OriginallyPresent);
        }
    }

    internal static UpdateDataState Commit(UpdateDataState state, Action<UpdateDataState> persist)
    {
        state.Validate();
        if (state.Stores.Any(store => store.SwitchStep != 2)) throw new InvalidDataException("update.data_commit_incomplete");
        foreach (var store in state.Stores) RequireExact(store.ActiveRoot, store.Output, true);
        state = state with { Committed = true };
        persist(state);
        return state;
    }

    internal static void Cleanup(UpdateDataState state, Action? guard = null)
    {
        state.Validate();
        if (!state.Committed)
            foreach (var store in state.Stores) RequireExact(store.ActiveRoot, store.Input, store.OriginallyPresent);
        state.Workspace.DeleteOwned(state.TransactionRoot, () => { state.Validate(); guard?.Invoke(); });
        guard?.Invoke();
        if (Directory.Exists(state.TransactionRoot)) Directory.Delete(state.TransactionRoot, false);
    }

    private static UpdateDataState Replace(UpdateDataState state, int index, UpdateDataStoreReceipt receipt)
        => state with { Stores = state.Stores.Select((store, item) => item == index ? receipt : store).ToArray() };

    private static void RequireExact(string root, UpdateInventory inventory, bool present)
    {
        if (File.Exists(root) || Directory.Exists(root) != present || UpdateInventory.Current(root, null) != inventory)
            throw new IOException("update.data_snapshot_changed");
    }

    private static void Copy(string source, string destination, UpdateInventory inventory, CancellationToken token)
    {
        PayloadPathSafety.RequireLinkFree(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("update.data_copy_destination_exists");
        Directory.CreateDirectory(destination);
        foreach (var entry in inventory.Entries)
        {
            token.ThrowIfCancellationRequested();
            string target = UpdateInventory.Resolve(destination, entry.Path);
            if (entry.IsDirectory) Directory.CreateDirectory(target);
            else
            {
                using var input = File.OpenRead(UpdateInventory.Resolve(source, entry.Path));
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[64 * 1024]; int read;
                while ((read = input.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
            }
        }
        RequireExact(destination, inventory, true);
    }
}

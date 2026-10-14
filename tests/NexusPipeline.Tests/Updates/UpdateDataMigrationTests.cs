using System.Text.Json.Nodes;
using NexusPipeline.Modules.Updates;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class UpdateDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nxp-data-migration-" + Guid.NewGuid().ToString("N"));
    private readonly StoreOwner _owner = new("fixture-store", "data/fixture");
    private string Active => _owner.ActiveRoot(_root);
    private string Transaction => Path.Combine(_root, ".nxp-update", "fixture-transaction");
    private readonly byte[] _original;
    public UpdateDataMigrationTests()
    {
        Directory.CreateDirectory(Active);
        _original = System.Text.Encoding.UTF8.GetBytes("{\"schema\":1,\"count\":3,\"unknown\":{\"keep\":[1,2,3]}}");
        File.WriteAllBytes(Path.Combine(Active, "store.json"), _original);
    }

    [Fact]
    public void GraphSelectsBoundedDirectCompiledRouteAndRejectsUnknownSchemaAndCycles()
    {
        var graph = new UpdateMigrationGraph([new Migrator("one-two", "1", "2"), new Migrator("two-three", "2", "3"), new Migrator("one-three", "1", "3")]);
        Assert.Equal("one-three", Assert.Single(graph.Plan(_owner.Id, "1", "3")).MigratorId);
        Assert.Empty(graph.Plan(_owner.Id, "1", "1"));
        Assert.Throws<InvalidDataException>(() => graph.Plan(_owner.Id, "unknown", "unknown"));
        Assert.Throws<InvalidDataException>(() => graph.Plan(_owner.Id, "3", "1"));
        Assert.Throws<InvalidDataException>(() => new UpdateMigrationGraph([new Migrator("a", "1", "2"), new Migrator("b", "2", "1")]));
    }

    [Fact]
    public async Task ConversionAndCommitKeepUnknownFieldsAndForbidOldSnapshotOverNewWrites()
    {
        var state = await PrepareAsync();
        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(Active, "store.json")));
        UpdateDataState? persisted = null;
        state = UpdateDataMigration.Apply(state, value => persisted = value);
        Assert.Equal(state, persisted);
        var result = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Active, "store.json")))!;
        Assert.Equal(2, result["schema"]!.GetValue<int>());
        Assert.Equal(103, result["count"]!.GetValue<int>());
        Assert.Equal("[1,2,3]", result["unknown"]!["keep"]!.ToJsonString());
        state = UpdateDataMigration.Commit(state, value => persisted = value);
        File.WriteAllText(Path.Combine(Active, "after-commit.json"), "new-user-data");
        Assert.Throws<InvalidDataException>(() => UpdateDataMigration.Rollback(state));
        UpdateDataMigration.Cleanup(state);
        Assert.False(Directory.Exists(Transaction));
        Assert.Equal("new-user-data", File.ReadAllText(Path.Combine(Active, "after-commit.json")));
    }

    [Fact]
    public async Task InterruptedStoreMovesResumeFromPersistedBytesAndRollbackIsIdempotent()
    {
        foreach (int interruptStep in new[] { 1, 2 })
        foreach (bool persistBeforeFailure in new[] { false, true })
        {
            var prepared = await PrepareAsync();
            UpdateDataState persisted = prepared;
            Assert.Throws<IOException>(() => UpdateDataMigration.Apply(prepared, state =>
            {
                if (state.Stores[0].SwitchStep == interruptStep)
                {
                    if (persistBeforeFailure) persisted = state;
                    throw new IOException("fixture interruption");
                }
                persisted = state;
            }));
            var resumed = UpdateDataMigration.Apply(persisted, state => persisted = state);
            Assert.Equal(2, resumed.Stores[0].SwitchStep);
            UpdateDataMigration.Rollback(resumed);
            UpdateDataMigration.Rollback(resumed);
            Assert.Equal(_original, File.ReadAllBytes(Path.Combine(Active, "store.json")));
            UpdateDataMigration.Cleanup(resumed);
            Assert.False(Directory.Exists(Transaction));
        }
    }

    [Fact]
    public async Task UnknownActiveChangesPreserveBothBackupAndJournalOwnedSnapshots()
    {
        var prepared = await PrepareAsync();
        var applied = UpdateDataMigration.Apply(prepared, _ => { });
        string unknown = Path.Combine(Active, "unknown-new.json");
        File.WriteAllText(unknown, "external-write");
        Assert.Throws<IOException>(() => UpdateDataMigration.Rollback(applied));
        Assert.Throws<IOException>(() => UpdateDataMigration.Cleanup(applied));
        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(applied.Stores[0].BackupRoot, "store.json")));
        Assert.Equal("external-write", File.ReadAllText(unknown));
        File.Delete(unknown);
        UpdateDataMigration.Rollback(applied);
        File.WriteAllText(Path.Combine(Transaction, "unknown-evidence.txt"), "retain");
        Assert.Throws<IOException>(() => UpdateDataMigration.Cleanup(applied));
        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(Active, "store.json")));
    }

    [Fact]
    public async Task ReservedOverlappingSourcesAndCancelledConversionNeverChangeActiveStore()
    {
        var graph = new UpdateMigrationGraph([new Migrator("one-two", "1", "2")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateDataMigration.PrepareAsync(_root, Transaction,
            [(_owner, "2"), (new StoreOwner("second", "data/fixture/nested"), "2")], graph, default, Persist));
        Assert.False(Directory.Exists(Transaction));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateDataMigration.PrepareAsync(_root, Transaction,
            [(new StoreOwner("reserved", ".nxp/state"), "2")], graph, default, Persist));
        using var cancellation = new CancellationTokenSource();
        var cancelling = new UpdateMigrationGraph([new Migrator("cancel", "1", "2", () => cancellation.Cancel())]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateDataMigration.PrepareAsync(_root, Transaction,
            [(_owner, "2")], cancelling, cancellation.Token, Persist));
        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(Active, "store.json")));
    }

    [Fact]
    public async Task ProcessTerminationDuringPreparationPreservesUnownedBytesAndRecoversRecordedFiles()
    {
        for (int cut = 1; cut <= 9; cut++)
        {
            using var fixture = new NexusPipeline.Tests.Execution.ProviderWorkerFixture();
            File.WriteAllText(Path.Combine(fixture.Root, ".nxp-test-fixture"), "owned-process-contract");
            Directory.CreateDirectory(Path.Combine(fixture.Root, "active"));
            string active = Path.Combine(fixture.Root, "active", "data.txt");
            File.WriteAllText(active, "user bytes");
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(fixture.Root, "NexusPipeline.TestProviderWorker.exe"))
                { WorkingDirectory = fixture.Root, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--migration-cut"); start.ArgumentList.Add(cut.ToString());
            using var child = System.Diagnostics.Process.Start(start)!;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (!File.Exists(Path.Combine(fixture.Root, "cut-ready")))
                {
                    Assert.False(child.HasExited);
                    await Task.Delay(20, deadline.Token);
                }
                child.Kill(); await child.WaitForExitAsync(deadline.Token);
                var state = System.Text.Json.JsonSerializer.Deserialize<UpdateDataState>(File.ReadAllBytes(Path.Combine(fixture.Root, "journal.json")))!;
                Assert.Equal("Preparing", state.PreparationPhase);
                Assert.Equal("user bytes", File.ReadAllText(active));
                Assert.Throws<InvalidDataException>(() => UpdateDataMigration.Apply(state, _ => { }));
                if (cut is 2 or 9)
                {
                    Assert.Throws<IOException>(() => UpdateDataMigration.Cleanup(state));
                    Assert.True(Directory.Exists(state.TransactionRoot));
                }
                else
                {
                    if (Directory.Exists(state.TransactionRoot))
                    {
                        string unknown = Path.Combine(state.TransactionRoot, "external.txt");
                        File.WriteAllText(unknown, "retain");
                        Assert.Throws<IOException>(() => UpdateDataMigration.Cleanup(state));
                        Assert.Equal("retain", File.ReadAllText(unknown));
                        File.Delete(unknown);
                    }
                    UpdateDataMigration.Cleanup(state);
                    UpdateDataMigration.Cleanup(state);
                    Assert.False(Directory.Exists(state.TransactionRoot));
                }
                Assert.Equal("user bytes", File.ReadAllText(active));
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
        }
    }

    private void Persist(UpdateDataState state)
    {
        state.Validate();
        string file = Path.Combine(_root, "preparation.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(state));
    }

    private Task<UpdateDataState> PrepareAsync() => UpdateDataMigration.PrepareAsync(_root, Transaction, [(_owner, "2")],
        new UpdateMigrationGraph([new Migrator("one-two", "1", "2")]), default, Persist);

    private sealed class StoreOwner(string id, string relative) : IUpdateStoreOwner
    {
        public string Id => id;
        public string ActiveRoot(string installation) => Path.Combine(installation, relative);
        public string DescribeSchema(string root) => Directory.Exists(root)
            ? JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "store.json")))!["schema"]!.ToString() : "missing";
        public void Validate(string root, string schema)
        {
            if (schema is not ("1" or "2" or "3") || DescribeSchema(root) != schema)
                throw new InvalidDataException("fixture unknown schema");
        }
    }
    private sealed class Migrator(string id, string input, string output, Action? before = null) : IUpdateDataMigrator
    {
        public string Id => id;
        public string StoreId => "fixture-store";
        public string InputSchema => input;
        public string OutputSchema => output;
        public Task ConvertAsync(string source, string destination, CancellationToken token)
        {
            before?.Invoke(); token.ThrowIfCancellationRequested();
            var value = JsonNode.Parse(File.ReadAllBytes(Path.Combine(source, "store.json")))!;
            value["schema"] = int.Parse(output, System.Globalization.CultureInfo.InvariantCulture);
            value["count"] = value["count"]!.GetValue<int>() + 100;
            File.WriteAllText(Path.Combine(destination, "store.json"), value.ToJsonString());
            return Task.CompletedTask;
        }
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture path outside Temp");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}

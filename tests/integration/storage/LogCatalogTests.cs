using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LogCatalogTests
{
    [Fact(DisplayName = "The Current Log Catalog Reopens Without Losing Real Entries")]
    public async Task ReadsCurrentLogs()
    {
        RuntimeInstance instance;
        Guid runId, attemptId;
        LogCatalogItem[] entries;
        string root;
        using (var fixture = new ExecutionHooksFixture(false))
        {
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            var source = fixture.Source;
            new LogCatalogProjection(source.Instance, source.Database, new(), source.Lease).OnCommitted(receipt);
            entries = new LogCatalogReader(source.Instance, source.Database, new())
                .ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items.ToArray();
            instance = source.Instance; root = source.Root;
            runId = receipt.Facts.RunId; attemptId = receipt.Facts.AttemptId;
        }
        var databasePath = Path.Combine(instance.RuntimeRoot, "state", "nexus.db");
        using var lease = InstanceLease.Acquire(instance, new());
        var reopened = StateDatabase.OpenExisting(instance, Path.Combine(root, "log-limit-migration-inspection"),
            new JournalStartupAdmission(instance, new()), lease);
        Assert.Equal(entries, new LogCatalogReader(instance, reopened, new())
            .ReadPage(new(runId, attemptId)).Items.ToArray());
        Assert.NotNull(new AttemptReceiptStore(reopened).Read(attemptId));
        Assert.Equal(StateDatabase.SchemaDigest,
            DatabaseInspector.Inspect(databasePath, instance.InstanceId, Path.Combine(root, "new-catalog-inspection")).SchemaDigest);
        _ = StateDatabase.OpenExisting(instance, Path.Combine(root, "idempotent-log-limit-inspection"));
    }

    private static string[] DatabaseGroup(string path) => new[] { "", "-wal", "-shm", "-journal" }
        .Where(suffix => File.Exists(path + suffix)).Select(suffix =>
        {
            using var file = File.OpenRead(path + suffix);
            return suffix + ":" + Convert.ToHexStringLower(SHA256.HashData(file));
        }).ToArray();

    [Fact(DisplayName = "Replaying A Real Pre Main Post Attempt Keeps One Log Per Role In Page Order")]
    public async Task PublishesRolePages()
    {
        using var fixture = new ExecutionHooksFixture(false);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        var source = fixture.Source; var projection = new LogCatalogProjection(source.Instance, source.Database, new(), source.Lease);
        projection.OnCommitted(receipt); projection.OnCommitted(receipt);
        var reader = new LogCatalogReader(source.Instance, source.Database, new());
        var first = reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId, Limit: 1));
        Assert.Equal(RunLogRole.PreHook, Assert.Single(first.Items).Key.Role); Assert.Equal(0, first.NextPosition);
        var second = reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId, first.NextPosition!.Value, 1));
        Assert.Equal(RunLogRole.Main, Assert.Single(second.Items).Key.Role); Assert.Equal(1, second.NextPosition);
        var third = reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId, second.NextPosition!.Value, 1));
        Assert.Equal(RunLogRole.PostHook, Assert.Single(third.Items).Key.Role); Assert.Null(third.NextPosition);
        Assert.All(first.Items.Concat(second.Items).Concat(third.Items), item =>
        { Assert.True(item.IsComplete); Assert.InRange(item.RecordedBytes, 1, 65536); Assert.Equal(receipt.Facts.AttemptId, item.Key.AttemptId); });
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
    }

    [Theory(DisplayName = "A Changed Derived Sql Entry Cannot Grant Foreign Or Missing Original Bytes")]
    [InlineData("binding")]
    [InlineData("snapshot")]
    [InlineData("missing")]
    [InlineData("raw")]
    [InlineData("header")]
    public async Task RejectsChangedProjection(string fault)
    {
        using var fixture = new ExecutionHooksFixture(false);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync()); var source = fixture.Source;
        new LogCatalogProjection(source.Instance, source.Database, new(), source.Lease).OnCommitted(receipt);
        var reader = new LogCatalogReader(source.Instance, source.Database, new());
        var page = reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId));
        var raw = new ExecutionLogReader(source.Instance, source.Database, new()).Describe(page.Items[0].Key);
        var original = new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export();
        var selected = $"attempt_id='{receipt.Facts.AttemptId:D}' AND position=0";
        switch (fault)
        {
            case "binding": fixture.Base.Execute("UPDATE execution_log_entries SET step_id='foreign-step' WHERE " + selected + ";"); break;
            case "snapshot": fixture.Base.Execute("UPDATE execution_log_entries SET raw_sha256='" + new string('0', 64) + "' WHERE " + selected + ";"); break;
            case "missing": fixture.Base.Execute("DELETE FROM execution_log_entries WHERE " + selected + ";"); break;
            case "header": fixture.Base.Execute("UPDATE execution_log_catalog SET terminal_sha256='" + new string('0', 64) + "';"); break;
            case "raw": File.WriteAllText(Path.Combine(source.Instance.RuntimeRoot, "blobs", "content", raw.Parts[0].Sha256 + ".bin"), "preserve-unknown-bytes"); break;
        }
        if (fault == "raw")
        {
            Assert.Equal(page.Items.ToArray(), reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items.ToArray());
            Assert.ThrowsAny<IOException>(() => new ExecutionLogReader(source.Instance, source.Database, new())
                .OpenOriginal(page.Items[0].Key, page.Items[0].SnapshotSha256));
        }
        else Assert.ThrowsAny<IOException>(() => reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)));
        Assert.Equal(original, new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export());
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
        if (fault == "raw") Assert.Equal("preserve-unknown-bytes",
            File.ReadAllText(Path.Combine(source.Instance.RuntimeRoot, "blobs", "content", raw.Parts[0].Sha256 + ".bin")));
    }

    [Fact(DisplayName = "A Projection Sql Rollback Publishes No Partial Index And Exact Retry Does Not Change The Receipt Or Raw")]
    public async Task RevertsFailedProjection()
    {
        using var fixture = new ExecutionHooksFixture(false);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync()); var source = fixture.Source;
        var original = new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export();
        var projection = new LogCatalogProjection(source.Instance, source.Database, new(), source.Lease);
        fixture.Base.Execute("CREATE TRIGGER fail_log_catalog BEFORE INSERT ON execution_log_entries BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try
        {
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => projection.OnCommitted(receipt));
            Assert.Equal(0, fixture.Base.Count("execution_log_catalog")); Assert.Equal(0, fixture.Base.Count("execution_log_entries"));
        }
        finally { fixture.Base.Execute("DROP TRIGGER fail_log_catalog;"); }
        projection.OnCommitted(receipt); projection.OnCommitted(receipt);
        var reader = new LogCatalogReader(source.Instance, source.Database, new());
        Assert.Equal(3, reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items.Count);
        Assert.Equal(original, new JournalStore(source.Instance, new()).Read(receipt.JournalId).Export());
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "Real Retry Keeps Both Attempt Log Catalogs And Never Rebinds The First Snapshot To The Successor")]
    public async Task KeepsAttemptCatalogs()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("retry-interrupted",
            retrySafe: true, retryApproved: true, maximumAttempts: 2);
        var source = fixture.Source; var first = await fixture.RunInterruptedAsync();
        var projection = new LogCatalogProjection(source.Instance, source.Database, new(), source.Lease);
        projection.OnCommitted(first);
        var reader = new LogCatalogReader(source.Instance, source.Database, new());
        var old = Assert.Single(reader.ReadPage(new(first.Facts.RunId, first.Facts.AttemptId)).Items);
        var retries = new ExecutionRetryService(source.Instance, source.Database, new(), source.Lease, fixture.Service);
        using var pending = Assert.IsType<PreparedExecutionRetry>(retries.Prepare(first.Facts.RunId, first.Facts.AttemptId, DateTimeOffset.UtcNow));
        _ = retries.Start(pending, DateTimeOffset.UtcNow);
        var second = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync()); projection.OnCommitted(second);
        Assert.Equal(first.Facts.RunId, second.Facts.RunId); Assert.NotEqual(first.Facts.AttemptId, second.Facts.AttemptId);
        var current = Assert.Single(reader.ReadPage(new(second.Facts.RunId, second.Facts.AttemptId)).Items);
        Assert.Equal(second.Facts.AttemptId, current.Key.AttemptId); Assert.Equal(first.Facts.AttemptId, old.Key.AttemptId);
        Assert.Equal(old, Assert.Single(reader.ReadPage(new(first.Facts.RunId, first.Facts.AttemptId)).Items));
        Assert.Equal(2, fixture.Base.Count("execution_log_catalog"));
        Assert.Equal(first, new AttemptReceiptStore(source.Database).Read(first.Facts.AttemptId));
        Assert.Equal(second, new AttemptReceiptStore(source.Database).Read(second.Facts.AttemptId));
    }

    [Fact(DisplayName = "Actual Stopped Recovery Keeps Its Saved Observed Raw Readable Without Inventing Final Or Eof")]
    public async Task ReadsRecoveredOutput()
    {
        using var fixture = new StepWriterFixture("wait", judgement: new(AutomationJudgementLanguage.Keywords, ["ready"], [], null));
        var source = fixture.Base.Source; var state = fixture.Scope.VerifyReadOnly();
        var binding = StepResultCodec.Binding(state.Latest.Root, Assert.Single(state.Steps));
        using var collector = new StepEvidenceCollector(source.Instance, source.Database, new(), source.Lease, binding, fixture.Writer);
        fixture.Scope.EnterApplying(); collector.Start();
        Assert.Equal(BusinessOutcome.Succeeded, (await collector.BusinessAccepted.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        Assert.Null(await fixture.Writer.AwaitRootAsync(TimeSpan.FromMilliseconds(20)));
        state = fixture.Scope.VerifyReadOnly();
        var observed = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total)).Latest;
        var saved = StepEvidenceCodec.Read(new(source.Instance, new()), binding, observed.Artifact);
        Assert.NotNull(saved.RawOutput);
        var raw = RawOutputCodec.Read(new(source.Instance, new()), binding, saved.RawOutput!);
        Assert.False(raw.IsFinal); Assert.InRange(raw.Parts.Sum(part => part.Content.SizeBytes), 1, 65536);
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        var terminal = new AttemptTerminalStore(source.Instance, source.Database, new(), source.Lease);
        terminal.FreezeDispatch(fixture.JournalId, true);
        terminal.RecordCollected(fixture.JournalId,
            new StepEvidenceFactory(source.Instance, source.Database, new(), source.Lease).CreateInterruptedCollected(binding));
        _ = await collector.CollectAsync(stopped, TimeSpan.FromSeconds(10));
        terminal.MarkApplied(fixture.JournalId);
        var receipt = terminal.Commit(fixture.JournalId, LifecycleOutcome.Interrupted, "host_interrupted");
        new LogCatalogProjection(source.Instance, source.Database, new(), source.Lease).OnCommitted(receipt);
        var row = Assert.Single(new LogCatalogReader(source.Instance, source.Database, new())
            .ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items);
        Assert.False(row.IsComplete); Assert.Equal(saved.RawOutput!.Sha256, row.SnapshotSha256); Assert.Equal(raw.ObservedBytes, row.ObservedBytes);
        using var original = new ExecutionLogReader(source.Instance, source.Database, new()).OpenOriginal(row.Key, row.SnapshotSha256);
        using var text = new StreamReader(original, Encoding.UTF8);
        var value = await text.ReadToEndAsync(); Assert.Equal("ready\n", value);
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "A Metadata Page Does Not Open Raw Parts While Original Reads Require Their Actual Verified Bytes")]
    public async Task SeparatesMetadataReads()
    {
        using var fixture = new ExecutionHooksFixture(false);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync()); var source = fixture.Source;
        new LogCatalogProjection(source.Instance, source.Database, new(), source.Lease).OnCommitted(receipt);
        var reader = new LogCatalogReader(source.Instance, source.Database, new());
        var page = reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId));
        var selected = page.Items.Single(item => item.Key.Role == RunLogRole.PreHook);
        var logs = new ExecutionLogReader(source.Instance, source.Database, new());
        var part = Assert.Single(logs.Describe(selected.Key).Parts.Take(1));
        var path = Path.Combine(source.Instance.RuntimeRoot, "blobs", "content", part.Sha256 + ".bin");
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(page.Items.ToArray(), reader.ReadPage(new(receipt.Facts.RunId, receipt.Facts.AttemptId)).Items.ToArray());
            Assert.ThrowsAny<IOException>(() => logs.OpenPart(selected.Key, selected.SnapshotSha256, part.Index));
            Assert.ThrowsAny<IOException>(() => logs.OpenOriginal(selected.Key, selected.SnapshotSha256));
        }
        using var verified = logs.OpenPart(selected.Key, selected.SnapshotSha256, part.Index);
        Assert.Equal(part.SizeBytes, verified.Length);
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
    }
}

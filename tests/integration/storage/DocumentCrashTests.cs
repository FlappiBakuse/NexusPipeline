using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class DocumentCrashTests
{
    [Theory(DisplayName = "A Six Credential Publication Recovers Only After Its Actual Complete Writer Family Stops And Never Publishes An Incomplete Candidate")]
    [InlineData(SecretPreparationPoint.Accepted, true)]
    [InlineData(SecretPreparationPoint.CommittedJournal, true)]
    [InlineData(SecretPreparationPoint.CiphertextFlushed, false)]
    public async Task RequiresStoppedWriter(SecretPreparationPoint point, bool recoverable)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "secret-document-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var reportPath = Path.Combine(root, "fault.json");
        using var owner = new OwnedCrashProcess();
        _ = owner.Start(["secret-document", root, point.ToString(), reportPath], root, Guid.NewGuid(), Guid.NewGuid(), Path.Combine(root, "native.json"));
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath);
        Assert.Equal("faultReady", report.GetProperty("status").GetString());
        var instance = report.GetProperty("instance").Deserialize<RuntimeInstance>()!;
        var subject = report.GetProperty("subjectId").GetGuid();
        var journalId = report.GetProperty("journalId").GetGuid();
        var database = StateDatabase.OpenExisting(instance, Path.Combine(root, "before-stop"));
        Assert.Equal(new WriterStopObservation(true, WriterState.Running), new JournalWriterStore(database).Observe(journalId));
        var before = Fingerprints(instance.RuntimeRoot);
        Assert.ThrowsAny<Exception>(() => StateDatabase.OpenExisting(instance, Path.Combine(root, "live-refusal"), new JournalStartupAdmission(instance, new())));
        Assert.Equal(before, Fingerprints(instance.RuntimeRoot));
        Assert.Equal(1, ReadDocument(database).Revision);
        foreach (var field in Fields) Assert.False((await new SecretFieldStore(instance, database).GetStateAsync(new("fixture-plugin", subject, field), default)).IsConfigured);
        var parent = new SecretDocumentFormat(instance, new()).Read(report.GetProperty("parent").Deserialize<BlobReference>()!);
        var source = new PreparationPlanCodec(instance, new(), database).Read(parent.Document.Secrets[0].Preparation!).ReadWire().DocumentPublication!.Ciphertext;
        var sourceReference = new BlobReference(source.RelativePath, source.SizeBytes, source.Sha256);
        var references = new BlobReferenceIndex(instance, new(), database, new(instance, new()));
        Assert.True(references.IsReferenced(sourceReference));
        Assert.True(references.IsReferenced(parent.Document.Content));
        var stopped = await owner.StopAsync(); Assert.True(stopped.CompleteJobStopped); _ = await owner.AwaitExitAsync();
        Assert.Equal(new WriterStopObservation(true, WriterState.Stopped), new JournalWriterStore(database).Observe(journalId));
        using var lease = InstanceLease.Acquire(instance, new());
        var roots = new PayloadApplyRoots(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"));
        database = StateDatabase.OpenExisting(instance, Path.Combine(root, "startup"), new JournalStartupAdmission(instance, new(), roots));
        var recovery = new StartupJournalRecovery(instance, new(), database, roots);
        if (!recoverable)
        {
            before = Fingerprints(instance.RuntimeRoot, includeDatabase: false);
            var business = BusinessRows(database);
            Assert.ThrowsAny<IOException>(() => recovery.Recover(lease));
            Assert.Equal(before, Fingerprints(instance.RuntimeRoot, includeDatabase: false));
            Assert.Equal(business, BusinessRows(database));
            Assert.Equal(1, ReadDocument(database).Revision);
            foreach (var field in Fields) Assert.False((await new SecretFieldStore(instance, database).GetStateAsync(new("fixture-plugin", subject, field), default)).IsConfigured);
            return;
        }
        var index = recovery.Recover(lease); Assert.True(index.Current.IsReady); Assert.Empty(index.Current.Isolations);
        Assert.Equal(2, ReadDocument(database).Revision);
        foreach (var field in Fields)
        {
            using var value = await new SecretFieldStore(instance, database).ReadAsync(new("fixture-plugin", subject, field), default);
            value.Use(text => Assert.True(text.SequenceEqual("synthetic-" + field)));
        }
        var again = recovery.Recover(lease); Assert.True(again.Current.IsReady); Assert.Empty(again.Current.Isolations);
        Assert.False(references.IsReferenced(sourceReference));
        File.Delete(Path.Combine(instance.RuntimeRoot, sourceReference.RelativePath));
        _ = StateDatabase.OpenExisting(instance, Path.Combine(root, "source-retired"), new JournalStartupAdmission(instance, new(), roots));
        new SecretDocumentInventory(instance, database, new()).Verify();
    }

    private static readonly string[] Fields = ["cn", "os", "skland", "skport", "kuro", "tajiduo"];
    private static Extensibility.State.ExtensionDocument ReadDocument(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        return ExtensionDocumentRows.Read(connection, null, "fixture-plugin", "tasks")!;
    }
    private static string[] Fingerprints(string root, bool includeDatabase = true) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !path.EndsWith("instance.lock", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
        .Where(path => includeDatabase || !path.StartsWith(Path.Combine(root, "state") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private static string[] BusinessRows(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        var result = new List<string>();
        foreach (var table in new[] { "operations", "secret_fields", "secret_payloads", "secret_mutations", "extension_documents", "secret_document_publications", "secret_document_fields" })
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY 1;";
            using var rows = command.ExecuteReader();
            while (rows.Read()) result.Add(table + ":" + JsonSerializer.Serialize(Enumerable.Range(0, rows.FieldCount)
                .Select(index => rows.IsDBNull(index) ? null : rows.GetValue(index))));
        }
        return result.ToArray();
    }
}

using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RunHistoryTests
{
    [Theory(DisplayName = "Historical Progress Revision Damage Is Rejected Even When Initial And Current Snapshots Remain Valid")]
    [InlineData("manifest", "immutable_blob_mismatch")]
    [InlineData("contract", "configuration_run_history_revision_unbound")]
    [InlineData("assets", "configuration_reference_closure_invalid")]
    public async Task RejectsDamagedProgress(string field, string reason)
    {
        await using var fixture = new RunWriterFixture(); await fixture.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(15));
        var source = fixture.Base.Source;
        var runs = new ConfigurationRunStore(source.Instance, source.Database, new());
        var progress = new RunProgressStore(source.Instance, source.Database, new());
        var capture = new RunProgressCapture(source.Instance, source.Database, new());
        ConfigurationRunStatus Current() => runs.Read(fixture.Session.SessionId, ConfigurationRunFixture.Subject)!;
        using (var sample = capture.Read(Current(), fixture.Writer, sealedOutput: false))
            Assert.All(progress.Publish(Guid.NewGuid(), ConfigurationRunFixture.Subject, sample).Session.Resources,
                resource => Assert.Equal(2, resource.CurrentRevision));
        var main = fixture.Base.Request.Resources.Single(resource => resource.Target.Address.ResourceId == "main");
        File.WriteAllText(Path.Combine(main.Target.TargetPath, "tasks.json"), "{\"tasks\":{\"one\":true,\"new\":false},\"count\":42}");
        var stopped = await fixture.StopAsync(); fixture.Seal(stopped);
        fixture.Restore(verifySavedBaseline: false);
        using (var sample = capture.Read(Current(), fixture.Writer, sealedOutput: true))
            Assert.Equal(ConfigurationRunPhase.Completed,
                progress.Publish(Guid.NewGuid(), ConfigurationRunFixture.Subject, sample, ConfigurationRunPhase.Completed).Session.Phase);
        ConfigurationRunInventory.VerifyReadOnly(source.Instance, source.Database, new());
        var where = $"binding_id='{main.Target.Address.BindingId:D}' AND resource_id='main' AND revision=2";
        fixture.Base.Execute(field switch
        {
            "manifest" => $"UPDATE configuration_revisions SET manifest_size=manifest_size+1 WHERE {where};",
            "contract" => $"UPDATE configuration_revisions SET contract_digest='{new string('b', 64)}' WHERE {where};",
            _ => $"DELETE FROM configuration_revision_assets WHERE {where};"
        });
        var saved = new ConfigurationRevisionStore(source.Instance, source.Database, new()).Read(main.Target.Address)!;
        Assert.Equal(3, saved.Revision); Assert.Contains("\"count\":42", fixture.Base.Read(saved.Tree));
        using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
            Assert.Equal(1, ConfigurationRevisionRows.Read(connection, transaction, main.Target.Address, 1,
                new(new(source.Instance, new()))).Revision.Revision);
        var suffixes = new[] { "", "-wal", "-shm", "-journal" }; var path = source.Database.DatabasePath;
        var before = suffixes.Where(suffix => File.Exists(path + suffix)).ToDictionary(suffix => suffix, suffix => File.ReadAllBytes(path + suffix));
        Assert.Equal(reason, Assert.Throws<IOException>(() => StateDatabase.OpenExisting(source.Instance,
            Path.Combine(source.Root, "intermediate-" + field), new JournalStartupAdmission(source.Instance, new()))).Message);
        Assert.Equal(before.Keys.Order(), suffixes.Where(suffix => File.Exists(path + suffix)).Order());
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(path + pair.Key));
        fixture.Base.VerifyOriginal();
    }
}

using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ReferenceReuseTests
{
    [Theory(DisplayName = "Completed Reference Facts Avoid Rereading Immutable Inputs And Recheck Changed Registration")]
    [InlineData("revision")]
    [InlineData("resources")]
    public async Task ReusesCompletedFacts(string change)
    {
        using var fixture = new ExecutionRuntimeFixture(); _ = await fixture.Consumer.RunNextAsync();
        var source = fixture.Source; var registry = new RecoveryJournalRegistry(source.Database);
        var registered = registry.ReadJournal(fixture.State.Journal.JournalId);
        var reference = AttemptPreparedBinding.Reference(source.Instance, registered);
        var reader = new PlanReferenceClosure(source.Instance, new(), new(source.Instance, new()), source.Database);
        var first = reader.Read(registered, reference).ToArray(); Assert.NotEmpty(first);
        using var unavailable = new FileStream(Path.Combine(source.Instance.RuntimeRoot, reference.RelativePath),
            FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(first, reader.Read(registered, reference).ToArray());
        source.Database.Write((connection, transaction) =>
        {
            using var command = DatabaseConnections.Command(connection, transaction,
                change == "revision" ? "UPDATE operation_journals SET revision=revision+1 WHERE journal_id=$0;" :
                    "INSERT INTO journal_resources(journal_id,resource_key) VALUES($0,'fixture:changed');", registered.JournalId.ToString("D"));
            return command.ExecuteNonQuery();
        });
        Assert.Throws<IOException>(() => reader.Read(registry.ReadJournal(registered.JournalId), reference));
    }
}

using Microsoft.Data.Sqlite;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NoEffectTests
{
    [Theory(DisplayName = "An Actual No Effect Operation Sql Failure Rolls Back Its Terminal Cursor And Reconciles The Original Receipt")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplaysUnexecutedOutcome(bool ignore)
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.ExtensionApply, PayloadAction.Install);
        fixture.Accept();
        var id = fixture.Plan.OperationId.ToString("D");
        Sql("CREATE TRIGGER reject_no_effect_operation BEFORE UPDATE OF status ON operations " +
            "WHEN OLD.operation_id='" + id + "' AND NEW.status='completed' BEGIN SELECT RAISE(" +
            (ignore ? "IGNORE" : "ABORT,'fixture-operation-failure'") + "); END;");
        var error = Record.Exception(() => fixture.Recover());
        if (ignore) Assert.Equal("extension_no_effect_operation_unbound", Assert.IsType<IOException>(error).Message);
        else Assert.IsType<SqliteException>(error);
        var registry = new RecoveryJournalRegistry(fixture.Database);
        var pending = registry.ReadJournal(fixture.Plan.JournalId);
        Assert.Equal(JournalPhase.Prepared, pending.Phase); Assert.Equal(2, pending.Revision);
        Assert.Equal("accepted", Status());
        Assert.All(registry.ReadSnapshot().FileEffects.Where(item => item.JournalId == pending.JournalId),
            item => Assert.Equal(FileEffectState.NotExecuted, item.State));
        Assert.Contains(registry.ReadSnapshot().Isolations, item => item.JournalId == pending.JournalId);
        Assert.Equal(EffectGateState.Sealed, new JournalEffectGate(fixture.Database).Read(pending.JournalId).State);
        var history = new JournalStore(fixture.Instance, new ContractReader()).ReadAllRevisions(pending.JournalId);
        Assert.Equal(JournalPhase.Committed, history[^1].Phase); Assert.Equal(3, history[^1].Revision);
        var bytes = history.Select(item => Convert.ToHexStringLower(SHA256.HashData(item.Export()))).ToArray();
        Sql("DROP TRIGGER reject_no_effect_operation;");
        Assert.Equal(JournalRecoveryResult.AlreadyCompleted, fixture.Recover());
        Assert.Equal("completed", Status());
        Assert.Equal(JournalPhase.Committed, registry.ReadJournal(pending.JournalId).Phase);
        Assert.Equal(bytes, new JournalStore(fixture.Instance, new ContractReader()).ReadAllRevisions(pending.JournalId)
            .Select(item => Convert.ToHexStringLower(SHA256.HashData(item.Export()))).ToArray());
        Assert.Equal(JournalRecoveryResult.AlreadyCompleted, fixture.Recover());

        void Sql(string sql) => fixture.Database.Write((connection, transaction) =>
        { using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = sql; command.ExecuteNonQuery(); return true; });
        string Status()
        {
            using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM operations WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$id", id); return (string)command.ExecuteScalar()!;
        }
    }
}

using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed class FileEffectOutbox(StateDatabase database)
{
    public IReadOnlyList<FileEffect> Read(Guid journalId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        return FileEffectRows.Read(connection, journalId);
    }

    public void Record(Guid journalId, string stepId, FileEffectState expectedState, FileEffectState nextState, string receiptDigest,
        NoEffectEvidence? noEffect = null)
    {
        RequireTransition(expectedState, nextState, receiptDigest, noEffect);
        database.Write((connection, transaction) =>
        {
            RecordInside(connection, transaction, journalId, stepId, expectedState, nextState, receiptDigest, noEffect);
            return true;
        });
    }

    internal static void RecordInside(SqliteConnection connection, SqliteTransaction transaction,
        Guid journalId, string stepId, FileEffectState expectedState, FileEffectState nextState, string receiptDigest,
        NoEffectEvidence? noEffect = null)
    {
        RequireTransition(expectedState, nextState, receiptDigest, noEffect);
        if (nextState is FileEffectState.Started or FileEffectState.NotExecuted)
            JournalEffectGate.Require(connection, transaction, journalId,
                nextState == FileEffectState.Started ? EffectGateState.Entered : EffectGateState.Sealed,
                requireApplying: nextState == FileEffectState.Started);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE file_effects SET state=$next,receipt_digest=$receipt WHERE journal_id=$journal AND step_id=$step AND state=$expected AND " +
            "(action='removeOwned' OR EXISTS(SELECT 1 FROM operation_journals WHERE journal_id=$journal AND phase<>'committed')) AND " +
            "($skipStarted=0 OR action='removeOwned' AND EXISTS(SELECT 1 FROM operation_journals WHERE journal_id=$journal AND operation_kind='fileCleanup'));";
        command.Parameters.AddWithValue("$skipStarted", expectedState == FileEffectState.Started && nextState == FileEffectState.NotExecuted ? 1 : 0);
        command.Parameters.AddWithValue("$next", FileEffectRows.Wire(nextState));
        command.Parameters.AddWithValue("$receipt", receiptDigest);
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$step", stepId);
        command.Parameters.AddWithValue("$expected", FileEffectRows.Wire(expectedState));
        if (command.ExecuteNonQuery() != 1) throw new IOException("file_effect_state_conflict");
    }

    private static void RequireTransition(FileEffectState expectedState, FileEffectState nextState, string receiptDigest,
        NoEffectEvidence? noEffect)
    {
        if (!FileEffectRows.IsDigest(receiptDigest) || !Allowed(expectedState, nextState)) throw new ArgumentException("Invalid file effect transition");
        if (nextState == FileEffectState.NotExecuted && noEffect is not { HasCompleteInventory: true, HasExternalWrites: false,
                HasStartedWriter: false, HasUnknownBytes: false }) throw new IOException("file_effect_no_effect_evidence_required");
    }

    private static bool Allowed(FileEffectState from, FileEffectState to) => (from, to) is
        (FileEffectState.Pending, FileEffectState.Started) or
        (FileEffectState.Pending, FileEffectState.NotExecuted) or
        (FileEffectState.Started, FileEffectState.NotExecuted) or
        (FileEffectState.Pending, FileEffectState.Quarantined) or
        (FileEffectState.Started, FileEffectState.Completed) or
        (FileEffectState.Started, FileEffectState.Quarantined);
}

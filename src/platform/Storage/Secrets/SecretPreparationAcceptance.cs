using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Secrets;

[SupportedOSPlatform("windows")]
internal sealed class SecretPreparationAcceptance(StateDatabase database)
{
    internal bool Accept(OperationIdentity operation, PreparedSecretPayload prepared)
    {
        var plan = RequirePrepared(operation, prepared);
        if (plan.NotificationPublication is not null || plan.DocumentPublication is not null)
            throw new IOException("secret_parent_publication_required");
        var writer = JournalWriterStore.CurrentWriter;
        return database.Write((connection, transaction) => AcceptInside(connection, transaction, operation, plan, prepared.Reference, writer));
    }

    internal SecretPreparationPlan RequirePrepared(OperationIdentity operation, PreparedSecretPayload prepared)
    {
        var plan = prepared.Plan.ReadWire();
        if (plan.InstanceId != database.InstanceId.ToString("D") || operation.OperationId.ToString("D") != plan.OperationId ||
            operation != SecretFieldStore.Identity(operation.OperationId, SecretPreparationRows.Key(plan), plan.ExpectedRevision, SecretDisposition.Set) ||
            prepared.Ciphertext.Length != plan.Candidate.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(prepared.Ciphertext)) != plan.Candidate.Sha256)
            throw new IOException("secret_preparation_acceptance_unbound");
        return plan;
    }

    internal static bool AcceptInside(SqliteConnection connection, SqliteTransaction transaction, OperationIdentity operation,
        SecretPreparationPlan plan, BlobReference reference, JournalWriter writer)
    {
        NotificationSecretBinding.RequireParentInside(connection, transaction, plan);
        SecretDocumentBinding.Parent(connection, transaction, plan);
        var accepted = OperationRows.Accept(connection, transaction, operation, DateTimeOffset.UtcNow);
        if (!accepted.IsNew) { _ = SecretPreparationRows.Read(connection, transaction, operation.OperationId, plan.ReplayProof); return false; }
        SecretPreparationRows.VerifyBefore(connection, transaction, plan);
        var journal = new RegisteredJournal(Guid.Parse(plan.JournalId), operation.OperationId, JournalKind.SecretPrepare, JournalPhase.Prepared, 1,
            reference.RelativePath, reference.Sha256, Array.AsReadOnly(plan.ResourceKeys.ToArray()));
        RecoveryJournalRegistry.RegisterInside(connection, transaction, journal,
            [new(journal.JournalId, "secret-payload", FileEffectAction.InstallCandidate, FileEffectState.Pending, reference.Sha256, null)],
            writer);
        SecretPreparationRows.Insert(connection, transaction, plan);
        return true;
    }
}

namespace NexusPipeline.Extensibility.Secrets;

/// <summary>Nonsecret status scoped to one extension, subject and declared field.</summary>
public sealed record SecretFieldState(Guid SubjectId, string Field, long Revision, Guid? SecretId, bool IsConfigured, bool IsReadable);

/// <summary>Accesses only the extension and selected subjects bound by the Host, never generic secret storage.</summary>
public interface IExtensionSecretPort
{
    /// <summary>Returns status without plaintext; decryption failure remains unavailable, not an empty value.</summary>
    ValueTask<SecretFieldState> GetStateAsync(Guid subjectId, string field, CancellationToken cancellationToken);
    /// <summary>Consumes and always disposes the change, including capability, revision and persistence failures.</summary>
    ValueTask<SecretFieldState> ApplyAsync(Guid operationId, Guid subjectId, string field, long expectedRevision, SecretChange change, CancellationToken cancellationToken);
    /// <summary>Returns a short-lived plaintext owner only after a fresh authorization check.</summary>
    ValueTask<SecretDraft> ReadAsync(Guid subjectId, string field, CancellationToken cancellationToken);
}

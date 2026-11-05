namespace NexusPipeline.Core.Secrets;

public sealed record SecretFieldReference
{
    public const long MaximumRevision = 9007199254740991L;
    public long Revision { get; }
    public Guid? SecretId { get; }

    public SecretFieldReference(long revision, Guid? secretId)
    {
        if (revision is < 0 or > MaximumRevision || secretId == Guid.Empty) throw new ArgumentException("secret_reference_invalid");
        Revision = revision;
        SecretId = secretId;
    }
}

public static class SecretMutationPolicy
{
    public static SecretFieldReference Apply(SecretFieldReference current, long expectedRevision, SecretDisposition disposition, Guid? candidate)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!Enum.IsDefined(disposition) || expectedRevision is < 0 or > SecretFieldReference.MaximumRevision
            || candidate == Guid.Empty || (disposition == SecretDisposition.Set) != candidate.HasValue)
            throw new ArgumentException("secret_mutation_invalid");
        if (current.Revision != expectedRevision) throw new InvalidOperationException("secret_revision_conflict");
        return disposition == SecretDisposition.Keep ? current : new(checked(current.Revision + 1), candidate);
    }
}

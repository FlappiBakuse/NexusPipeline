namespace NexusPipeline.Core.Secrets;

public sealed class OwnSecretPolicy : ISecretAccessPolicy
{
    public void AssertAllowed(SecretFieldKey key, SecretAccessKind access, SecretSubjectScopes declaredScopes)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Enum.IsDefined(access) || (declaredScopes & ~(SecretSubjectScopes.OwnExtension | SecretSubjectScopes.SelectedBinding)) != 0)
            throw new UnauthorizedAccessException("secret_access_scope_invalid");
        if (key.OwnerKind != FieldOwnerKind.Extension ||
            (declaredScopes & SecretSubjectScopes.OwnExtension) == 0)
            throw new UnauthorizedAccessException("secret_subject_scope_unbound");
        // The extension port creates this namespace from its actual activation, never a caller-supplied extension ID.
        key.RequireExtensionOwner(key.ExtensionId!);
    }
}

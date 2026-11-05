using System.Text.Json;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Secrets;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

public sealed class ExtensionSecretPort : IExtensionSecretPort
{
    private readonly ExtensionScope _scope;
    private readonly ISecretAccessPolicy _authorization;
    private readonly ISecretFieldStore _store;
    private readonly SecretSubjectScopes _subjectScopes;

    public ExtensionSecretPort(ExtensionManifest manifest, ExtensionScope scope, ISecretAccessPolicy authorization,
        ISecretFieldStore store, ContractReader reader)
    {
        var admitted = new ExtensionManifestAdmission(reader).ReadCurrent(JsonSerializer.SerializeToUtf8Bytes(manifest), scope.Activation.ExtensionId);
        _scope = scope;
        _authorization = authorization;
        _store = store;
        foreach (var permission in admitted.Permissions.SecretAccess ?? [])
            _subjectScopes |= permission switch
            {
                "own-extension" => SecretSubjectScopes.OwnExtension,
                "selected-binding" => SecretSubjectScopes.SelectedBinding,
                _ => throw new InvalidOperationException("secret_subject_scope_unknown")
            };
    }

    public async ValueTask<SecretFieldState> GetStateAsync(Guid subjectId, string field, CancellationToken cancellationToken)
    {
        var key = Authorize(subjectId, field, SecretAccessKind.Status);
        using var cancellation = Linked(cancellationToken);
        var state = await _store.GetStateAsync(key, cancellation.Token).ConfigureAwait(false);
        cancellation.Token.ThrowIfCancellationRequested();
        Authorize(subjectId, field, SecretAccessKind.Status);
        return Map(key, state);
    }

    public async ValueTask<SecretFieldState> ApplyAsync(Guid operationId, Guid subjectId, string field, long expectedRevision,
        SecretChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        using (change)
        {
            if (operationId == Guid.Empty || expectedRevision < 0) throw new ArgumentException("secret_mutation_identity_invalid");
            var key = Authorize(subjectId, field, SecretAccessKind.Write);
            using var cancellation = Linked(cancellationToken);
            cancellation.Token.ThrowIfCancellationRequested();
            var disposition = change.Kind switch
            {
                SecretChangeKind.Keep => SecretDisposition.Keep,
                SecretChangeKind.Set => SecretDisposition.Set,
                SecretChangeKind.Clear => SecretDisposition.Clear,
                _ => throw new InvalidOperationException("secret_disposition_invalid")
            };
            SecretValue? material = null;
            try
            {
                if (disposition == SecretDisposition.Set) change.UseValue(value => material = new SecretValue(value));
                var state = await _store.ApplyAsync(operationId, key, expectedRevision, disposition, material, cancellation.Token).ConfigureAwait(false);
                // Committed mutations retain their operation result even if the caller cancels after the commit.
                return Map(key, state);
            }
            finally { material?.Dispose(); }
        }
    }

    public async ValueTask<SecretDraft> ReadAsync(Guid subjectId, string field, CancellationToken cancellationToken)
    {
        var key = Authorize(subjectId, field, SecretAccessKind.Read);
        using var cancellation = Linked(cancellationToken);
        using var material = await _store.ReadAsync(key, cancellation.Token).ConfigureAwait(false);
        cancellation.Token.ThrowIfCancellationRequested();
        Authorize(subjectId, field, SecretAccessKind.Read);
        SecretDraft? draft = null;
        try
        {
            material.Use(value => draft = new SecretDraft(value));
            cancellation.Token.ThrowIfCancellationRequested();
            Authorize(subjectId, field, SecretAccessKind.Read);
            return draft ?? throw new IOException("secret_unavailable");
        }
        catch { draft?.Dispose(); throw; }
    }

    internal SecretFieldKey Authorize(Guid subjectId, string field, SecretAccessKind kind)
    {
        _scope.Cancellation.ThrowIfCancellationRequested();
        if (!_scope.IsPublished) throw new InvalidOperationException("extension_activation_not_published");
        if (_subjectScopes == SecretSubjectScopes.None) throw new UnauthorizedAccessException("secret_access_permission_required");
        var key = new SecretFieldKey(_scope.Activation.ExtensionId, subjectId, field);
        key.RequireExtensionOwner(_scope.Activation.ExtensionId);
        _authorization.AssertAllowed(key, kind, _subjectScopes);
        return key;
    }

    private CancellationTokenSource Linked(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _scope.Cancellation);

    private static SecretFieldState Map(SecretFieldKey key, SecretFieldStatus state)
    {
        if (state.Revision < 0 || state.IsConfigured != state.SecretId.HasValue || state.SecretId == Guid.Empty || state.IsReadable && !state.IsConfigured)
            throw new IOException("secret_status_unverified");
        return new(key.SubjectId, key.Field, state.Revision, state.SecretId, state.IsConfigured, state.IsReadable);
    }
}

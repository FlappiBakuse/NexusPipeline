using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed record DraftProjectContract(Guid RootId, long RootRevision, ExtensionInstalledMetadata Consumer,
    ExtensionInstalledMetadata Provider);

public sealed class BindingDrafts : IDisposable
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ConfigurationBatchStore _batches;
    private readonly Dictionary<Guid, Candidate> _candidates = new();
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly ITimer _expiry;

    public BindingDrafts(RuntimeInstance instance, StateDatabase database, ContractReader contracts, TimeProvider? clock = null)
    {
        _instance = instance;
        _database = database;
        _batches = new(instance, database, contracts);
        _clock = clock ?? TimeProvider.System;
        _expiry = _clock.CreateTimer(_ => { lock (_gate) Expire(); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    internal bool IsStaged(Guid id, string subject, Guid sessionId, string digest)
    {
        lock (_gate)
        {
            Expire();
            if (!_candidates.TryGetValue(id, out var value)) return false;
            if (value.Subject != subject || value.Session != sessionId || value.Digest != digest) throw new OperationConflictException();
            value.LastTouched = _clock.GetTimestamp();
            return true;
        }
    }

    internal void Stage(Guid id, string subject, Guid session, string digest, DraftBindingIdentity owner, ConfigurationRevisionBatch batch,
        DraftProjectSecrets? secrets = null, DraftProjectContract? project = null)
    {
        lock (_gate)
        {
            if (IsStaged(id, subject, session, digest))
            {
                if (OperatingSystem.IsWindows()) _candidates[id].Secrets?.RequireProofs(secrets?.ReplayProofs);
                return;
            }
            if (_candidates.Count >= 64) throw new ConfigurationException("configuration_draft_capacity");
            _candidates.Add(id, new(subject, session, digest, owner, _batches.Prepare(batch), secrets, project, _clock.GetTimestamp()));
        }
    }

    internal void StageManaged(Guid id, string subject, string digest, DraftBindingIdentity owner, ConfigurationRevisionBatch batch,
        DraftProjectSecrets? secrets = null, DraftProjectContract? project = null) => Stage(id, subject, Guid.Empty, digest, owner, batch, secrets, project);

    internal ConfigurationRevisionBatch ReadManaged(Guid id, string subject, Guid bindingId, DraftBindingIdentity owner) =>
        Read(id, subject, bindingId, owner, managed: true).Batch.Batch;

    internal DraftProjectSecrets? ReadProtections(Guid id, string subject, Guid bindingId, DraftBindingIdentity owner) =>
        Read(id, subject, bindingId, owner, managed: true).Secrets;

    internal IReadOnlyList<ResourceClaim> Claims(IReadOnlyList<Guid> ids, string subject)
    {
        lock (_gate)
        {
            var selected = RequireCandidates(ids, subject);
            var claims = selected.Select(value => value.Project).OfType<DraftProjectContract>()
                .SelectMany(project => new[] { project.Consumer.ExtensionId, project.Provider.ExtensionId })
                .Distinct(StringComparer.Ordinal).Select(id => new ResourceClaim("extension:" + id, ResourceClaimMode.Reference)).ToList();
            if (OperatingSystem.IsWindows() && selected.Any(candidate => candidate.Secrets?.HasChanges == true))
                claims.Add(new("secrets:fields", ResourceClaimMode.Mutation));
            return claims;
        }
    }

    internal ConfigurationRevisionBatch ReadNative(Guid id, string subject, Guid bindingId, DraftBindingIdentity owner) =>
        Read(id, subject, bindingId, owner, managed: false).Batch.Batch;

    private Candidate Read(Guid id, string subject, Guid bindingId, DraftBindingIdentity owner, bool managed)
    {
        lock (_gate)
        {
            Expire();
            if (!_candidates.TryGetValue(id, out var value) || value.Subject != subject || value.Owner != owner ||
                (value.Session == Guid.Empty) != managed ||
                value.Batch.Batch.Changes[0].Address.BindingId != bindingId)
                throw new ConfigurationException("configuration_draft_missing");
            value.LastTouched = _clock.GetTimestamp();
            return value;
        }
    }

    internal void Discard(Guid id, string subject)
    {
        lock (_gate)
        {
            if (!_candidates.TryGetValue(id, out var value)) return;
            if (value.Subject != subject) throw new OperationConflictException();
            Remove(id);
        }
    }

    internal AutomationChangeResult Commit(OperationIdentity operation, AutomationChanges changes, IReadOnlyList<Guid> ids,
        DateTimeOffset acceptedAt, Func<Action<SqliteConnection, SqliteTransaction, AutomationCatalog>, AutomationChangeResult> write)
    {
        lock (_gate)
        {
            var selected = RequireCandidates(ids, operation.SubjectId);
            var byBinding = selected.ToDictionary(value => value.Batch.Batch.Changes[0].Address.BindingId);
            foreach (var (id, value) in byBinding)
            {
                var binding = changes.Bindings.SingleOrDefault(item => item.Id == id);
                if (binding is null || binding.Revision != 0 || binding.ScriptId != value.Owner.ScriptId || binding.UserId != value.Owner.UserId)
                    throw new ConfigurationException("configuration_draft_binding_changed");
            }
            var files = OperatingSystem.IsWindows() ? new DraftSecretFiles(_instance, _database,
                selected.Select(value => value.Secrets).OfType<DraftProjectSecrets>()) : null;
            Exception? primary = null;
            try
            {
                var result = write(Publish);
                foreach (var id in ids) Discard(id, operation.SubjectId);
                return result;
            }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                if (OperatingSystem.IsWindows()) files?.Release(primary);
            }

            void Publish(SqliteConnection connection, SqliteTransaction transaction, AutomationCatalog before)
            {
                var installations = new Dictionary<string, ExtensionInstalledMetadata?>(StringComparer.Ordinal);
                for (var index = 0; index < selected.Length; index++)
                {
                    var value = selected[index];
                    var id = value.Batch.Batch.Changes[0].Address.BindingId;
                    DraftBindingOwner.Resolve(before, id, value.Owner);
                    if (value.Project is { } project)
                        RequireProject(connection, transaction, before, changes, value.Owner, project, installations);
                    if (value.Session != Guid.Empty)
                    {
                        var session = ConfigurationEditRows.Read(connection, transaction, value.Session, operation.SubjectId);
                        if (session is not { IsDraft: true, Phase: ConfigurationEditPhase.Cancelled } || session.CompletionOperationId != ids[index])
                            throw new ConfigurationException("configuration_draft_not_staged");
                    }
                    var child = new Guid(SHA256.HashData(BoundedJson.Serialize(new { operation.OperationId, BindingId = id })).AsSpan(0, 16));
                    value.Batch.Use((batch, manifests) =>
                    {
                        if (!OperationRows.Accept(connection, transaction, ConfigurationBatchStore.Identity(child, operation.SubjectId, batch), acceptedAt).IsNew)
                            throw new OperationConflictException();
                        var saved = _batches.PublishInside(connection, transaction, child, batch, manifests);
                        AutomationRowWriter.Row(connection, transaction, "UPDATE main.operations SET status='completed' WHERE operation_id=$0;", child);
                        return saved;
                    });
                    if (OperatingSystem.IsWindows()) value.Secrets?.Publish(connection, transaction, child, acceptedAt);
                }
                if (OperatingSystem.IsWindows()) files?.RequireCurrent();
            }
        }
    }

    private static void RequireProject(SqliteConnection connection, SqliteTransaction transaction, AutomationCatalog before,
        AutomationChanges changes, DraftBindingIdentity owner, DraftProjectContract project,
        Dictionary<string, ExtensionInstalledMetadata?> installations)
    {
        var script = before.Scripts.Single(item => item.Id == owner.ScriptId);
        if (script.ExtensionId != project.Consumer.ExtensionId || script.RootId != project.RootId ||
            !before.Roots.Any(root => root.Id == project.RootId && root.Revision == project.RootRevision) ||
            changes.Roots.Any(root => root.Id == project.RootId) || changes.Scripts.Any(item => item.Id == owner.ScriptId) ||
            !Matches(project.Consumer) || !Matches(project.Provider))
            throw new ConfigurationException("project_selection_contract_changed");

        bool Matches(ExtensionInstalledMetadata expected)
        {
            if (!installations.TryGetValue(expected.ExtensionId, out var current))
                installations.Add(expected.ExtensionId, current = ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, expected.ExtensionId));
            return current is { IsInstalled: true } && current == expected;
        }
    }

    private Candidate[] RequireCandidates(IReadOnlyList<Guid> ids, string subject)
    {
        Expire();
        if (ids.Count is < 1 or > 32 || ids.Distinct().Count() != ids.Count)
            throw new ConfigurationException("configuration_draft_selection_invalid");
        var selected = ids.Select(id => _candidates.TryGetValue(id, out var value) && value.Subject == subject
            ? value : throw new ConfigurationException("configuration_draft_missing")).ToArray();
        var now = _clock.GetTimestamp();
        foreach (var value in selected) value.LastTouched = now;
        return selected;
    }

    private void Expire()
    {
        var now = _clock.GetTimestamp();
        foreach (var id in _candidates.Where(item => _clock.GetElapsedTime(item.Value.LastTouched, now) >= IdleLimit)
                     .Select(item => item.Key).ToArray())
            Remove(id);
    }

    private void Remove(Guid id)
    {
        if (_candidates.Remove(id, out var value)) value.Batch.Dispose();
    }

    public void Dispose()
    {
        _expiry.Dispose();
        lock (_gate)
        {
            foreach (var value in _candidates.Values) value.Batch.Dispose();
            _candidates.Clear();
        }
    }

    private sealed record Candidate(string Subject, Guid Session, string Digest, DraftBindingIdentity Owner, PreparedConfigurationBatch Batch,
        DraftProjectSecrets? Secrets, DraftProjectContract? Project, long CreatedAt)
    {
        internal long LastTouched { get; set; } = CreatedAt;
    }
}

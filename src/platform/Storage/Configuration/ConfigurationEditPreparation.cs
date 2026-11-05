using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed class ConfigurationEditPreparation(RuntimeInstance instance, StateDatabase database, ContractReader contracts, AdapterEditHook? adapter = null)
{
    internal PreparedConfigurationEdit Prepare(Guid sessionId, ConfigurationEditRequest request)
    {
        if (sessionId == Guid.Empty) throw new ConfigurationException("configuration_edit_session_invalid");
        request = ConfigurationEditPolicy.Freeze(request);
        ValidateTargets(instance, request);
        var plans = new List<PreparedConfigurationPlan>();
        var revisions = new ConfigurationRevisionStore(instance, database, contracts);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), blobs);
        try
        {
            foreach (var target in request.Targets)
            {
                var targetPath = ConfigurationTargetPaths.Canonical(target.TargetPath);
                var saved = revisions.Read(target.Address);
                if ((saved?.Revision ?? 0) != target.ExpectedRevision) throw new ConfigurationException("configuration_revision_conflict");
                if (saved is not null) ConfigurationRevisionPolicy.RequireCurrentContract(saved, target.Contract);
                var before = archive.Capture(targetPath, default);
                var tree = saved?.Tree ?? before.Snapshot;
                if (target.WritePolicy?.Adapter is { } rule)
                    tree = (adapter ?? throw new ConfigurationException("configuration_edit_adapter_unavailable"))
                        .Apply(sessionId, target.Address, target.ExpectedRevision, tree, rule, "editPrepare");
                var candidate = new ArchivedFileTree(tree, [], Array.AsReadOnly(tree.Entries.Where(entry => entry.Kind == FileObjectKind.File)
                    .Select(entry => new BlobReference($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!)).Distinct().ToArray()));
                var plan = new FrozenConfigurationExchange(instance.InstanceId, sessionId, Guid.NewGuid(), JournalKind.ConfigEdit,
                    targetPath, PhysicalObjectIdentity.Read(Path.GetDirectoryName(targetPath)!), before, candidate)
                    { EditPolicy = target.WritePolicy, Editor = request.Editor,
                        PhysicalClaims = request.Editor is null ? [] : PhysicalResourceAdmission.Read(target.TargetPath) };
                plans.Add(new ConfigurationPlanCodec(instance, contracts, blobs).Freeze(plan));
            }
            return new(instance, sessionId, request, plans);
        }
        catch { for (var index = plans.Count - 1; index >= 0; index--) plans[index].Dispose(); throw; }
    }

    internal static void ValidateTargets(RuntimeInstance instance, ConfigurationEditRequest request)
        => ConfigurationTargetPaths.Validate(instance, request.Targets);
}

using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Journals;

internal static class ConfigurationOutputEvidence
{
    internal static void Require(PreparedConfigurationExchange source, ArchivedFileTree output)
    {
        var plan = source.Plan;
        var policy = plan.EditPolicy ?? throw new IOException("configuration_editor_write_policy_required");
        var objects = output.OriginalObjects;
        FileTreeArchive.ValidateArchive(output); var canonical = FileTreePolicy.Freeze(output.Snapshot);
        if (!canonical.Entries.SequenceEqual(output.Snapshot.Entries) || objects.Count != canonical.Entries.Count ||
            !objects.Select(item => item.RelativePath).SequenceEqual(canonical.Entries.Select(item => item.RelativePath)) ||
            objects.Select(item => item.Identity).Distinct().Count() != objects.Count || objects.Any(item => item.Identity == source.WorkIdentity ||
                item.Identity.VolumeSerial != plan.ParentIdentity.VolumeSerial || plan.Before.OriginalObjects.Any(original => original.Identity == item.Identity)))
            throw new IOException("configuration_edit_output_objects_invalid");
        EditWriteRules.RequireOutput(policy, plan.Candidate.Snapshot, output.Snapshot);
        foreach (var original in source.CandidateObjects.Where(item => !EditWriteRules.Allows(policy, item.RelativePath)))
            if (!objects.Contains(original)) throw new IOException("configuration_edit_output_identity_scope_exceeded");
    }
}

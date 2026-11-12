using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal static class ExecutionHistoryInventory
{
    internal static void Verify(RuntimeInstance instance, StateDatabase database, ContractReader contracts, RegisteredJournal expected)
    {
        using var timing = StartupProfile.Measure("inventory.execution-history");
        using var paths = DirectoryAttributeScope.Hold(instance);
        using var blobs = BlobReadScope.Hold(instance);
        using var parents = JournalReadScope.HoldParents(instance, contracts);
        JournalReadScope.SelectParent(instance, contracts, expected.JournalId);
        var state = AttemptWriterState.Read(instance, database, contracts, expected.JournalId);
        var actual = state.Journal;
        if (actual.OperationId != expected.OperationId || actual.Kind != expected.Kind || actual.Phase != expected.Phase ||
            actual.Revision != expected.Revision || actual.PlanPath != expected.PlanPath || actual.PlanDigest != expected.PlanDigest ||
            !actual.ResourceKeys.SequenceEqual(expected.ResourceKeys)) throw new IOException("attempt_registration_changed_after_preflight");
        new JobClosureStore(instance, database, contracts).VerifyAttempt(state);
        LogCatalogInventory.VerifyAttempt(instance, database, contracts, state);
        ImageCatalogInventory.VerifyAttempt(instance, database, contracts, state);
    }
}
